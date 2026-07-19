"""Authoritative control plane for accounts, databases, and job scheduling.

Only this module owns the server persistence objects. HTTP worker-plane routes
receive a narrow gateway reference and never import either SQLite store.
"""

from __future__ import annotations

import copy
import os
from pathlib import Path

from checkpoint_manager import delete_checkpoint, list_checkpoints
from compute_store import ComputeStore
from distributed_training import MAX_ARTIFACT_BYTES, TrainingCoordinator
from leaderboard_store import LeaderboardStore
from trainer import final_evaluate_graph


class ControlPlane:
    """The sole authority allowed to mutate server state and assign jobs."""

    def __init__(
        self,
        leaderboard_store: LeaderboardStore,
        compute_store: ComputeStore,
        coordinator: TrainingCoordinator | None = None,
    ) -> None:
        self._leaderboard_store = leaderboard_store
        self._compute_store = compute_store
        self._coordinator = coordinator or TrainingCoordinator(compute_store)

    @classmethod
    def from_environment(cls) -> "ControlPlane":
        backend_dir = Path(__file__).resolve().parent
        server_data_dir = Path(
            os.environ.get("SERVER_DATA_DIR", backend_dir / "server_data")
        )
        server_data_dir.mkdir(parents=True, exist_ok=True)
        token_pepper = os.environ.get("PLAYER_TOKEN_PEPPER", "")
        if len(token_pepper) < 32:
            raise RuntimeError(
                "PLAYER_TOKEN_PEPPER must be set to a stable secret of at least "
                "32 characters before the control plane starts."
            )
        leaderboard_store = LeaderboardStore(
            os.environ.get(
                "LEADERBOARD_DB_PATH", server_data_dir / "leaderboard.db"
            ),
            season=os.environ.get("LEADERBOARD_SEASON", "season-1"),
            token_pepper=token_pepper,
            password_iterations=int(
                os.environ.get("PASSWORD_HASH_ITERATIONS", "310000")
            ),
            session_ttl_days=int(os.environ.get("SESSION_TTL_DAYS", "30")),
        )
        compute_store = ComputeStore(
            os.environ.get("COMPUTE_DB_PATH", server_data_dir / "compute.db")
        )
        coordinator = TrainingCoordinator(
            compute_store,
            minimum_remote_workers=max(
                2, int(os.environ.get("MIN_REMOTE_WORKERS", "2"))
            ),
        )
        return cls(leaderboard_store, compute_store, coordinator)

    @property
    def maximum_artifact_bytes(self) -> int:
        return MAX_ARTIFACT_BYTES

    def start(self) -> None:
        self._coordinator.start()

    def stop(self) -> None:
        self._coordinator.stop()

    @staticmethod
    def _public_checkpoint(item: dict) -> dict:
        result = copy.deepcopy(item)
        result.pop("checkpointPath", None)
        result.pop("ownerPlayerId", None)
        return result

    @classmethod
    def _public_evaluation(cls, result: dict) -> dict:
        result = copy.deepcopy(result)
        result.pop("checkpointPath", None)
        metadata = result.get("checkpointMetadata")
        if isinstance(metadata, dict):
            result["checkpointMetadata"] = cls._public_checkpoint(metadata)
        return result

    def status(self) -> dict:
        active_workers = self._compute_store.active_worker_count()
        threshold = self._coordinator.minimum_remote_workers
        return {
            "activeWorkers": active_workers,
            "remoteThreshold": threshold,
            "mode": "remote_workers" if active_workers >= threshold else "server",
        }

    # Account commands and queries -----------------------------------------

    def register_account(self, payload: dict) -> dict:
        return self._leaderboard_store.register_account(
            email=payload.get("email", ""),
            password=payload.get("password", ""),
            confirm_password=payload.get("confirmPassword", ""),
            display_name=payload.get("displayName", ""),
        )

    def login_account(self, payload: dict) -> dict:
        return self._leaderboard_store.login_account(
            email=payload.get("email", ""),
            password=payload.get("password", ""),
        )

    def authenticate_player(self, token: str) -> dict:
        return self._leaderboard_store.authenticate_account_session(token)

    def logout_account(self, token: str) -> None:
        self._leaderboard_store.logout_account(token)

    # Unity control-plane operations ---------------------------------------

    def leaderboard(
        self,
        dataset: str,
        limit: int,
        offset: int,
        player: dict | None,
    ) -> dict:
        return self._leaderboard_store.get_leaderboard(
            dataset=dataset,
            limit=limit,
            offset=offset,
            caller_player_id=player["playerId"] if player else None,
        )

    def schedule_training(self, payload: dict, player: dict) -> dict:
        # TrainingCoordinator validates and canonicalizes before the job can
        # be inserted. ComputeStore then requires the matching validation hash.
        return self._coordinator.schedule(payload, player["playerId"])

    def training_job(self, job_id: str, player: dict) -> dict:
        return self._compute_store.get_job(
            job_id,
            player_id=player["playerId"],
        )

    def final_evaluate(self, payload: dict, player: dict) -> dict:
        submit = bool(payload.get("submitToLeaderboard", False))
        result = final_evaluate_graph(
            payload,
            leaderboard_mode=submit,
            owner_player_id=player["playerId"],
        )
        if submit and result.get("success"):
            metadata = result.get("checkpointMetadata", {})
            result["leaderboardScore"] = self._leaderboard_store.record_score(
                player_id=player["playerId"],
                dataset=result["dataset"],
                checkpoint_id=result["checkpointId"],
                model_name=metadata.get("modelName", "UnnamedModel"),
                f1_score=result.get("finalMetrics", {}).get("f1_macro"),
            )
        return self._public_evaluation(result)

    def checkpoints(
        self,
        player: dict,
        dataset_name: str = "",
        model_name: str = "",
    ) -> list[dict]:
        result = list_checkpoints(
            dataset_name=dataset_name or None,
            model_name=model_name or None,
            owner_player_id=player["playerId"],
        )
        return [self._public_checkpoint(item) for item in result]

    def delete_checkpoint(self, checkpoint_id: str, player: dict) -> dict:
        deleted = delete_checkpoint(
            checkpoint_id,
            owner_player_id=player["playerId"],
        )
        return self._public_checkpoint(deleted)

    # Worker-plane gateway --------------------------------------------------

    def register_worker(self, payload: dict, player: dict) -> dict:
        return self._compute_store.register_worker(
            player_id=player["playerId"],
            name=payload.get("name", "Player computer"),
            capabilities=payload.get("capabilities", {}),
        )

    def authenticate_worker(self, token: str) -> dict:
        return self._compute_store.authenticate_worker(token)

    def heartbeat_worker(self, worker: dict) -> dict:
        return self._compute_store.heartbeat(worker["workerId"])

    def claim_worker_job(self, worker: dict) -> dict | None:
        self._compute_store.heartbeat(worker["workerId"])
        if (
            self._compute_store.active_worker_count()
            < self._coordinator.minimum_remote_workers
        ):
            return None
        supported = worker.get("capabilities", {}).get("supportedDatasets")
        return self._compute_store.claim_next_job(
            worker["workerId"],
            lease_seconds=120,
            supported_datasets=(
                set(supported) if isinstance(supported, list) else None
            ),
        )

    def renew_worker_job(self, job_id: str, worker: dict) -> dict:
        self._compute_store.heartbeat(worker["workerId"])
        return self._compute_store.renew_lease(
            job_id,
            worker["workerId"],
            lease_seconds=120,
        )

    def complete_worker_job(
        self,
        job_id: str,
        worker: dict,
        artifact: bytes,
    ) -> dict:
        return self._coordinator.accept_worker_artifact(
            job_id,
            worker["workerId"],
            artifact,
        )

    def fail_worker_job(
        self,
        job_id: str,
        worker: dict,
        payload: dict,
    ) -> dict:
        return self._compute_store.fail_job(
            job_id,
            worker["workerId"],
            str(payload.get("error", "Worker training failed.")),
            retry=bool(payload.get("retry", True)),
        )
