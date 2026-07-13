import json
import hashlib
import os
import re
import threading
import time
import uuid
from pathlib import Path
from typing import Any

import torch


BACKEND_DIR = Path(__file__).resolve().parent
RUNTIME_DIR = Path(os.environ.get("NN_BUILDER_RUNTIME_DIR", BACKEND_DIR))
SAVED_MODEL_DIR = RUNTIME_DIR / "saved_models"
CHECKPOINT_DIR = SAVED_MODEL_DIR / "checkpoints"
REGISTRY_PATH = SAVED_MODEL_DIR / "checkpoint_registry.json"
_REGISTRY_LOCK = threading.RLock()

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
    with _REGISTRY_LOCK:
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
    with _REGISTRY_LOCK:
        temporary_path = REGISTRY_PATH.with_suffix(".json.tmp")
        with open(temporary_path, "w", encoding="utf-8") as f:
            json.dump(registry, f, indent=2, ensure_ascii=False)
            f.flush()

        temporary_path.replace(REGISTRY_PATH)


def build_graph_signature(graph: dict) -> dict:
    """
    Lightweight architecture signature.
    Used to warn the player if a weight is loaded into a different model.
    """
    canonical_graph = _canonical_graph(graph)
    graph_json = json.dumps(
        canonical_graph,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
    )

    return {
        "nodes": canonical_graph["nodes"],
        "edgeCount": len(canonical_graph["edges"]),
        "nodeCount": len(canonical_graph["nodes"]),
        "graphHash": hashlib.sha256(graph_json.encode("utf-8")).hexdigest(),
    }


def _canonical_graph(graph: dict) -> dict:
    """Return a stable architecture description without Unity UUIDs or positions."""
    raw_nodes = graph.get("nodes", [])
    node_indexes = {
        node.get("nodeId", ""): index
        for index, node in enumerate(raw_nodes)
    }

    nodes = []
    for node in raw_nodes:
        parameters = [
            {
                "key": str(parameter.get("key", "")),
                "value": str(parameter.get("value", "")),
            }
            for parameter in node.get("parameters", [])
            if parameter.get("key", "") != "container_name"
        ]
        parameters.sort(key=lambda item: (item["key"], item["value"]))

        canonical_node = {
            "definitionId": node.get("definitionId", ""),
            "symbol": node.get("symbol", ""),
            "nodeKind": node.get("nodeKind", ""),
            "parameters": parameters,
        }

        inner_graph = node.get("innerGraph")
        if isinstance(inner_graph, dict):
            canonical_node["innerGraph"] = _canonical_graph(inner_graph)

        nodes.append(canonical_node)

    edges = []
    for edge in graph.get("edges", []):
        edges.append({
            "fromNode": node_indexes.get(edge.get("fromNodeId", ""), -1),
            "fromPortName": edge.get("fromPortName", ""),
            "toNode": node_indexes.get(edge.get("toNodeId", ""), -1),
            "toPortName": edge.get("toPortName", ""),
        })

    edges.sort(key=lambda item: (
        item["fromNode"],
        item["fromPortName"],
        item["toNode"],
        item["toPortName"],
    ))

    return {"nodes": nodes, "edges": edges}


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

    checkpoint_id = (
        f"{safe_model_name}_{safe_weight_name}_{timestamp}_{uuid.uuid4().hex[:12]}"
    )

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

    with _REGISTRY_LOCK:
        registry = load_checkpoint_registry()
        registry["checkpoints"].append(metadata)
        save_checkpoint_registry(registry)

    return metadata


def save_received_checkpoint(
    state_dict: dict,
    graph: dict,
    dataset_name: str,
    input_shape: list[int],
    history: dict,
    num_classes: int,
    model_name: str,
    weight_name: str,
    owner_player_id: str,
    worker_id: str,
):
    """Persist a worker-produced state dict using server-owned metadata."""
    timestamp = time.strftime("%Y%m%d_%H%M%S")
    model_name = model_name.strip() if model_name else "UnnamedModel"
    weight_name = weight_name.strip() if weight_name else f"{model_name}_{timestamp}"
    safe_model_name = sanitize_filename(model_name)
    safe_weight_name = sanitize_filename(weight_name)
    checkpoint_id = (
        f"{safe_model_name}_{safe_weight_name}_{timestamp}_{uuid.uuid4().hex[:12]}"
    )
    checkpoint_path = CHECKPOINT_DIR / f"{checkpoint_id}.pt"
    metadata = {
        "checkpointId": checkpoint_id,
        "modelName": model_name,
        "weightName": weight_name,
        "datasetName": dataset_name,
        "inputShape": input_shape,
        "numClasses": int(num_classes),
        "savedAt": timestamp,
        "checkpointPath": str(checkpoint_path),
        "graphSignature": build_graph_signature(graph),
        "ownerPlayerId": owner_player_id,
        "trainedByWorkerId": worker_id,
    }
    checkpoint = {
        "model_state_dict": state_dict,
        "metadata": metadata,
        "history": history,
    }
    torch.save(checkpoint, checkpoint_path)

    with _REGISTRY_LOCK:
        registry = load_checkpoint_registry()
        registry["checkpoints"].append(metadata)
        save_checkpoint_registry(registry)

    return metadata


def list_checkpoints(
    dataset_name: str | None = None,
    model_name: str | None = None,
    owner_player_id: str | None = None,
):
    registry = load_checkpoint_registry()
    checkpoints = registry.get("checkpoints", [])

    result = []

    for item in checkpoints:
        if dataset_name and item.get("datasetName") != dataset_name:
            continue

        if model_name and item.get("modelName") != model_name:
            continue

        if owner_player_id is not None:
            if item.get("ownerPlayerId", "") != owner_player_id:
                continue

        result.append(item)

    # Newest first.
    result.sort(key=lambda x: x.get("savedAt", ""), reverse=True)

    return result


def get_checkpoint_metadata(
    checkpoint_id: str,
    owner_player_id: str | None = None,
):
    checkpoints = list_checkpoints(owner_player_id=owner_player_id)

    for item in checkpoints:
        if item.get("checkpointId") == checkpoint_id:
            return item

    raise RuntimeError(f"Checkpoint not found: {checkpoint_id}")


def resolve_checkpoint_path_by_id(
    checkpoint_id: str,
    owner_player_id: str | None = None,
):
    metadata = get_checkpoint_metadata(
        checkpoint_id,
        owner_player_id=owner_player_id,
    )

    path = Path(metadata.get("checkpointPath", ""))

    if not path.exists():
        raise RuntimeError(f"Checkpoint file not found: {path}")

    return path, metadata


def delete_checkpoint(
    checkpoint_id: str,
    owner_player_id: str | None = None,
):
    with _REGISTRY_LOCK:
        registry = load_checkpoint_registry()
        checkpoints = registry.get("checkpoints", [])

        kept = []
        deleted = None

        for item in checkpoints:
            matches_id = item.get("checkpointId") == checkpoint_id
            matches_owner = (
                owner_player_id is None
                or item.get("ownerPlayerId", "") == owner_player_id
            )

            if matches_id and matches_owner:
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
