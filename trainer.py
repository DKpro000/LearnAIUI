import os
import traceback
import time
from pathlib import Path
import torch
import torch.nn as nn
from torch.utils.data import DataLoader, Subset, random_split

from graph_validator import validate_graph_payload
from model_builder import GeneratedGraphModel, get_topological_order, get_input_shape
from local_datasets import build_local_dataset
from checkpoint_manager import (
    save_named_checkpoint,
    resolve_checkpoint_path_by_id,
    build_graph_signature,
)

BACKEND_DIR = Path(__file__).resolve().parent
RUNTIME_DIR = Path(os.environ.get("NN_BUILDER_RUNTIME_DIR", BACKEND_DIR))
DATA_ROOT = Path(os.environ.get("NN_BUILDER_DATA_DIR", RUNTIME_DIR / "data"))
SAVED_MODEL_DIR = RUNTIME_DIR / "saved_models"
DATA_ROOT.mkdir(parents=True, exist_ok=True)
SAVED_MODEL_DIR.mkdir(exist_ok=True)

def train_graph(payload: dict, owner_player_id: str | None = None) -> dict:
    try:
        graph = payload.get("graph")
        training = payload.get("training", {})

        model_name = training.get("modelName", "UnnamedModel")
        weight_name = training.get("weightName", "")

        if graph is None:
            return fail_response("Missing graph field.")

        dataset_name = get_dataset_name_from_graph(graph)

        if not dataset_name:
            dataset_name = training.get("dataset", "MNIST")
        epochs = int(training.get("epochs", 1))
        batch_size = int(training.get("batchSize", 64))
        learning_rate = float(training.get("learningRate", 0.001))
        optimizer_name = training.get("optimizer", "Adam")
        loss_name = training.get("loss", "CrossEntropyLoss")
        max_train_samples = int(training.get("maxTrainSamples", 2000))
        device_name = training.get("device", "auto")

        device = choose_device(device_name)

        execution_order = get_topological_order(graph)
        model = GeneratedGraphModel(graph, execution_order)
        model.to(device)

        input_shape = get_input_shape(graph)

        train_loader, final_loader, num_classes = build_train_final_dataloaders(
            dataset_name=dataset_name,
            batch_size=batch_size,
            max_train_samples=max_train_samples,
        )

        criterion = build_loss(loss_name)
        optimizer = build_optimizer(
            optimizer_name=optimizer_name,
            model=model,
            learning_rate=learning_rate,
        )

        history = {
            "trainLoss": [],
            "trainAcc": [],
        }

        warnings = []
        model_output_name = get_model_output_name(graph)

        if loss_name == "CrossEntropyLoss" and model_output_name != "logits":
            warnings.append(
                "CrossEntropyLoss expects raw logits. Current Model Output is "
                f"'{model_output_name}'. Training will still use the raw model tensor."
            )

        for epoch in range(epochs):
            model.train()

            total_loss = 0.0
            total_correct = 0
            total_samples = 0

            for batch_x, batch_y in train_loader:
                batch_x = prepare_input_for_graph(batch_x, input_shape)
                batch_x = batch_x.to(device)
                batch_y = batch_y.to(device)

                optimizer.zero_grad()

                logits = model(batch_x)
                loss = criterion(logits, batch_y)

                loss.backward()
                optimizer.step()

                batch_size_actual = batch_y.size(0)

                total_loss += loss.item() * batch_size_actual

                preds = torch.argmax(logits, dim=1)
                total_correct += (preds == batch_y).sum().item()
                total_samples += batch_size_actual

            avg_loss = total_loss / max(total_samples, 1)
            avg_acc = total_correct / max(total_samples, 1)

            history["trainLoss"].append(avg_loss)
            history["trainAcc"].append(avg_acc)
        
        eval_metrics = evaluate_model(
            model=model,
            data_loader=train_loader,
            input_shape=input_shape,
            device=device,
            num_classes=num_classes,
        )

        result_nodes = build_result_node_outputs(
            graph=graph,
            history=history,
            eval_metrics=eval_metrics,
        )

        checkpoint_metadata = save_named_checkpoint(
            model=model,
            graph=graph,
            dataset_name=dataset_name,
            input_shape=input_shape,
            history=history,
            num_classes=num_classes,
            model_name=model_name,
            weight_name=weight_name,
            extra_metadata={
                "epochs": epochs,
                "optimizer": optimizer_name,
                "loss": loss_name,
                "ownerPlayerId": owner_player_id or "",
            },
        )

        checkpoint_path = checkpoint_metadata["checkpointPath"]

        model_output_results = build_model_output_node_results(
            graph=graph,
            model=model,
            data_loader=train_loader,
            input_shape=input_shape,
            device=device,
            history=history,
            num_classes=num_classes,
            phase_name="Training Data",
            eval_metrics=eval_metrics,
        )

        return {
            "success": True,
            "errors": [],
            "warnings": warnings,
            "history": history,
            "resultNodes": model_output_results,
            "checkpointId": checkpoint_metadata["checkpointId"],
            "checkpointPath": checkpoint_metadata["checkpointPath"],
            "checkpointMetadata": checkpoint_metadata,
            "numClasses": num_classes,
            "modelSummary": str(model),
            "device": str(device),
            "dataset": dataset_name,
            "epochs": epochs,
        }

    except Exception as e:
        return {
            "success": False,
            "errors": [
                str(e),
                traceback.format_exc(),
            ],
            "warnings": warnings,
            "history": {
                "trainLoss": [],
                "trainAcc": [],
            },
            "resultNodes": [],
            "checkpointId": "",
            "checkpointPath": "",
            "checkpointMetadata": {},
            "numClasses": 0,
            "modelSummary": "",
            "device": "",
            "dataset": "",
            "epochs": 0,
        }


def fail_response(message: str) -> dict:
    return {
        "success": False,
        "errors": [message],
        "warnings": [],
        "history": {
            "trainLoss": [],
            "trainAcc": [],
        },
        "modelSummary": "",
        "device": "",
        "dataset": "",
        "epochs": 0,
    }


def choose_device(device_name: str):
    if device_name == "auto":
        return torch.device("cuda" if torch.cuda.is_available() else "cpu")

    return torch.device(device_name)


def build_dataloader(dataset_name: str, batch_size: int, max_train_samples: int):
    try:
        from torchvision import datasets, transforms
    except Exception:
        raise RuntimeError(
            "torchvision is not installed. Run: pip install torchvision"
        )

    transform = transforms.ToTensor()

    if dataset_name == "MNIST":
        dataset = datasets.MNIST(
            root=str(DATA_ROOT),
            train=True,
            download=True,
            transform=transform,
        )

    elif dataset_name == "FashionMNIST":
        dataset = datasets.FashionMNIST(
            root=str(DATA_ROOT),
            train=True,
            download=True,
            transform=transform,
        )

    elif dataset_name == "CIFAR10":
        dataset = datasets.CIFAR10(
            root=str(DATA_ROOT),
            train=True,
            download=True,
            transform=transform,
        )

    elif dataset_name in ["ChihuahuaMuffin", "Titanic", "WeatherPrediction"]:
        dataset = build_local_dataset(dataset_name)

    else:
        raise RuntimeError(f"Unsupported dataset: {dataset_name}")

    if max_train_samples > 0:
        count = min(max_train_samples, len(dataset))
        dataset = Subset(dataset, list(range(count)))

    return DataLoader(
        dataset,
        batch_size=batch_size,
        shuffle=True,
    )


def prepare_input_for_graph(batch_x, input_shape: list[int]):
    """
    Supports:
    MLP input: [1, features]
    CNN input: [1, C, H, W]
    """
    if len(input_shape) == 2:
        return batch_x.view(batch_x.size(0), -1)

    if len(input_shape) == 4:
        return batch_x

    raise RuntimeError(
        f"Unsupported input shape for training: {input_shape}. "
        "Use [1, features] for MLP or [1, C, H, W] for CNN."
    )


def build_loss(loss_name: str):
    if loss_name == "CrossEntropyLoss":
        return nn.CrossEntropyLoss()

    if loss_name == "MSELoss":
        return nn.MSELoss()

    raise RuntimeError(f"Unsupported loss for now: {loss_name}")


def build_optimizer(optimizer_name: str, model: nn.Module, learning_rate: float):
    if optimizer_name == "Adam":
        return torch.optim.Adam(model.parameters(), lr=learning_rate)

    if optimizer_name == "SGD":
        return torch.optim.SGD(model.parameters(), lr=learning_rate)

    raise RuntimeError(f"Unsupported optimizer for now: {optimizer_name}")

def get_dataset_name_from_graph(graph: dict) -> str:
    for node in graph.get("nodes", []):
        if node.get("nodeKind") == "DatasetNode":
            params = {}
            for param in node.get("parameters", []):
                params[param.get("key", "")] = param.get("value", "")

            dataset_name = params.get("dataset_name", "")
            if dataset_name:
                return dataset_name

        inner_graph = node.get("innerGraph")
        if inner_graph is not None:
            inner_dataset = get_dataset_name_from_graph(inner_graph)
            if inner_dataset:
                return inner_dataset

    return ""

def find_result_output_nodes(graph: dict) -> list[dict]:
    result_nodes = []

    for node in graph.get("nodes", []):
        if node.get("nodeKind") == "ResultOutputNode":
            result_nodes.append(node)

        inner_graph = node.get("innerGraph")
        if inner_graph is not None:
            result_nodes.extend(find_result_output_nodes(inner_graph))

    return result_nodes

def params_to_dict_local(parameters: list[dict]) -> dict:
    result = {}

    for param in parameters:
        key = param.get("key", "")
        value = param.get("value", "")

        if key:
            result[key] = value

    return result

def evaluate_model(model, data_loader, input_shape, device, num_classes):
    model.eval()

    total_correct = 0
    total_samples = 0

    confusion = torch.zeros(num_classes, num_classes, dtype=torch.int64)

    with torch.no_grad():
        for batch_x, batch_y in data_loader:
            batch_x = prepare_input_for_graph(batch_x, input_shape)
            batch_x = batch_x.to(device)
            batch_y = batch_y.to(device)

            logits = model(batch_x)

            if logits.dim() != 2:
                raise RuntimeError(
                    "Model output must have shape [batch, classes] during evaluation. "
                    f"Received {list(logits.shape)}."
                )

            if logits.size(0) != batch_y.numel():
                raise RuntimeError(
                    "Model output batch size does not match the target batch size. "
                    f"Received {logits.size(0)} predictions for {batch_y.numel()} targets."
                )

            if logits.size(1) != num_classes:
                raise RuntimeError(
                    "Model output class count does not match the evaluation dataset. "
                    f"Expected {num_classes}, received {logits.size(1)}."
                )

            preds = torch.argmax(logits, dim=1)

            total_correct += (preds == batch_y).sum().item()
            total_samples += batch_y.size(0)

            targets = batch_y.view(-1).to(dtype=torch.int64)

            if torch.any(targets < 0) or torch.any(targets >= num_classes):
                raise RuntimeError(
                    "Evaluation dataset contains a target outside the configured class range."
                )

            batch_confusion = torch.bincount(
                (targets * num_classes + preds.view(-1)).detach().cpu(),
                minlength=num_classes * num_classes,
            ).reshape(num_classes, num_classes)
            confusion += batch_confusion

    accuracy = total_correct / max(total_samples, 1)
    f1_macro = compute_macro_f1(confusion)

    return {
        "accuracy": accuracy,
        "f1_macro": f1_macro,
        "confusion_matrix": confusion.tolist(),
        "total_samples": total_samples,
    }

def find_root_model_output_nodes(graph: dict) -> list[dict]:
    """
    Only root-level OutputNode is treated as visible model output display node.
    Inner container OutputNode is used for container ports and should not normally
    display final metrics on the root screen.
    """
    result = []

    for node in graph.get("nodes", []):
        if node.get("nodeKind") == "OutputNode":
            result.append(node)

    return result


def compute_macro_f1(confusion):
    num_classes = confusion.size(0)
    f1_values = []

    for c in range(num_classes):
        tp = confusion[c, c].item()
        fp = confusion[:, c].sum().item() - tp
        fn = confusion[c, :].sum().item() - tp

        precision = tp / max(tp + fp, 1)
        recall = tp / max(tp + fn, 1)

        if precision + recall == 0:
            f1 = 0.0
        else:
            f1 = 2 * precision * recall / (precision + recall)

        f1_values.append(f1)

    return sum(f1_values) / max(len(f1_values), 1)

def build_result_node_outputs(graph: dict, history: dict, eval_metrics: dict) -> list[dict]:
    result_nodes = find_result_output_nodes(graph)
    outputs = []

    for node in result_nodes:
        params = params_to_dict_local(node.get("parameters", []))
        result_type = params.get("result_type", "Accuracy")

        text = ""
        data = {}

        if result_type == "Accuracy":
            acc = eval_metrics.get("accuracy", 0.0)
            text = f"Accuracy: {acc:.4f}"
            data = {"accuracy": acc}

        elif result_type == "F1 Score":
            f1 = eval_metrics.get("f1_macro", 0.0)
            text = f"F1 Score: {f1:.4f}"
            data = {"f1_macro": f1}

        elif result_type == "Confusion Matrix":
            matrix = eval_metrics.get("confusion_matrix", [])
            text = format_confusion_matrix(matrix)
            data = {"confusion_matrix": matrix}

        elif result_type == "Loss Graph":
            losses = history.get("trainLoss", [])
            text = format_loss_graph_text(losses)
            data = {"trainLoss": losses}

        elif result_type == "Training Loss":
            losses = history.get("trainLoss", [])
            last_loss = losses[-1] if len(losses) > 0 else 0.0
            text = f"Training Loss: {last_loss:.4f}"
            data = {"trainLoss": losses}

        elif result_type == "Training Accuracy":
            accs = history.get("trainAcc", [])
            last_acc = accs[-1] if len(accs) > 0 else 0.0
            text = f"Training Accuracy: {last_acc:.4f}"
            data = {"trainAcc": accs}

        else:
            text = f"Unsupported result type: {result_type}"

        outputs.append(
            {
                "nodeId": node.get("nodeId", ""),
                "title": node.get("title", ""),
                "resultType": result_type,
                "text": text,
                "data": data,
            }
        )

    return outputs


def format_loss_graph_text(losses: list[float]) -> str:
    if len(losses) == 0:
        return "Loss Graph: no data"

    lines = ["Loss Graph"]

    for i, loss in enumerate(losses):
        lines.append(f"Epoch {i + 1}: {loss:.4f}")

    return "\n".join(lines)


def format_confusion_matrix(matrix: list[list[int]]) -> str:
    if len(matrix) == 0:
        return "Confusion Matrix: no data"

    lines = ["Confusion Matrix"]

    max_rows = min(len(matrix), 10)

    for i in range(max_rows):
        row = matrix[i]
        short_row = row[:10]
        lines.append(str(short_row))

    return "\n".join(lines)

def get_model_output_name(graph: dict) -> str:
    for node in graph.get("nodes", []):
        if node.get("nodeKind") == "OutputNode":
            for param in node.get("parameters", []):
                if param.get("key") == "output_name":
                    return param.get("value", "logits")

        inner_graph = node.get("innerGraph")
        if inner_graph is not None:
            value = get_model_output_name(inner_graph)
            if value:
                return value

    return "logits"

def build_dataset_by_name(dataset_name: str, train: bool = True):
    try:
        from torchvision import datasets, transforms
    except Exception:
        raise RuntimeError(
            "torchvision is not installed. Run: pip install torchvision"
        )

    transform = transforms.ToTensor()

    if dataset_name == "MNIST":
        return datasets.MNIST(
            root=str(DATA_ROOT),
            train=train,
            download=True,
            transform=transform,
        )

    if dataset_name == "FashionMNIST":
        return datasets.FashionMNIST(
            root=str(DATA_ROOT),
            train=train,
            download=True,
            transform=transform,
        )

    if dataset_name == "CIFAR10":
        return datasets.CIFAR10(
            root=str(DATA_ROOT),
            train=train,
            download=True,
            transform=transform,
        )

    if dataset_name in ["ChihuahuaMuffin", "Titanic", "WeatherPrediction"]:
        return build_local_dataset(dataset_name)

    raise RuntimeError(f"Unsupported dataset: {dataset_name}")


def get_num_classes_from_dataset(dataset):
    base_dataset = dataset

    while isinstance(base_dataset, Subset):
        base_dataset = base_dataset.dataset

    if hasattr(base_dataset, "classes"):
        return len(base_dataset.classes)

    if hasattr(base_dataset, "num_classes"):
        return int(base_dataset.num_classes)

    labels = []

    for _, y in dataset:
        labels.append(int(y))

    if len(labels) == 0:
        return 0

    return max(labels) + 1


def build_final_dataloader(
    dataset_name: str,
    batch_size: int,
    final_ratio: float = 0.2,
):
    """Build only the held-out loader used for final/leaderboard evaluation."""
    if dataset_name in ["MNIST", "FashionMNIST", "CIFAR10"]:
        final_dataset = build_dataset_by_name(dataset_name, train=False)
    else:
        full_dataset = build_dataset_by_name(dataset_name, train=True)
        final_size = int(len(full_dataset) * final_ratio)
        train_size = len(full_dataset) - final_size

        if final_size <= 0:
            raise RuntimeError(
                f"Dataset {dataset_name} is too small to create final split."
            )

        generator = torch.Generator().manual_seed(42)
        _, final_dataset = random_split(
            full_dataset,
            [train_size, final_size],
            generator=generator,
        )

    num_classes = get_num_classes_from_dataset(final_dataset)
    final_loader = DataLoader(
        final_dataset,
        batch_size=batch_size,
        shuffle=False,
    )
    return final_loader, num_classes


def build_train_final_dataloaders(
    dataset_name: str,
    batch_size: int,
    max_train_samples: int,
    final_ratio: float = 0.2,
):
    """
    Returns:
    train_loader: used for training
    final_loader: never used for training
    num_classes
    """

    # Built-in torchvision datasets already have official train/test splits.
    if dataset_name in ["MNIST", "FashionMNIST", "CIFAR10"]:
        train_dataset = build_dataset_by_name(dataset_name, train=True)
        final_dataset = build_dataset_by_name(dataset_name, train=False)

    # Local datasets may not have official split, so we create an 80/20 split.
    else:
        full_dataset = build_dataset_by_name(dataset_name, train=True)

        final_size = int(len(full_dataset) * final_ratio)
        train_size = len(full_dataset) - final_size

        if final_size <= 0:
            raise RuntimeError(
                f"Dataset {dataset_name} is too small to create final split."
            )

        generator = torch.Generator().manual_seed(42)

        train_dataset, final_dataset = random_split(
            full_dataset,
            [train_size, final_size],
            generator=generator,
        )

    # maxTrainSamples should only limit training data.
    # It must not consume final/test data.
    if max_train_samples > 0:
        count = min(max_train_samples, len(train_dataset))
        train_dataset = Subset(train_dataset, list(range(count)))

    num_classes = get_num_classes_from_dataset(final_dataset)

    train_loader = DataLoader(
        train_dataset,
        batch_size=batch_size,
        shuffle=True,
    )

    final_loader = DataLoader(
        final_dataset,
        batch_size=batch_size,
        shuffle=False,
    )

    return train_loader, final_loader, num_classes

def save_model_checkpoint(
    model,
    graph: dict,
    dataset_name: str,
    input_shape: list[int],
    history: dict,
    num_classes: int,
):
    timestamp = time.strftime("%Y%m%d_%H%M%S")
    filename = f"checkpoint_{dataset_name}_{timestamp}.pt"
    checkpoint_path = SAVED_MODEL_DIR / filename

    checkpoint = {
        "model_state_dict": model.state_dict(),
        "dataset_name": dataset_name,
        "input_shape": input_shape,
        "history": history,
        "num_classes": num_classes,
        "saved_at": timestamp,
    }

    torch.save(checkpoint, checkpoint_path)

    latest_path = SAVED_MODEL_DIR / "latest_checkpoint.txt"
    latest_path.write_text(str(checkpoint_path), encoding="utf-8")

    return str(checkpoint_path)


def resolve_checkpoint_path(checkpoint_path: str):
    if checkpoint_path is not None and checkpoint_path.strip() != "":
        path = Path(checkpoint_path)

        if not path.exists():
            raise RuntimeError(f"Checkpoint file not found: {path}")

        return path

    latest_path = SAVED_MODEL_DIR / "latest_checkpoint.txt"

    if not latest_path.exists():
        raise RuntimeError(
            "No checkpoint path was provided and no latest checkpoint exists. "
            "Please train and save weights first."
        )

    path = Path(latest_path.read_text(encoding="utf-8").strip())

    if not path.exists():
        raise RuntimeError(f"Latest checkpoint file not found: {path}")

    return path

def params_to_dict_local(parameters: list[dict]) -> dict:
    result = {}

    for param in parameters:
        key = param.get("key", "")
        value = param.get("value", "")

        if key:
            result[key] = value

    return result


def find_final_result_nodes(graph: dict) -> list[dict]:
    nodes = []

    for node in graph.get("nodes", []):
        if node.get("nodeKind") == "FinalResultNode":
            nodes.append(node)

        inner_graph = node.get("innerGraph")
        if inner_graph is not None:
            nodes.extend(find_final_result_nodes(inner_graph))

    return nodes


def build_final_result_node_outputs(graph: dict, eval_metrics: dict) -> list[dict]:
    final_nodes = find_final_result_nodes(graph)
    outputs = []

    for node in final_nodes:
        params = params_to_dict_local(node.get("parameters", []))
        result_type = params.get("result_type", "All Metrics")

        accuracy = eval_metrics.get("accuracy", 0.0)
        f1 = eval_metrics.get("f1_macro", 0.0)
        matrix = eval_metrics.get("confusion_matrix", [])
        total_samples = eval_metrics.get("total_samples", 0)

        if result_type == "Accuracy":
            text = f"Final Accuracy: {accuracy:.4f}\nSamples: {total_samples}"
            data = {"accuracy": accuracy, "total_samples": total_samples}

        elif result_type == "F1 Score":
            text = f"Final F1 Score: {f1:.4f}\nSamples: {total_samples}"
            data = {"f1_macro": f1, "total_samples": total_samples}

        elif result_type == "Confusion Matrix":
            text = format_confusion_matrix(matrix)
            data = {"confusion_matrix": matrix}

        else:
            text = (
                f"Final Evaluation\n"
                f"Accuracy: {accuracy:.4f}\n"
                f"F1 Score: {f1:.4f}\n"
                f"Samples: {total_samples}\n\n"
                f"{format_confusion_matrix(matrix)}"
            )
            data = {
                "accuracy": accuracy,
                "f1_macro": f1,
                "confusion_matrix": matrix,
                "total_samples": total_samples,
            }

        outputs.append(
            {
                "nodeId": node.get("nodeId", ""),
                "title": node.get("title", ""),
                "resultType": result_type,
                "text": text,
                "data": data,
            }
        )

    return outputs


def format_confusion_matrix(matrix: list[list[int]]) -> str:
    if len(matrix) == 0:
        return "Confusion Matrix: no data"

    lines = ["Confusion Matrix"]

    max_rows = min(len(matrix), 10)

    for i in range(max_rows):
        row = matrix[i]
        lines.append(str(row[:10]))

    return "\n".join(lines)

def build_model_from_graph(graph: dict):
    validation = validate_graph_payload({"graph": graph})

    if not validation.get("success", False):
        errors = validation.get("errors", [])
        raise RuntimeError("Graph validation failed: " + "; ".join(errors))

    execution_order_items = validation.get("executionOrder", [])
    execution_order = []

    for item in execution_order_items:
        if isinstance(item, dict):
            execution_order.append(item.get("nodeId"))
        else:
            execution_order.append(item)

    execution_order = [x for x in execution_order if x]

    model = GeneratedGraphModel(
        graph=graph,
        execution_order=execution_order,
    )

    return model

def load_checkpoint_weights_flexible(model, saved_state_dict):
    """
    First try strict load.
    If node UUID changed, remap checkpoint weights by parameter order and shape.
    This fixes old checkpoints whose state_dict keys contain nodeId UUIDs.
    """
    try:
        model.load_state_dict(saved_state_dict, strict=True)
        return {
            "success": True,
            "warnings": [],
            "mode": "strict",
        }

    except Exception as strict_error:
        current_state = model.state_dict()

        saved_items = list(saved_state_dict.items())
        current_items = list(current_state.items())

        if len(saved_items) != len(current_items):
            raise RuntimeError(
                "Failed to load checkpoint strictly, and flexible remap also failed "
                "because parameter count is different.\n\n"
                f"Strict load error:\n{strict_error}"
            )

        remapped_state = {}

        for index, ((current_key, current_tensor), (saved_key, saved_tensor)) in enumerate(
            zip(current_items, saved_items)
        ):
            if tuple(current_tensor.shape) != tuple(saved_tensor.shape):
                raise RuntimeError(
                    "Failed to load checkpoint strictly, and flexible remap also failed "
                    "because parameter shapes are different.\n\n"
                    f"At parameter index {index}:\n"
                    f"Current key: {current_key}, shape={tuple(current_tensor.shape)}\n"
                    f"Saved key: {saved_key}, shape={tuple(saved_tensor.shape)}\n\n"
                    f"Strict load error:\n{strict_error}"
                )

            remapped_state[current_key] = saved_tensor

        model.load_state_dict(remapped_state, strict=True)

        return {
            "success": True,
            "warnings": [
                "Checkpoint was loaded by parameter order and shape because node IDs changed. "
                "This usually means the model structure is the same, but node UUIDs are different."
            ],
            "mode": "remapped_by_order",
        }

def final_evaluate_graph(
    payload: dict,
    leaderboard_mode: bool = False,
    owner_player_id: str | None = None,
):
    try:
        graph = payload.get("graph", {})
        checkpoint_id = payload.get("checkpointId", "")
        training = payload.get("training", {})

        batch_size = 64 if leaderboard_mode else int(training.get("batchSize", 64))
        device_name = "auto" if leaderboard_mode else training.get("device", "auto")

        if batch_size < 1 or batch_size > 512:
            raise RuntimeError("Evaluation batchSize must be between 1 and 512.")

        dataset_name = get_dataset_name_from_graph(graph)

        if not dataset_name:
            dataset_name = training.get("dataset", "MNIST")

        input_shape = get_input_shape(graph)

        device = choose_device(device_name)

        if not checkpoint_id:
            raise RuntimeError(
                "No checkpoint selected. Please choose a saved weight before final evaluation."
            )

        checkpoint_path, checkpoint_metadata = resolve_checkpoint_path_by_id(checkpoint_id)

        if leaderboard_mode:
            checkpoint_owner = checkpoint_metadata.get("ownerPlayerId", "")
            if not checkpoint_owner or checkpoint_owner != owner_player_id:
                raise RuntimeError(
                    "Only the player who trained this weight can submit its leaderboard score."
                )

        saved_dataset_name = checkpoint_metadata.get("datasetName", "")
        if not saved_dataset_name:
            raise RuntimeError("Selected checkpoint does not contain a dataset name.")

        if dataset_name != saved_dataset_name:
            raise RuntimeError(
                "The selected checkpoint belongs to a different dataset. "
                f"Expected {saved_dataset_name}, received {dataset_name}."
            )

        dataset_name = saved_dataset_name

        checkpoint = torch.load(
            checkpoint_path,
            map_location=device,
            weights_only=True,
        )

        warnings = []

        current_signature = build_graph_signature(graph)
        saved_signature = checkpoint_metadata.get("graphSignature", {})

        current_graph_hash = current_signature.get("graphHash", "")
        saved_graph_hash = saved_signature.get("graphHash", "")

        if leaderboard_mode:
            if not saved_graph_hash:
                raise RuntimeError(
                    "This weight was saved before verified leaderboard support. "
                    "Train it again before submitting a score."
                )

            if current_graph_hash != saved_graph_hash:
                raise RuntimeError(
                    "The graph does not exactly match the graph used to train this weight."
                )

        if current_signature.get("nodeCount") != saved_signature.get("nodeCount"):
            warnings.append(
                "The selected checkpoint may not match the current model structure. "
                "Node count is different."
            )

        model = build_model_from_graph(graph)
        model.to(device)

        if leaderboard_mode:
            model.load_state_dict(checkpoint["model_state_dict"], strict=True)
            load_result = {"warnings": [], "mode": "strict"}
        else:
            load_result = load_checkpoint_weights_flexible(
                model,
                checkpoint["model_state_dict"],
            )

        warnings.extend(load_result.get("warnings", []))

        final_loader, num_classes = build_final_dataloader(
            dataset_name=dataset_name,
            batch_size=batch_size,
        )

        eval_metrics = evaluate_model(
            model=model,
            data_loader=final_loader,
            input_shape=input_shape,
            device=device,
            num_classes=num_classes,
        )

        model_output_results = build_model_output_node_results(
            graph=graph,
            model=model,
            data_loader=final_loader,
            input_shape=input_shape,
            device=device,
            history=checkpoint.get("history", {}),
            num_classes=num_classes,
            phase_name="Final Test Data",
            eval_metrics=eval_metrics,
        )

        return {
            "success": True,
            "errors": [],
            "warnings": warnings,
            "checkpointId": checkpoint_id,
            "checkpointPath": str(checkpoint_path),
            "checkpointMetadata": checkpoint_metadata,
            "dataset": dataset_name,
            "numClasses": num_classes,
            "finalMetrics": eval_metrics,
            "finalResultNodes": model_output_results,
        }

    except Exception as e:
        return {
            "success": False,
            "errors": [str(e)],
            "warnings": [],
            "checkpointId": "",
            "checkpointMetadata": {},
            "checkpointPath": "",
            "dataset": "",
            "numClasses": 0,
            "finalMetrics": {},
            "finalResultNodes": [],
        }

def collect_model_preview(model, data_loader, input_shape, device, max_items=5):
    model.eval()

    with torch.no_grad():
        for batch_x, batch_y in data_loader:
            batch_x = prepare_input_for_graph(batch_x, input_shape)
            batch_x = batch_x.to(device)
            batch_y = batch_y.to(device)

            logits = model(batch_x)

            if logits.dim() == 1:
                logits = logits.unsqueeze(0)

            probabilities = torch.softmax(logits, dim=1)
            predicted_class = torch.argmax(logits, dim=1)

            count = min(max_items, logits.size(0))

            return {
                "logits_shape": list(logits.shape),
                "logits": logits[:count].detach().cpu().tolist(),
                "probabilities": probabilities[:count].detach().cpu().tolist(),
                "predicted_class": predicted_class[:count].detach().cpu().tolist(),
                "target": batch_y[:count].detach().cpu().tolist(),
            }

    return {
        "logits_shape": [],
        "logits": [],
        "probabilities": [],
        "predicted_class": [],
        "target": [],
    }

def short_number(x):
    try:
        return round(float(x), 4)
    except Exception:
        return x


def short_nested_list(values, max_rows=5, max_cols=10):
    if values is None:
        return "[]"

    if not isinstance(values, list):
        return str(values)

    lines = []

    for row in values[:max_rows]:
        if isinstance(row, list):
            short_row = [short_number(v) for v in row[:max_cols]]
            lines.append(str(short_row))
        else:
            lines.append(str(short_number(row)))

    return "\n".join(lines)

def build_model_output_node_results(
    graph: dict,
    model,
    data_loader,
    input_shape,
    device,
    history: dict,
    num_classes: int,
    phase_name: str,
    eval_metrics: dict | None = None,
    preview: dict | None = None,
):
    """
    Build display text for each root-level Model Output node.
    These results will be written back to the corresponding Unity node.
    """

    output_nodes = find_root_model_output_nodes(graph)

    if len(output_nodes) == 0:
        return []

    if eval_metrics is None:
        eval_metrics = evaluate_model(
            model=model,
            data_loader=data_loader,
            input_shape=input_shape,
            device=device,
            num_classes=num_classes,
        )

    if preview is None:
        preview = collect_model_preview(
            model=model,
            data_loader=data_loader,
            input_shape=input_shape,
            device=device,
            max_items=5,
        )

    outputs = []

    for node in output_nodes:
        params = params_to_dict_local(node.get("parameters", []))
        output_name = params.get("output_name", "Accuracy")

        key = output_name.strip().lower().replace(" ", "_")

        accuracy = eval_metrics.get("accuracy", 0.0)
        f1 = eval_metrics.get("f1_macro", 0.0)
        matrix = eval_metrics.get("confusion_matrix", [])
        total_samples = eval_metrics.get("total_samples", 0)

        if key == "accuracy":
            text = (
                f"{phase_name} Accuracy\n"
                f"Accuracy: {accuracy:.4f}\n"
                f"Samples: {total_samples}"
            )

        elif key in ["f1_score", "f1"]:
            text = (
                f"{phase_name} F1 Score\n"
                f"Macro F1: {f1:.4f}\n"
                f"Samples: {total_samples}"
            )

        elif key == "confusion_matrix":
            text = (
                f"{phase_name}\n"
                f"{format_confusion_matrix(matrix)}"
            )

        elif key == "loss_graph":
            losses = history.get("trainLoss", [])

            if len(losses) == 0:
                text = "Loss Graph\nNo loss history yet."
            else:
                lines = ["Loss Graph"]
                for i, loss in enumerate(losses):
                    lines.append(f"Epoch {i + 1}: {loss:.4f}")
                text = "\n".join(lines)

        elif key == "logits":
            text = (
                f"{phase_name} Logits\n"
                f"Shape: {preview.get('logits_shape', [])}\n"
                f"First samples:\n"
                f"{short_nested_list(preview.get('logits', []))}"
            )

        elif key == "probabilities":
            text = (
                f"{phase_name} Probabilities\n"
                f"First samples:\n"
                f"{short_nested_list(preview.get('probabilities', []))}"
            )

        elif key == "predicted_class":
            text = (
                f"{phase_name} Predicted Class\n"
                f"Pred: {preview.get('predicted_class', [])}\n"
                f"True: {preview.get('target', [])}\n"
                f"Accuracy: {accuracy:.4f}"
            )

        elif key in ["features", "embedding"]:
            text = (
                f"{phase_name} {output_name}\n"
                f"Currently this node is connected to final model output.\n"
                f"Shape: {preview.get('logits_shape', [])}\n"
                f"Preview:\n"
                f"{short_nested_list(preview.get('logits', []))}\n\n"
                f"To show real intermediate features later, we need a Feature Tap node."
            )

        else:
            text = (
                f"Unsupported output_name: {output_name}\n"
                f"Available: Accuracy, F1 Score, Confusion Matrix, Loss Graph, "
                f"Logits, Probabilities, Predicted Class."
            )

        outputs.append(
            {
                "nodeId": node.get("nodeId", ""),
                "title": node.get("title", ""),
                "resultType": output_name,
                "text": text,
                "data": {},
            }
        )

    return outputs
