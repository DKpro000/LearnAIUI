"""LearnAIUI authoritative control-plane HTTP application."""

from __future__ import annotations

from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, Header, Request
from fastapi.responses import JSONResponse

from control_plane import ControlPlane
from graph_validator import validate_graph_payload
from leaderboard_store import AuthenticationError, LeaderboardError
from local_datasets import infer_dataset_metadata
from model_builder import dry_run_graph
from node_registry import build_node_library
from worker_plane import create_worker_plane_router


control_plane = ControlPlane.from_environment()


@asynccontextmanager
async def lifespan(_: FastAPI):
    control_plane.start()
    try:
        yield
    finally:
        control_plane.stop()


app = FastAPI(
    title="LearnAIUI Control Plane",
    description=(
        "Authoritative account, validation, scheduling, checkpoint, and "
        "leaderboard API. Worker traffic is isolated behind the worker-plane "
        "router and cannot access persistence directly."
    ),
    lifespan=lifespan,
)


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
    return control_plane.authenticate_player(token) if token else None


def require_player(player: dict | None = Depends(optional_player)) -> dict:
    if player is None:
        raise AuthenticationError("A signed-in player session is required.")
    return player


@app.get("/")
def root():
    return {
        "success": True,
        "message": "LearnAIUI control plane is running.",
        "plane": "control",
        "compute": control_plane.status(),
        "workerPlane": "/worker-plane",
    }


# Formal account API ---------------------------------------------------------


@app.post("/auth/register", status_code=201)
def register_account(payload: dict):
    player = control_plane.register_account(payload)
    return {"success": True, "player": player, "errors": []}


@app.post("/auth/login")
def login_account(payload: dict):
    player = control_plane.login_account(payload)
    return {"success": True, "player": player, "errors": []}


@app.post("/auth/logout")
def logout_account(
    authorization: str | None = Header(default=None),
    _: dict = Depends(require_player),
):
    control_plane.logout_account(_bearer_token(authorization))
    return {"success": True, "errors": []}


@app.get("/auth/me")
def account_me(player: dict = Depends(require_player)):
    return {"success": True, "player": player, "errors": []}


# Compatibility names now require the same formal email/password payload.
@app.post("/players/register", status_code=201, include_in_schema=False)
def register_player(payload: dict):
    return register_account(payload)


@app.get("/players/me", include_in_schema=False)
def player_me(player: dict = Depends(require_player)):
    return account_me(player)


# Unity control-plane API ----------------------------------------------------


@app.get("/leaderboard")
def leaderboard(
    dataset: str,
    limit: int = 50,
    offset: int = 0,
    player: dict | None = Depends(optional_player),
):
    result = control_plane.leaderboard(dataset, limit, offset, player)
    return {"success": True, "leaderboard": result, "errors": []}


@app.get("/node_library")
def node_library():
    library = build_node_library()
    return {"success": True, "count": len(library), "library": library}


@app.post("/validate_graph")
def validate_graph(payload: dict, _: dict = Depends(require_player)):
    result = validate_graph_payload(payload)
    return {
        "success": result["success"],
        "errors": result["errors"],
        "warnings": result["warnings"],
    }


@app.post("/dry_run_graph")
def dry_run_graph_endpoint(payload: dict, _: dict = Depends(require_player)):
    return dry_run_graph(payload)


@app.post("/train_graph")
def train_graph_endpoint(
    payload: dict,
    player: dict = Depends(require_player),
):
    return control_plane.schedule_training(payload, player)


@app.get("/training_jobs/{job_id}")
def training_job(job_id: str, player: dict = Depends(require_player)):
    job = control_plane.training_job(job_id, player)
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
    player: dict = Depends(require_player),
):
    return control_plane.final_evaluate(payload, player)


@app.get("/checkpoints")
def checkpoints(
    dataset_name: str = "",
    model_name: str = "",
    player: dict = Depends(require_player),
):
    try:
        result = control_plane.checkpoints(player, dataset_name, model_name)
        return {"success": True, "checkpoints": result, "errors": []}
    except Exception as error:
        return {"success": False, "checkpoints": [], "errors": [str(error)]}


@app.delete("/checkpoints/{checkpoint_id}")
def delete_checkpoint_endpoint(
    checkpoint_id: str,
    player: dict = Depends(require_player),
):
    try:
        deleted = control_plane.delete_checkpoint(checkpoint_id, player)
        return {"success": True, "deleted": deleted, "errors": []}
    except Exception as error:
        return {"success": False, "deleted": {}, "errors": [str(error)]}


@app.get("/compute/status")
@app.get("/worker-plane/status")
def compute_status():
    return {"success": True, **control_plane.status()}


# The worker-plane module is transport-only. Both prefixes call the same
# control-plane gateway; /compute remains temporarily compatible with existing
# packaged workers while /worker-plane is the canonical endpoint.
app.include_router(create_worker_plane_router(control_plane))
app.include_router(
    create_worker_plane_router(
        control_plane,
        prefix="/compute",
        include_in_schema=False,
    )
)
