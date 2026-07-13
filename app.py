import copy
import os
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import Depends, FastAPI, Header, Request
from fastapi.responses import JSONResponse

from checkpoint_manager import delete_checkpoint, list_checkpoints
from compute_store import ComputeStore
from distributed_training import MAX_ARTIFACT_BYTES, TrainingCoordinator
from graph_validator import validate_graph_payload
from leaderboard_store import LeaderboardError, LeaderboardStore
from local_datasets import infer_dataset_metadata
from model_builder import dry_run_graph
from node_registry import build_node_library
from trainer import final_evaluate_graph


BACKEND_DIR = Path(__file__).resolve().parent
SERVER_DATA_DIR = Path(
    os.environ.get("SERVER_DATA_DIR", BACKEND_DIR / "server_data")
)
SERVER_DATA_DIR.mkdir(parents=True, exist_ok=True)

leaderboard_store = LeaderboardStore(
    os.environ.get("LEADERBOARD_DB_PATH", SERVER_DATA_DIR / "leaderboard.db"),
    season=os.environ.get("LEADERBOARD_SEASON", "season-1"),
    token_pepper=os.environ.get("PLAYER_TOKEN_PEPPER", ""),
)
compute_store = ComputeStore(
    os.environ.get("COMPUTE_DB_PATH", SERVER_DATA_DIR / "compute.db")
)
coordinator = TrainingCoordinator(
    compute_store,
    minimum_remote_workers=max(2, int(os.environ.get("MIN_REMOTE_WORKERS", "2"))),
)


@asynccontextmanager
async def lifespan(_: FastAPI):
    coordinator.start()
    try:
        yield
    finally:
        coordinator.stop()


app = FastAPI(title="Neural Network Builder", lifespan=lifespan)


@app.exception_handler(LeaderboardError)
async def leaderboard_error_handler(_: Request, error: LeaderboardError):
    return JSONResponse(
        status_code=error.status_code,
        content={"success": False, "errors": [error.as_dict()]},
    )


def _bearer_token(authorization: str | None) -> str:
    if not authorization:
        return ""
    prefix, separator, token = authorization.partition(" ")
    if not separator or prefix.lower() != "bearer":
        return ""
    return token.strip()


def optional_player(authorization: str | None = Header(default=None)) -> dict | None:
    token = _bearer_token(authorization)
    return leaderboard_store.authenticate(token) if token else None


def require_player(player: dict | None = Depends(optional_player)) -> dict:
    if player is None:
        from leaderboard_store import AuthenticationError

        raise AuthenticationError("A player token is required.")
    return player


def require_worker(authorization: str | None = Header(default=None)) -> dict:
    return compute_store.authenticate_worker(_bearer_token(authorization))


def _public_checkpoint(item: dict) -> dict:
    result = copy.deepcopy(item)
    result.pop("checkpointPath", None)
    result.pop("ownerPlayerId", None)
    return result


def _public_evaluation(result: dict) -> dict:
    result = copy.deepcopy(result)
    result.pop("checkpointPath", None)
    metadata = result.get("checkpointMetadata")
    if isinstance(metadata, dict):
        result["checkpointMetadata"] = _public_checkpoint(metadata)
    return result


@app.get("/")
def root():
    active_workers = compute_store.active_worker_count()
    return {
        "success": True,
        "message": "Neural Network Builder backend is running.",
        "compute": {
            "activeWorkers": active_workers,
            "remoteThreshold": coordinator.minimum_remote_workers,
            "mode": (
                "remote_workers"
                if active_workers >= coordinator.minimum_remote_workers
                else "server"
            ),
        },
    }


@app.post("/players/register", status_code=201)
def register_player(payload: dict):
    player = leaderboard_store.register_player(payload.get("displayName", ""))
    return {"success": True, "player": player, "errors": []}


@app.get("/players/me")
def player_me(player: dict = Depends(require_player)):
    return {"success": True, "player": player, "errors": []}


@app.get("/leaderboard")
def leaderboard(
    dataset: str,
    limit: int = 50,
    offset: int = 0,
    player: dict | None = Depends(optional_player),
):
    result = leaderboard_store.get_leaderboard(
        dataset=dataset,
        limit=limit,
        offset=offset,
        caller_player_id=player["playerId"] if player else None,
    )
    return {"success": True, "leaderboard": result, "errors": []}


@app.get("/node_library")
def node_library():
    library = build_node_library()
    return {"success": True, "count": len(library), "library": library}


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
def train_graph_endpoint(
    payload: dict,
    player: dict = Depends(require_player),
):
    return coordinator.schedule(payload, player["playerId"])


@app.get("/training_jobs/{job_id}")
def training_job(job_id: str, player: dict = Depends(require_player)):
    job = compute_store.get_job(job_id, player_id=player["playerId"])
    return {"success": True, "job": job, "errors": []}


@app.get("/dataset_metadata/{dataset_name}")
def dataset_metadata(dataset_name: str):
    try:
        metadata = infer_dataset_metadata(dataset_name)
        return {"success": True, "metadata": metadata, "errors": []}
    except Exception as error:
        return {"success": False, "metadata": {}, "errors": [str(error)]}


@app.post("/final_evaluate_graph")
def final_evaluate_graph_endpoint(
    payload: dict,
    player: dict | None = Depends(optional_player),
):
    submit = bool(payload.get("submitToLeaderboard", False))
    if submit and player is None:
        from leaderboard_store import AuthenticationError

        raise AuthenticationError("A player token is required for leaderboard submission.")

    result = final_evaluate_graph(
        payload,
        leaderboard_mode=submit,
        owner_player_id=player["playerId"] if player else None,
    )
    if submit and result.get("success"):
        metadata = result.get("checkpointMetadata", {})
        score = leaderboard_store.record_score(
            player_id=player["playerId"],
            dataset=result["dataset"],
            checkpoint_id=result["checkpointId"],
            model_name=metadata.get("modelName", "UnnamedModel"),
            f1_score=result.get("finalMetrics", {}).get("f1_macro"),
        )
        result["leaderboardScore"] = score
    return _public_evaluation(result)


@app.get("/checkpoints")
def checkpoints(
    dataset_name: str = "",
    model_name: str = "",
    player: dict | None = Depends(optional_player),
):
    try:
        owner = player["playerId"] if player else ""
        result = list_checkpoints(
            dataset_name=dataset_name or None,
            model_name=model_name or None,
            owner_player_id=owner,
        )
        return {
            "success": True,
            "checkpoints": [_public_checkpoint(item) for item in result],
            "errors": [],
        }
    except Exception as error:
        return {"success": False, "checkpoints": [], "errors": [str(error)]}


@app.delete("/checkpoints/{checkpoint_id}")
def delete_checkpoint_endpoint(
    checkpoint_id: str,
    player: dict = Depends(require_player),
):
    try:
        deleted = delete_checkpoint(
            checkpoint_id,
            owner_player_id=player["playerId"],
        )
        return {
            "success": True,
            "deleted": _public_checkpoint(deleted),
            "errors": [],
        }
    except Exception as error:
        return {"success": False, "deleted": {}, "errors": [str(error)]}


@app.get("/compute/status")
def compute_status():
    active_workers = compute_store.active_worker_count()
    return {
        "success": True,
        "activeWorkers": active_workers,
        "remoteThreshold": coordinator.minimum_remote_workers,
        "mode": (
            "remote_workers"
            if active_workers >= coordinator.minimum_remote_workers
            else "server"
        ),
    }


@app.post("/compute/workers/register", status_code=201)
def register_worker(payload: dict, player: dict = Depends(require_player)):
    worker = compute_store.register_worker(
        player_id=player["playerId"],
        name=payload.get("name", "Player computer"),
        capabilities=payload.get("capabilities", {}),
    )
    return {"success": True, "worker": worker, "errors": []}


@app.post("/compute/workers/heartbeat")
def worker_heartbeat(worker: dict = Depends(require_worker)):
    heartbeat = compute_store.heartbeat(worker["workerId"])
    return {"success": True, "heartbeat": heartbeat, "errors": []}


@app.post("/compute/jobs/claim")
def claim_compute_job(worker: dict = Depends(require_worker)):
    compute_store.heartbeat(worker["workerId"])
    if compute_store.active_worker_count() < coordinator.minimum_remote_workers:
        job = None
    else:
        supported = worker.get("capabilities", {}).get("supportedDatasets")
        job = compute_store.claim_next_job(
            worker["workerId"],
            lease_seconds=120,
            supported_datasets=set(supported) if isinstance(supported, list) else None,
        )
    return {"success": True, "job": job, "errors": []}


@app.post("/compute/jobs/{job_id}/heartbeat")
def compute_job_heartbeat(job_id: str, worker: dict = Depends(require_worker)):
    compute_store.heartbeat(worker["workerId"])
    lease = compute_store.renew_lease(
        job_id,
        worker["workerId"],
        lease_seconds=120,
    )
    return {"success": True, "lease": lease, "errors": []}


@app.post("/compute/jobs/{job_id}/complete")
async def complete_compute_job(
    job_id: str,
    request: Request,
    worker: dict = Depends(require_worker),
):
    content_length = int(request.headers.get("content-length", "0") or "0")
    if content_length > MAX_ARTIFACT_BYTES:
        from leaderboard_store import ValidationError

        raise ValidationError("Worker checkpoint exceeds the upload limit.")
    artifact = await request.body()
    result = coordinator.accept_worker_artifact(
        job_id,
        worker["workerId"],
        artifact,
    )
    return {"success": True, "result": result, "errors": []}


@app.post("/compute/jobs/{job_id}/fail")
def fail_compute_job(
    job_id: str,
    payload: dict,
    worker: dict = Depends(require_worker),
):
    result = compute_store.fail_job(
        job_id,
        worker["workerId"],
        str(payload.get("error", "Worker training failed.")),
        retry=bool(payload.get("retry", True)),
    )
    return {"success": True, "job": result, "errors": []}
