"""Coordinator for server fallback and opt-in player-computer training workers."""

from __future__ import annotations

import copy
import io
import json
import math
import secrets
import threading

import torch

from checkpoint_manager import save_received_checkpoint
from compute_store import ComputeStore
from graph_validator import validate_graph_payload
from leaderboard_store import ValidationError
from model_builder import (
    GeneratedGraphModel,
    get_input_shape,
    get_topological_order,
    parse_value,
)
from node_registry import build_node_library
from trainer import get_dataset_name_from_graph, train_graph


MAX_GRAPH_BYTES = 1_000_000
MAX_GRAPH_NODES = 128
MAX_MODEL_PARAMETERS = 25_000_000
MAX_INPUT_ELEMENTS = 2_000_000
MAX_ARTIFACT_BYTES = 256 * 1024 * 1024


def _product(values: list[int]) -> int:
    result = 1
    for value in values:
        result *= value
    return result


def _sanitize_history(value: object) -> dict:
    source = value if isinstance(value, dict) else {}
    result = {}
    for key in ("trainLoss", "trainAcc"):
        items = source.get(key, [])
        if not isinstance(items, list):
            items = []
        clean = []
        for item in items[:1000]:
            number = float(item)
            if not math.isfinite(number):
                raise ValidationError("Worker returned non-finite training history.")
            clean.append(number)
        result[key] = clean
    return result


class TrainingCoordinator:
    def __init__(
        self,
        store: ComputeStore,
        minimum_remote_workers: int = 2,
    ) -> None:
        self.store = store
        self.minimum_remote_workers = max(2, int(minimum_remote_workers))
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._definitions = {
            definition["id"]: definition for definition in build_node_library()
        }

    def start(self) -> None:
        if self._thread is not None and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(
            target=self._fallback_loop,
            name="training-fallback-worker",
            daemon=True,
        )
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=5)

    def validate_payload(self, payload: dict) -> dict:
        if not isinstance(payload, dict):
            raise ValidationError("Training payload must be an object.")
        payload = copy.deepcopy(payload)
        encoded = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        if len(encoded) > MAX_GRAPH_BYTES:
            raise ValidationError("Training request is too large.")

        result = validate_graph_payload(payload)
        if not result.get("success"):
            errors = result.get("errors", [])
            raise ValidationError(errors[0] if errors else "Graph validation failed.")

        graph = payload.get("graph")
        node_count = self._validate_graph_definitions(graph)
        if node_count > MAX_GRAPH_NODES:
            raise ValidationError(
                f"Graph has {node_count} nodes; the limit is {MAX_GRAPH_NODES}."
            )

        training = payload.setdefault("training", {})
        if not isinstance(training, dict):
            raise ValidationError("training must be an object.")
        training["epochs"] = self._bounded_int(training.get("epochs", 1), "epochs", 1, 100)
        training["batchSize"] = self._bounded_int(
            training.get("batchSize", 64), "batchSize", 1, 512
        )
        training["maxTrainSamples"] = self._bounded_int(
            training.get("maxTrainSamples", 2000),
            "maxTrainSamples",
            1,
            100_000,
        )
        learning_rate = float(training.get("learningRate", 0.001))
        if not math.isfinite(learning_rate) or not 1e-7 <= learning_rate <= 1.0:
            raise ValidationError("learningRate must be between 0.0000001 and 1.")
        training["learningRate"] = learning_rate
        if training.get("optimizer", "Adam") not in {"Adam", "SGD"}:
            raise ValidationError("Unsupported optimizer.")
        if training.get("loss", "CrossEntropyLoss") != "CrossEntropyLoss":
            raise ValidationError("Unsupported loss.")
        training["device"] = "auto"

        input_shape = get_input_shape(graph)
        if (
            not input_shape
            or any(value < 1 or value > 8192 for value in input_shape)
            or _product(input_shape) > MAX_INPUT_ELEMENTS
        ):
            raise ValidationError("Graph input shape exceeds the compute limit.")

        model = GeneratedGraphModel(graph, get_topological_order(graph))
        parameter_count = sum(parameter.numel() for parameter in model.parameters())
        if parameter_count > MAX_MODEL_PARAMETERS:
            raise ValidationError(
                f"Model has {parameter_count:,} parameters; the limit is "
                f"{MAX_MODEL_PARAMETERS:,}."
            )
        return payload

    @staticmethod
    def _bounded_int(value: object, field: str, minimum: int, maximum: int) -> int:
        try:
            parsed = int(value)
        except (TypeError, ValueError, OverflowError):
            raise ValidationError(f"{field} must be an integer.") from None
        if parsed < minimum or parsed > maximum:
            raise ValidationError(
                f"{field} must be between {minimum} and {maximum}."
            )
        return parsed

    def _validate_graph_definitions(self, graph: dict) -> int:
        if not isinstance(graph, dict):
            raise ValidationError("graph must be an object.")
        count = 0
        for node in graph.get("nodes", []):
            count += 1
            definition_id = node.get("definitionId", "")
            definition = self._definitions.get(definition_id)
            if definition is None:
                raise ValidationError(f"Unknown node definition: {definition_id}")
            if node.get("symbol") != definition.get("symbol"):
                raise ValidationError(f"Node {definition_id} has an invalid symbol.")
            if node.get("nodeKind") != definition.get("nodeKind"):
                raise ValidationError(f"Node {definition_id} has an invalid node kind.")

            allowed_parameters = {
                parameter["name"] for parameter in definition.get("initParams", [])
            }
            parsed_parameters = {}
            for parameter in node.get("parameters", []):
                key = parameter.get("key", "")
                if key not in allowed_parameters:
                    raise ValidationError(
                        f"Node {definition_id} contains unsupported parameter {key}."
                    )
                raw_value = parameter.get("value", "")
                if len(str(raw_value)) > 256:
                    raise ValidationError(f"Parameter {key} is too long.")
                parsed = parse_value(raw_value)
                self._validate_parameter_size(parsed, key)
                parsed_parameters[key] = parsed

            self._validate_node_allocation(
                definition.get("symbol", ""),
                parsed_parameters,
            )

            inner_graph = node.get("innerGraph")
            if inner_graph is not None:
                count += self._validate_graph_definitions(inner_graph)
        return count

    @staticmethod
    def _validate_parameter_size(value: object, key: str) -> None:
        if isinstance(value, bool) or value is None:
            return
        if isinstance(value, int) and abs(value) > 16384:
            raise ValidationError(f"Parameter {key} exceeds the compute limit.")
        if isinstance(value, (list, tuple)):
            if len(value) > 8:
                raise ValidationError(f"Parameter {key} has too many values.")
            for item in value:
                TrainingCoordinator._validate_parameter_size(item, key)

    @staticmethod
    def _validate_node_allocation(symbol: str, parameters: dict) -> None:
        def positive_int(name: str, default: int = 1) -> int:
            value = parameters.get(name, default)
            if isinstance(value, bool) or not isinstance(value, int):
                return default
            return max(1, value)

        estimated_parameters = 0
        if symbol == "torch.nn.Linear":
            estimated_parameters = (
                positive_int("in_features") * positive_int("out_features")
            )
        elif symbol == "torch.nn.Bilinear":
            estimated_parameters = (
                positive_int("in1_features")
                * positive_int("in2_features")
                * positive_int("out_features")
            )
        elif symbol in {
            "torch.nn.Conv1d",
            "torch.nn.Conv2d",
            "torch.nn.Conv3d",
            "torch.nn.ConvTranspose1d",
            "torch.nn.ConvTranspose2d",
            "torch.nn.ConvTranspose3d",
        }:
            kernel = parameters.get("kernel_size", 1)
            if isinstance(kernel, int):
                kernel_elements = max(1, kernel)
            elif isinstance(kernel, (list, tuple)):
                kernel_elements = _product([max(1, int(value)) for value in kernel])
            else:
                kernel_elements = 1
            estimated_parameters = (
                positive_int("in_channels")
                * positive_int("out_channels")
                * kernel_elements
                // positive_int("groups")
            )
        elif symbol in {"torch.nn.Embedding", "torch.nn.EmbeddingBag"}:
            estimated_parameters = (
                positive_int("num_embeddings") * positive_int("embedding_dim")
            )
        elif symbol in {"torch.nn.RNN", "torch.nn.GRU", "torch.nn.LSTM"}:
            gates = 4 if symbol.endswith("LSTM") else 3 if symbol.endswith("GRU") else 1
            hidden = positive_int("hidden_size")
            estimated_parameters = (
                gates
                * hidden
                * (positive_int("input_size") + hidden)
                * positive_int("num_layers")
            )
        elif symbol == "torch.nn.MultiheadAttention":
            embed_dim = positive_int("embed_dim")
            estimated_parameters = 4 * embed_dim * embed_dim

        if estimated_parameters > MAX_MODEL_PARAMETERS:
            raise ValidationError(
                f"Node {symbol} would create about {estimated_parameters:,} parameters; "
                f"the per-model limit is {MAX_MODEL_PARAMETERS:,}."
            )

    def schedule(self, payload: dict, player_id: str) -> dict:
        payload = self.validate_payload(payload)
        active_workers = self.store.active_worker_count()
        if active_workers >= self.minimum_remote_workers:
            validation_hash = self.store.payload_hash(payload)
            job = self.store.enqueue_validated_job(
                player_id,
                payload,
                validation_hash,
            )
            return {
                "success": True,
                "queued": True,
                "jobId": job["jobId"],
                "jobStatus": "queued",
                "executionMode": "remote_worker",
                "activeWorkers": active_workers,
                "errors": [],
                "warnings": [],
            }

        result = train_graph(payload, owner_player_id=player_id)
        result["queued"] = False
        result["executionMode"] = "server"
        result["activeWorkers"] = active_workers
        return self._public_training_result(result)

    def accept_worker_artifact(
        self,
        job_id: str,
        worker_id: str,
        artifact: bytes,
    ) -> dict:
        if len(artifact) == 0 or len(artifact) > MAX_ARTIFACT_BYTES:
            raise ValidationError("Worker checkpoint has an invalid size.")
        leased_job = self.store.get_leased_job(job_id, worker_id)
        payload = leased_job["payload"]
        if not secrets.compare_digest(
            self.store.payload_hash(payload),
            leased_job["validationHash"],
        ):
            raise ValidationError("Training job validation receipt is invalid.")

        try:
            checkpoint = torch.load(
                io.BytesIO(artifact),
                map_location="cpu",
                weights_only=True,
            )
            if not isinstance(checkpoint, dict):
                raise ValidationError("Worker checkpoint must be an object.")
            state_dict = checkpoint.get("model_state_dict")
            if not isinstance(state_dict, dict) or not state_dict:
                raise ValidationError("Worker checkpoint does not contain model weights.")

            tensor_bytes = 0
            for tensor in state_dict.values():
                if not isinstance(tensor, torch.Tensor):
                    raise ValidationError("Worker checkpoint contains a non-tensor weight.")
                tensor_bytes += tensor.numel() * tensor.element_size()
                if tensor_bytes > MAX_ARTIFACT_BYTES:
                    raise ValidationError("Worker model weights exceed the size limit.")
                if (tensor.is_floating_point() or tensor.is_complex()) and not torch.isfinite(
                    tensor
                ).all():
                    raise ValidationError("Worker model contains NaN or infinite weights.")

            graph = payload["graph"]
            model = GeneratedGraphModel(graph, get_topological_order(graph))
            model.load_state_dict(state_dict, strict=True)

            worker_metadata = checkpoint.get("metadata", {})
            num_classes = int(worker_metadata.get("numClasses", 0))
            if num_classes < 1 or num_classes > 10_000:
                raise ValidationError("Worker checkpoint has an invalid class count.")
            history = _sanitize_history(checkpoint.get("history", {}))
            training = payload.get("training", {})
            dataset_name = get_dataset_name_from_graph(graph) or training.get(
                "dataset", "MNIST"
            )
            metadata = save_received_checkpoint(
                state_dict=state_dict,
                graph=graph,
                dataset_name=dataset_name,
                input_shape=get_input_shape(graph),
                history=history,
                num_classes=num_classes,
                model_name=training.get("modelName", "UnnamedModel"),
                weight_name=training.get("weightName", ""),
                owner_player_id=leased_job["playerId"],
                worker_id=worker_id,
            )
            result = {
                "success": True,
                "errors": [],
                "warnings": ["Training ran on an available player computer."],
                "history": history,
                "resultNodes": [],
                "checkpointId": metadata["checkpointId"],
                "checkpointMetadata": metadata,
                "numClasses": num_classes,
                "modelSummary": str(model),
                "device": "remote worker",
                "dataset": dataset_name,
                "epochs": training.get("epochs", 1),
                "queued": False,
                "jobId": job_id,
                "jobStatus": "completed",
                "executionMode": "remote_worker",
            }
            result = self._public_training_result(result)
            self.store.complete_job(job_id, worker_id, result)
            return result
        except Exception as error:
            self.store.fail_job(job_id, worker_id, str(error), retry=True)
            raise

    @staticmethod
    def _public_training_result(result: dict) -> dict:
        result = copy.deepcopy(result)
        result.pop("checkpointPath", None)
        metadata = result.get("checkpointMetadata")
        if isinstance(metadata, dict):
            metadata.pop("checkpointPath", None)
            metadata.pop("ownerPlayerId", None)
        return result

    def _fallback_loop(self) -> None:
        while not self._stop.wait(2.0):
            self.process_fallback_once()

    def process_fallback_once(self) -> bool:
        if self.store.active_worker_count() >= self.minimum_remote_workers:
            return False
        job = self.store.claim_next_job("server", lease_seconds=3600)
        if job is None:
            return False
        try:
            result = train_graph(
                job["payload"],
                owner_player_id=job["playerId"],
            )
            result["queued"] = False
            result["jobId"] = job["jobId"]
            result["jobStatus"] = "completed" if result.get("success") else "failed"
            result["executionMode"] = "server_fallback"
            result = self._public_training_result(result)
            if result.get("success"):
                self.store.complete_job(job["jobId"], "server", result)
            else:
                message = "; ".join(result.get("errors", [])) or "Training failed."
                self.store.fail_job(job["jobId"], "server", message, retry=False)
        except Exception as error:
            try:
                self.store.fail_job(job["jobId"], "server", str(error), retry=False)
            except Exception:
                pass
        return True
