from fastapi import FastAPI
from node_registry import build_node_library
from graph_validator import validate_graph_payload
from model_builder import dry_run_graph
from trainer import train_graph, final_evaluate_graph
from local_datasets import infer_dataset_metadata

app = FastAPI()


@app.get("/")
def root():
    return {
        "success": True,
        "message": "Neural Network Builder backend is running."
    }


@app.get("/node_library")
def node_library():
    library = build_node_library()

    return {
        "success": True,
        "count": len(library),
        "library": library
    }


@app.post("/validate_graph")
def validate_graph(payload: dict):
    result = validate_graph_payload(payload)

    return {
        "success": result["success"],
        "errors": result["errors"],
        "warnings": result["warnings"],
    }

@app.post("/dry_run_graph")
def dry_run_graph_endpoint(payload: dict):
    return dry_run_graph(payload)

@app.post("/train_graph")
def train_graph_endpoint(payload: dict):
    return train_graph(payload)

@app.get("/dataset_metadata/{dataset_name}")
def dataset_metadata(dataset_name: str):
    try:
        metadata = infer_dataset_metadata(dataset_name)

        return {
            "success": True,
            "metadata": metadata,
            "errors": [],
        }

    except Exception as e:
        return {
            "success": False,
            "metadata": {},
            "errors": [str(e)],
        }

@app.post("/final_evaluate_graph")
def final_evaluate_graph_endpoint(payload: dict):
    return final_evaluate_graph(payload)