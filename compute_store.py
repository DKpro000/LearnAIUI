"""Persistent worker registry and leased training-job queue."""

from __future__ import annotations

import hashlib
import json
import secrets
import sqlite3
import threading
import uuid
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Iterator

from leaderboard_store import (
    AuthenticationError,
    ConflictError,
    NotFoundError,
    ValidationError,
)


_SCHEMA = """
CREATE TABLE IF NOT EXISTS compute_workers (
    worker_id TEXT PRIMARY KEY,
    player_id TEXT NOT NULL,
    name TEXT NOT NULL,
    token_hash BLOB NOT NULL UNIQUE,
    capabilities_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS compute_workers_active_idx
ON compute_workers (last_seen_at, player_id);

CREATE TABLE IF NOT EXISTS training_jobs (
    job_id TEXT PRIMARY KEY,
    player_id TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    status TEXT NOT NULL CHECK (
        status IN ('queued', 'running', 'completed', 'failed')
    ),
    claimed_by TEXT,
    lease_until TEXT,
    attempts INTEGER NOT NULL DEFAULT 0,
    result_json TEXT,
    error TEXT,
    validation_hash TEXT,
    validated_at TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS training_jobs_queue_idx
ON training_jobs (status, created_at);
"""


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _timestamp(value: datetime | None = None) -> str:
    return (value or _utc_now()).isoformat(timespec="microseconds").replace(
        "+00:00", "Z"
    )


class ComputeStore:
    def __init__(self, db_path: str | Path) -> None:
        path = Path(db_path).expanduser()
        path.parent.mkdir(parents=True, exist_ok=True)
        self.db_path = str(path)
        self._lock = threading.RLock()

        with self._connection() as connection:
            connection.execute("PRAGMA journal_mode = WAL")
            connection.executescript(_SCHEMA)
            self._migrate_schema(connection)

    @staticmethod
    def _migrate_schema(connection: sqlite3.Connection) -> None:
        columns = {
            row["name"]
            for row in connection.execute("PRAGMA table_info(training_jobs)").fetchall()
        }
        if "claimed_by" not in columns:
            connection.execute(
                "ALTER TABLE training_jobs ADD COLUMN claimed_by TEXT"
            )
            if "assigned_worker_id" in columns:
                connection.execute(
                    "UPDATE training_jobs SET claimed_by = assigned_worker_id"
                )
        if "lease_until" not in columns:
            connection.execute(
                "ALTER TABLE training_jobs ADD COLUMN lease_until TEXT"
            )
            if "lease_expires_at" in columns:
                connection.execute(
                    "UPDATE training_jobs SET lease_until = lease_expires_at"
                )
        if "validation_hash" not in columns:
            connection.execute(
                "ALTER TABLE training_jobs ADD COLUMN validation_hash TEXT"
            )
        if "validated_at" not in columns:
            connection.execute(
                "ALTER TABLE training_jobs ADD COLUMN validated_at TEXT"
            )
        now = _timestamp()
        connection.execute(
            """
            UPDATE training_jobs
            SET status = 'failed',
                error = 'Legacy queued job was not control-plane validated.',
                claimed_by = NULL,
                lease_until = NULL,
                updated_at = ?
            WHERE validation_hash IS NULL
              AND status IN ('queued', 'running')
            """,
            (now,),
        )

    @contextmanager
    def _connection(self) -> Iterator[sqlite3.Connection]:
        connection = sqlite3.connect(
            self.db_path,
            isolation_level=None,
            timeout=5.0,
            check_same_thread=False,
        )
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA busy_timeout = 5000")
        connection.execute("PRAGMA synchronous = NORMAL")
        try:
            yield connection
        finally:
            connection.close()

    @contextmanager
    def _write(self) -> Iterator[sqlite3.Connection]:
        with self._lock, self._connection() as connection:
            connection.execute("BEGIN IMMEDIATE")
            try:
                yield connection
            except Exception:
                connection.rollback()
                raise
            else:
                connection.commit()

    @staticmethod
    def _hash_token(token: str) -> bytes:
        return hashlib.sha256(token.encode("utf-8")).digest()

    @staticmethod
    def _require_text(value: str, field: str, max_length: int) -> str:
        if not isinstance(value, str):
            raise ValidationError(f"{field} must be a string.")
        value = " ".join(value.split())
        if not value:
            raise ValidationError(f"{field} cannot be empty.")
        if len(value) > max_length:
            raise ValidationError(
                f"{field} must be at most {max_length} characters."
            )
        return value

    def register_worker(
        self,
        player_id: str,
        name: str,
        capabilities: dict | None = None,
    ) -> dict:
        player_id = self._require_text(player_id, "playerId", 128)
        name = self._require_text(name, "name", 64)
        capabilities = capabilities if isinstance(capabilities, dict) else {}
        worker_id = str(uuid.uuid4())
        token = "wk_" + secrets.token_urlsafe(32)
        now = _timestamp()

        with self._write() as connection:
            connection.execute(
                """
                INSERT INTO compute_workers
                    (worker_id, player_id, name, token_hash, capabilities_json,
                     created_at, last_seen_at)
                VALUES (?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    worker_id,
                    player_id,
                    name,
                    self._hash_token(token),
                    json.dumps(capabilities, separators=(",", ":")),
                    now,
                    now,
                ),
            )

        return {
            "workerId": worker_id,
            "name": name,
            "token": token,
            "createdAt": now,
        }

    def authenticate_worker(self, token: str) -> dict:
        if not isinstance(token, str):
            raise AuthenticationError("Invalid worker token.")
        token = token.removeprefix("Bearer ").strip()
        
        if not token:
            raise AuthenticationError("Invalid worker token.")

        with self._connection() as connection:
            row = connection.execute(
                """
                SELECT worker_id, player_id, name, capabilities_json, last_seen_at
                FROM compute_workers WHERE token_hash = ?
                """,
                (self._hash_token(token),),
            ).fetchone()

        if row is None:
            raise AuthenticationError("Invalid worker token.")
        return {
            "workerId": row["worker_id"],
            "playerId": row["player_id"],
            "name": row["name"],
            "capabilities": json.loads(row["capabilities_json"]),
            "lastSeenAt": row["last_seen_at"],
        }

    def heartbeat(self, worker_id: str) -> dict:
        now = _timestamp()
        with self._write() as connection:
            changed = connection.execute(
                "UPDATE compute_workers SET last_seen_at = ? WHERE worker_id = ?",
                (now, worker_id),
            ).rowcount
        if not changed:
            raise NotFoundError("Worker not found.")
        return {"workerId": worker_id, "lastSeenAt": now}

    def active_worker_count(self, active_seconds: int = 45) -> int:
        cutoff = _timestamp(_utc_now() - timedelta(seconds=active_seconds))
        with self._connection() as connection:
            return int(
                connection.execute(
                    """
                    SELECT COUNT(DISTINCT player_id)
                    FROM compute_workers WHERE last_seen_at >= ?
                    """,
                    (cutoff,),
                ).fetchone()[0]
            )

    @staticmethod
    def payload_hash(payload: dict) -> str:
        if not isinstance(payload, dict):
            raise ValidationError("Training payload must be an object.")
        encoded = json.dumps(
            payload,
            separators=(",", ":"),
            ensure_ascii=False,
            sort_keys=True,
        ).encode("utf-8")
        return hashlib.sha256(encoded).hexdigest()

    def enqueue_validated_job(
        self,
        player_id: str,
        payload: dict,
        validation_hash: str,
    ) -> dict:
        player_id = self._require_text(player_id, "playerId", 128)
        if not isinstance(payload, dict):
            raise ValidationError("Training payload must be an object.")
        expected_hash = self.payload_hash(payload)
        if (
            not isinstance(validation_hash, str)
            or not secrets.compare_digest(expected_hash, validation_hash.lower())
        ):
            raise ValidationError(
                "Training payload does not match its control-plane validation receipt."
            )
        job_id = str(uuid.uuid4())
        now = _timestamp()
        payload_json = json.dumps(payload, separators=(",", ":"), ensure_ascii=False)
        with self._write() as connection:
            connection.execute(
                """
                INSERT INTO training_jobs
                    (job_id, player_id, payload_json, status, validation_hash,
                     validated_at, created_at, updated_at)
                VALUES (?, ?, ?, 'queued', ?, ?, ?, ?)
                """,
                (
                    job_id,
                    player_id,
                    payload_json,
                    expected_hash,
                    now,
                    now,
                    now,
                ),
            )
        return {
            "jobId": job_id,
            "status": "queued",
            "validationHash": expected_hash,
            "validatedAt": now,
            "createdAt": now,
        }

    def _requeue_expired(self, connection: sqlite3.Connection) -> None:
        now = _timestamp()
        connection.execute(
            """
            UPDATE training_jobs
            SET status = CASE WHEN attempts >= 3 THEN 'failed' ELSE 'queued' END,
                error = CASE WHEN attempts >= 3
                    THEN 'Training worker lease expired too many times.' ELSE error END,
                claimed_by = NULL,
                lease_until = NULL,
                updated_at = ?
            WHERE status = 'running' AND lease_until < ?
            """,
            (now, now),
        )

    def claim_next_job(
        self,
        claimant: str,
        lease_seconds: int = 120,
        supported_datasets: set[str] | None = None,
    ) -> dict | None:
        claimant = self._require_text(claimant, "claimant", 128)
        if lease_seconds < 30 or lease_seconds > 3600:
            raise ValidationError("leaseSeconds must be between 30 and 3600.")

        now = _utc_now()
        with self._write() as connection:
            self._requeue_expired(connection)
            rows = connection.execute(
                """
                SELECT job_id, player_id, payload_json, validation_hash,
                       attempts, created_at
                FROM training_jobs
                WHERE status = 'queued'
                  AND validation_hash IS NOT NULL
                  AND validated_at IS NOT NULL
                ORDER BY created_at ASC
                LIMIT 100
                """
            ).fetchall()
            row = None
            for candidate in rows:
                candidate_payload = json.loads(candidate["payload_json"])
                if not secrets.compare_digest(
                    self.payload_hash(candidate_payload),
                    candidate["validation_hash"],
                ):
                    connection.execute(
                        """
                        UPDATE training_jobs
                        SET status = 'failed',
                            error = 'Validated payload hash mismatch.',
                            updated_at = ?
                        WHERE job_id = ? AND status = 'queued'
                        """,
                        (_timestamp(now), candidate["job_id"]),
                    )
                    continue
                dataset_name = self._payload_dataset(candidate_payload)
                if (
                    supported_datasets is None
                    or dataset_name in supported_datasets
                ):
                    row = candidate
                    break
            if row is None:
                return None

            lease_until = _timestamp(now + timedelta(seconds=lease_seconds))
            changed = connection.execute(
                """
                UPDATE training_jobs
                SET status = 'running', claimed_by = ?, lease_until = ?,
                    attempts = attempts + 1, updated_at = ?
                WHERE job_id = ? AND status = 'queued'
                """,
                (claimant, lease_until, _timestamp(now), row["job_id"]),
            ).rowcount
            if not changed:
                return None

        return {
            "jobId": row["job_id"],
            "playerId": row["player_id"],
            "payload": json.loads(row["payload_json"]),
            "validationHash": row["validation_hash"],
            "attempt": int(row["attempts"]) + 1,
            "leaseUntil": lease_until,
            "createdAt": row["created_at"],
        }

    @staticmethod
    def _payload_dataset(payload: dict) -> str:
        def find_in_graph(graph: object) -> str:
            if not isinstance(graph, dict):
                return ""
            for node in graph.get("nodes", []):
                if node.get("nodeKind") == "DatasetNode":
                    for parameter in node.get("parameters", []):
                        if parameter.get("key") == "dataset_name":
                            return str(parameter.get("value", "")).strip()
                dataset_name = find_in_graph(node.get("innerGraph"))
                if dataset_name:
                    return dataset_name
            return ""

        return find_in_graph(payload.get("graph")) or str(
            payload.get("training", {}).get("dataset", "MNIST")
        )

    def renew_lease(
        self,
        job_id: str,
        claimant: str,
        lease_seconds: int = 120,
    ) -> dict:
        now = _utc_now()
        lease_until = _timestamp(now + timedelta(seconds=lease_seconds))
        with self._write() as connection:
            changed = connection.execute(
                """
                UPDATE training_jobs
                SET lease_until = ?, updated_at = ?
                WHERE job_id = ? AND status = 'running' AND claimed_by = ?
                """,
                (lease_until, _timestamp(now), job_id, claimant),
            ).rowcount
        if not changed:
            raise ConflictError("The training job is not leased to this worker.")
        return {"jobId": job_id, "leaseUntil": lease_until}

    def complete_job(self, job_id: str, claimant: str, result: dict) -> dict:
        if not isinstance(result, dict):
            raise ValidationError("result must be an object.")
        now = _timestamp()
        with self._write() as connection:
            changed = connection.execute(
                """
                UPDATE training_jobs
                SET status = 'completed', result_json = ?, error = NULL,
                    lease_until = NULL, updated_at = ?
                WHERE job_id = ? AND status = 'running' AND claimed_by = ?
                """,
                (
                    json.dumps(result, separators=(",", ":"), ensure_ascii=False),
                    now,
                    job_id,
                    claimant,
                ),
            ).rowcount
        if not changed:
            raise ConflictError("The training job is not leased to this worker.")
        return {"jobId": job_id, "status": "completed", "updatedAt": now}

    def get_leased_job(self, job_id: str, claimant: str) -> dict:
        with self._connection() as connection:
            row = connection.execute(
                """
                SELECT job_id, player_id, payload_json, validation_hash
                FROM training_jobs
                WHERE job_id = ? AND status = 'running' AND claimed_by = ?
                """,
                (job_id, claimant),
            ).fetchone()
        if row is None:
            raise ConflictError("The training job is not leased to this worker.")
        return {
            "jobId": row["job_id"],
            "playerId": row["player_id"],
            "payload": json.loads(row["payload_json"]),
            "validationHash": row["validation_hash"],
        }

    def fail_job(
        self,
        job_id: str,
        claimant: str,
        error: str,
        retry: bool = True,
    ) -> dict:
        error = self._require_text(error, "error", 2000)
        now = _timestamp()
        with self._write() as connection:
            row = connection.execute(
                """
                SELECT attempts FROM training_jobs
                WHERE job_id = ? AND status = 'running' AND claimed_by = ?
                """,
                (job_id, claimant),
            ).fetchone()
            if row is None:
                raise ConflictError("The training job is not leased to this worker.")
            status = "queued" if retry and int(row["attempts"]) < 3 else "failed"
            changed = connection.execute(
                """
                UPDATE training_jobs
                SET status = ?, error = ?, claimed_by = NULL,
                    lease_until = NULL, updated_at = ?
                WHERE job_id = ? AND status = 'running' AND claimed_by = ?
                """,
                (status, error, now, job_id, claimant),
            ).rowcount
        if not changed:
            raise ConflictError("The training job is not leased to this worker.")
        return {"jobId": job_id, "status": status, "updatedAt": now}

    def get_job(self, job_id: str, player_id: str | None = None) -> dict:
        with self._connection() as connection:
            row = connection.execute(
                """
                SELECT job_id, player_id, status, claimed_by, attempts,
                       result_json, error, created_at, updated_at
                FROM training_jobs WHERE job_id = ?
                """,
                (job_id,),
            ).fetchone()
        if row is None or (player_id is not None and row["player_id"] != player_id):
            raise NotFoundError("Training job not found.")
        return {
            "jobId": row["job_id"],
            "playerId": row["player_id"],
            "status": row["status"],
            "attempts": int(row["attempts"]),
            "result": json.loads(row["result_json"]) if row["result_json"] else None,
            "error": row["error"],
            "createdAt": row["created_at"],
            "updatedAt": row["updated_at"],
        }
