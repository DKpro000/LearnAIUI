import json
import re
import time
from pathlib import Path
from typing import Any

import torch


BACKEND_DIR = Path(__file__).resolve().parent
SAVED_MODEL_DIR = BACKEND_DIR / "saved_models"
CHECKPOINT_DIR = SAVED_MODEL_DIR / "checkpoints"
REGISTRY_PATH = SAVED_MODEL_DIR / "checkpoint_registry.json"

SAVED_MODEL_DIR.mkdir(exist_ok=True)
CHECKPOINT_DIR.mkdir(exist_ok=True)


def sanitize_filename(text: str) -> str:
    text = text.strip()

    if text == "":
        text = "unnamed"

    text = re.sub(r"[^a-zA-Z0-9_\-]+", "_", text)
    text = text.strip("_")

    if text == "":
        text = "unnamed"

    return text[:80]


def load_checkpoint_registry() -> dict:
    if not REGISTRY_PATH.exists():
        return {"checkpoints": []}

    try:
        with open(REGISTRY_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)

        if "checkpoints" not in data or not isinstance(data["checkpoints"], list):
            return {"checkpoints": []}

        # Remove entries whose files are missing.
        valid = []
        for item in data["checkpoints"]:
            path = Path(item.get("checkpointPath", ""))
            if path.exists():
                valid.append(item)

        data["checkpoints"] = valid
        return data

    except Exception:
        return {"checkpoints": []}


def save_checkpoint_registry(registry: dict):
    with open(REGISTRY_PATH, "w", encoding="utf-8") as f:
        json.dump(registry, f, indent=2, ensure_ascii=False)


def build_graph_signature(graph: dict) -> dict:
    """
    Lightweight architecture signature.
    Used to warn the player if a weight is loaded into a different model.
    """
    nodes = []
    edges = []

    for node in graph.get("nodes", []):
        nodes.append({
            "definitionId": node.get("definitionId", ""),
            "title": node.get("title", ""),
            "symbol": node.get("symbol", ""),
            "nodeKind": node.get("nodeKind", ""),
            "parameters": [
                {
                    "key": p.get("key", ""),
                    "value": p.get("value", ""),
                }
                for p in node.get("parameters", [])
                if p.get("key", "") not in ["container_name"]
            ],
        })

    for edge in graph.get("edges", []):
        edges.append({
            "fromNodeId": edge.get("fromNodeId", ""),
            "fromPortName": edge.get("fromPortName", ""),
            "toNodeId": edge.get("toNodeId", ""),
            "toPortName": edge.get("toPortName", ""),
        })

    return {
        "nodes": nodes,
        "edgeCount": len(edges),
        "nodeCount": len(nodes),
    }


def save_named_checkpoint(
    model,
    graph: dict,
    dataset_name: str,
    input_shape: list[int],
    history: dict,
    num_classes: int,
    model_name: str,
    weight_name: str,
    extra_metadata: dict[str, Any] | None = None,
):
    timestamp = time.strftime("%Y%m%d_%H%M%S")

    model_name = model_name.strip() if model_name else "UnnamedModel"
    weight_name = weight_name.strip() if weight_name else f"{model_name}_{timestamp}"

    safe_model_name = sanitize_filename(model_name)
    safe_weight_name = sanitize_filename(weight_name)

    checkpoint_id = f"{safe_model_name}_{safe_weight_name}_{timestamp}"

    filename = f"{checkpoint_id}.pt"
    checkpoint_path = CHECKPOINT_DIR / filename

    metadata = {
        "checkpointId": checkpoint_id,
        "modelName": model_name,
        "weightName": weight_name,
        "datasetName": dataset_name,
        "inputShape": input_shape,
        "numClasses": num_classes,
        "savedAt": timestamp,
        "checkpointPath": str(checkpoint_path),
        "graphSignature": build_graph_signature(graph),
    }

    if extra_metadata:
        metadata.update(extra_metadata)

    checkpoint = {
        "model_state_dict": model.state_dict(),
        "metadata": metadata,
        "history": history,
    }

    torch.save(checkpoint, checkpoint_path)

    registry = load_checkpoint_registry()

    registry["checkpoints"].append(metadata)

    save_checkpoint_registry(registry)

    return metadata


def list_checkpoints(
    dataset_name: str | None = None,
    model_name: str | None = None,
):
    registry = load_checkpoint_registry()
    checkpoints = registry.get("checkpoints", [])

    result = []

    for item in checkpoints:
        if dataset_name and item.get("datasetName") != dataset_name:
            continue

        if model_name and item.get("modelName") != model_name:
            continue

        result.append(item)

    # Newest first.
    result.sort(key=lambda x: x.get("savedAt", ""), reverse=True)

    return result


def get_checkpoint_metadata(checkpoint_id: str):
    checkpoints = list_checkpoints()

    for item in checkpoints:
        if item.get("checkpointId") == checkpoint_id:
            return item

    raise RuntimeError(f"Checkpoint not found: {checkpoint_id}")


def resolve_checkpoint_path_by_id(checkpoint_id: str):
    metadata = get_checkpoint_metadata(checkpoint_id)

    path = Path(metadata.get("checkpointPath", ""))

    if not path.exists():
        raise RuntimeError(f"Checkpoint file not found: {path}")

    return path, metadata


def delete_checkpoint(checkpoint_id: str):
    registry = load_checkpoint_registry()
    checkpoints = registry.get("checkpoints", [])

    kept = []
    deleted = None

    for item in checkpoints:
        if item.get("checkpointId") == checkpoint_id:
            deleted = item
        else:
            kept.append(item)

    if deleted is None:
        raise RuntimeError(f"Checkpoint not found: {checkpoint_id}")

    path = Path(deleted.get("checkpointPath", ""))

    if path.exists():
        path.unlink()

    registry["checkpoints"] = kept
    save_checkpoint_registry(registry)

    return deleted