"""SQLite persistence and authentication primitives for the leaderboard API."""

from __future__ import annotations

import hashlib
import hmac
import math
import secrets
import sqlite3
import threading
import unicodedata
import uuid
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterator


__all__ = [
    "AuthenticationError",
    "ConflictError",
    "LeaderboardError",
    "LeaderboardStore",
    "NotFoundError",
    "ValidationError",
]


class LeaderboardError(Exception):
    """Base error carrying values that map cleanly to an HTTP response."""

    status_code = 500
    code = "leaderboard_error"

    def as_dict(self) -> dict:
        return {"code": self.code, "message": str(self)}


class ValidationError(LeaderboardError):
    status_code = 422
    code = "validation_error"


class AuthenticationError(LeaderboardError):
    status_code = 401
    code = "authentication_error"


class ConflictError(LeaderboardError):
    status_code = 409
    code = "conflict"


class NotFoundError(LeaderboardError):
    status_code = 404
    code = "not_found"


_SCHEMA = """
CREATE TABLE IF NOT EXISTS players (
    player_id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    display_name_key TEXT NOT NULL UNIQUE,
    token_hash BLOB NOT NULL UNIQUE,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS challenges (
    challenge_id TEXT PRIMARY KEY,
    season TEXT NOT NULL,
    season_key TEXT NOT NULL,
    dataset TEXT NOT NULL,
    dataset_key TEXT NOT NULL,
    created_at TEXT NOT NULL,
    UNIQUE (season_key, dataset_key)
);

CREATE TABLE IF NOT EXISTS evaluations (
    evaluation_id TEXT PRIMARY KEY,
    challenge_id TEXT NOT NULL REFERENCES challenges(challenge_id),
    player_id TEXT NOT NULL REFERENCES players(player_id),
    checkpoint_id TEXT NOT NULL,
    model_name TEXT NOT NULL,
    f1_score REAL NOT NULL CHECK (f1_score >= 0.0 AND f1_score <= 1.0),
    score_micros INTEGER NOT NULL CHECK (score_micros BETWEEN 0 AND 1000000),
    recorded_at TEXT NOT NULL,
    UNIQUE (challenge_id, player_id, checkpoint_id)
);

CREATE INDEX IF NOT EXISTS evaluations_player_challenge_idx
ON evaluations (player_id, challenge_id, recorded_at);

CREATE TABLE IF NOT EXISTS best_scores (
    challenge_id TEXT NOT NULL REFERENCES challenges(challenge_id),
    player_id TEXT NOT NULL REFERENCES players(player_id),
    evaluation_id TEXT NOT NULL UNIQUE REFERENCES evaluations(evaluation_id),
    score_micros INTEGER NOT NULL CHECK (score_micros BETWEEN 0 AND 1000000),
    achieved_at TEXT NOT NULL,
    PRIMARY KEY (challenge_id, player_id)
);

CREATE INDEX IF NOT EXISTS best_scores_rank_idx
ON best_scores (challenge_id, score_micros DESC, achieved_at ASC, player_id ASC);

CREATE TRIGGER IF NOT EXISTS evaluations_no_update
BEFORE UPDATE ON evaluations
BEGIN
    SELECT RAISE(ABORT, 'evaluation history is immutable');
END;

CREATE TRIGGER IF NOT EXISTS evaluations_no_delete
BEFORE DELETE ON evaluations
BEGIN
    SELECT RAISE(ABORT, 'evaluation history is immutable');
END;

PRAGMA user_version = 1;
"""

_CHALLENGE_NAMESPACE = uuid.UUID("4d537142-50ef-4dd8-a49b-dffbbf34d309")
_MAX_PAGE_SIZE = 100


def _now_utc() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace(
        "+00:00", "Z"
    )


def _normalize_text(value: str, field: str, max_length: int) -> tuple[str, str]:
    if not isinstance(value, str):
        raise ValidationError(f"{field} must be a string.")

    normalized = " ".join(unicodedata.normalize("NFKC", value).split())
    if not normalized:
        raise ValidationError(f"{field} cannot be empty.")
    if len(normalized) > max_length:
        raise ValidationError(f"{field} must be at most {max_length} characters.")
    if any(unicodedata.category(char).startswith("C") for char in normalized):
        raise ValidationError(f"{field} contains unsupported control characters.")

    return normalized, normalized.casefold()


def _validate_identifier(value: str, field: str, max_length: int = 128) -> str:
    normalized, _ = _normalize_text(value, field, max_length)
    return normalized


def _score_parts(value: float) -> tuple[float, int]:
    if isinstance(value, bool):
        raise ValidationError("f1Score must be a finite number between 0 and 1.")
    try:
        score = float(value)
    except (TypeError, ValueError, OverflowError):
        raise ValidationError(
            "f1Score must be a finite number between 0 and 1."
        ) from None
    if not math.isfinite(score) or score < 0.0 or score > 1.0:
        raise ValidationError("f1Score must be a finite number between 0 and 1.")

    score_micros = int(round(score * 1_000_000))
    return score_micros / 1_000_000, score_micros


class LeaderboardStore:
    """Thread-safe, file-backed store intended to be owned by the server process.

    ``season`` is constructor configuration, so API callers cannot choose which
    season receives a score. A challenge is created internally for each dataset.
    """

    def __init__(
        self,
        db_path: str | Path,
        season: str = "default",
        *,
        token_pepper: str | bytes = b"",
    ) -> None:
        raw_path = str(db_path)
        if not raw_path:
            raise ValidationError("dbPath cannot be empty.")

        self.season, self._season_key = _normalize_text(season, "season", 64)
        if isinstance(token_pepper, str):
            token_pepper = token_pepper.encode("utf-8")
        if not isinstance(token_pepper, bytes):
            raise ValidationError("tokenPepper must be bytes or a string.")
        self._token_pepper = token_pepper
        self._memory_lock = threading.RLock()
        self._memory_connection: sqlite3.Connection | None = None

        if raw_path == ":memory:":
            self.db_path = raw_path
            self._memory_connection = sqlite3.connect(
                ":memory:", isolation_level=None, check_same_thread=False
            )
            self._configure_connection(self._memory_connection)
        else:
            path = Path(db_path).expanduser()
            path.parent.mkdir(parents=True, exist_ok=True)
            self.db_path = str(path)

        with self._connection() as connection:
            if self._memory_connection is None:
                connection.execute("PRAGMA journal_mode = WAL")
            connection.executescript(_SCHEMA)

    @staticmethod
    def _configure_connection(connection: sqlite3.Connection) -> None:
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA foreign_keys = ON")
        connection.execute("PRAGMA busy_timeout = 5000")
        connection.execute("PRAGMA synchronous = NORMAL")

    @contextmanager
    def _connection(self) -> Iterator[sqlite3.Connection]:
        if self._memory_connection is not None:
            with self._memory_lock:
                yield self._memory_connection
            return

        connection = sqlite3.connect(self.db_path, isolation_level=None, timeout=5.0)
        self._configure_connection(connection)
        try:
            yield connection
        finally:
            connection.close()

    @contextmanager
    def _write(self) -> Iterator[sqlite3.Connection]:
        with self._connection() as connection:
            connection.execute("BEGIN IMMEDIATE")
            try:
                yield connection
            except Exception:
                connection.rollback()
                raise
            else:
                connection.commit()

    def _hash_token(self, token: str) -> bytes:
        token_bytes = token.encode("utf-8")
        if self._token_pepper:
            return hmac.new(self._token_pepper, token_bytes, hashlib.sha256).digest()
        return hashlib.sha256(token_bytes).digest()

    def _challenge_values(self, dataset: str) -> tuple[str, str, str]:
        dataset_name, dataset_key = _normalize_text(dataset, "dataset", 64)
        challenge_id = str(
            uuid.uuid5(
                _CHALLENGE_NAMESPACE,
                f"{self._season_key}\x1f{dataset_key}",
            )
        )
        return challenge_id, dataset_name, dataset_key

    def register_player(self, display_name: str) -> dict:
        display_name, display_name_key = _normalize_text(
            display_name, "displayName", 32
        )
        player_id = str(uuid.uuid4())
        token = "lb_" + secrets.token_urlsafe(32)
        created_at = _now_utc()

        try:
            with self._write() as connection:
                connection.execute(
                    """
                    INSERT INTO players
                        (player_id, display_name, display_name_key, token_hash, created_at)
                    VALUES (?, ?, ?, ?, ?)
                    """,
                    (
                        player_id,
                        display_name,
                        display_name_key,
                        self._hash_token(token),
                        created_at,
                    ),
                )
        except sqlite3.IntegrityError:
            raise ConflictError("That display name is already in use.") from None

        return {
            "playerId": player_id,
            "displayName": display_name,
            "token": token,
            "createdAt": created_at,
        }

    def authenticate(self, token: str) -> dict:
        if not isinstance(token, str):
            raise AuthenticationError("Invalid player token.")
        token = token.strip()
        if token.lower().startswith("bearer "):
            token = token[7:].strip()
        if not token:
            raise AuthenticationError("Invalid player token.")

        with self._connection() as connection:
            row = connection.execute(
                """
                SELECT player_id, display_name, created_at
                FROM players
                WHERE token_hash = ?
                """,
                (self._hash_token(token),),
            ).fetchone()

        if row is None:
            raise AuthenticationError("Invalid player token.")
        return {
            "playerId": row["player_id"],
            "displayName": row["display_name"],
            "createdAt": row["created_at"],
        }

    def record_score(
        self,
        player_id: str,
        dataset: str,
        checkpoint_id: str,
        model_name: str,
        f1_score: float,
    ) -> dict:
        player_id = _validate_identifier(player_id, "playerId")
        checkpoint_id = _validate_identifier(checkpoint_id, "checkpointId")
        model_name, _ = _normalize_text(model_name, "modelName", 80)
        score, score_micros = _score_parts(f1_score)
        challenge_id, dataset_name, dataset_key = self._challenge_values(dataset)
        evaluation_id = str(uuid.uuid4())
        recorded_at = _now_utc()

        try:
            with self._write() as connection:
                player = connection.execute(
                    "SELECT 1 FROM players WHERE player_id = ?", (player_id,)
                ).fetchone()
                if player is None:
                    raise NotFoundError("Player not found.")

                connection.execute(
                    """
                    INSERT INTO challenges
                        (challenge_id, season, season_key, dataset, dataset_key, created_at)
                    VALUES (?, ?, ?, ?, ?, ?)
                    ON CONFLICT (season_key, dataset_key) DO NOTHING
                    """,
                    (
                        challenge_id,
                        self.season,
                        self._season_key,
                        dataset_name,
                        dataset_key,
                        recorded_at,
                    ),
                )
                challenge = connection.execute(
                    """
                    SELECT challenge_id, season, dataset
                    FROM challenges
                    WHERE season_key = ? AND dataset_key = ?
                    """,
                    (self._season_key, dataset_key),
                ).fetchone()
                challenge_id = challenge["challenge_id"]

                previous = connection.execute(
                    """
                    SELECT score_micros FROM best_scores
                    WHERE challenge_id = ? AND player_id = ?
                    """,
                    (challenge_id, player_id),
                ).fetchone()
                is_personal_best = (
                    previous is None or score_micros > previous["score_micros"]
                )

                connection.execute(
                    """
                    INSERT INTO evaluations
                        (evaluation_id, challenge_id, player_id, checkpoint_id,
                         model_name, f1_score, score_micros, recorded_at)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        evaluation_id,
                        challenge_id,
                        player_id,
                        checkpoint_id,
                        model_name,
                        score,
                        score_micros,
                        recorded_at,
                    ),
                )
                connection.execute(
                    """
                    INSERT INTO best_scores
                        (challenge_id, player_id, evaluation_id, score_micros, achieved_at)
                    VALUES (?, ?, ?, ?, ?)
                    ON CONFLICT (challenge_id, player_id) DO UPDATE SET
                        evaluation_id = excluded.evaluation_id,
                        score_micros = excluded.score_micros,
                        achieved_at = excluded.achieved_at
                    WHERE excluded.score_micros > best_scores.score_micros
                    """,
                    (
                        challenge_id,
                        player_id,
                        evaluation_id,
                        score_micros,
                        recorded_at,
                    ),
                )
                best = connection.execute(
                    """
                    SELECT score_micros FROM best_scores
                    WHERE challenge_id = ? AND player_id = ?
                    """,
                    (challenge_id, player_id),
                ).fetchone()
        except sqlite3.IntegrityError as error:
            if "evaluations.challenge_id, evaluations.player_id, evaluations.checkpoint_id" in str(
                error
            ):
                raise ConflictError(
                    "That checkpoint has already been evaluated for this challenge."
                ) from None
            raise

        return {
            "evaluationId": evaluation_id,
            "challengeId": challenge_id,
            "playerId": player_id,
            "season": challenge["season"],
            "dataset": challenge["dataset"],
            "checkpointId": checkpoint_id,
            "modelName": model_name,
            "f1Score": score,
            "recordedAt": recorded_at,
            "isPersonalBest": is_personal_best,
            "personalBestF1Score": best["score_micros"] / 1_000_000,
        }

    def get_leaderboard(
        self,
        dataset: str,
        limit: int = 50,
        offset: int = 0,
        caller_player_id: str | None = None,
    ) -> dict:
        if isinstance(limit, bool) or not isinstance(limit, int):
            raise ValidationError("limit must be an integer.")
        if limit < 1 or limit > _MAX_PAGE_SIZE:
            raise ValidationError(f"limit must be between 1 and {_MAX_PAGE_SIZE}.")
        if isinstance(offset, bool) or not isinstance(offset, int) or offset < 0:
            raise ValidationError("offset must be a non-negative integer.")
        if caller_player_id is not None:
            caller_player_id = _validate_identifier(
                caller_player_id, "callerPlayerId"
            )

        expected_id, dataset_name, dataset_key = self._challenge_values(dataset)
        with self._connection() as connection:
            challenge = connection.execute(
                """
                SELECT challenge_id, season, dataset
                FROM challenges
                WHERE season_key = ? AND dataset_key = ?
                """,
                (self._season_key, dataset_key),
            ).fetchone()
            if challenge is None:
                return {
                    "challengeId": expected_id,
                    "season": self.season,
                    "dataset": dataset_name,
                    "metric": "macroF1",
                    "totalPlayers": 0,
                    "limit": limit,
                    "offset": offset,
                    "callerRank": None,
                    "entries": [],
                }

            challenge_id = challenge["challenge_id"]
            ranked_sql = """
                WITH ranked AS (
                    SELECT
                        DENSE_RANK() OVER (ORDER BY best.score_micros DESC) AS rank,
                        best.score_micros,
                        best.achieved_at,
                        players.player_id,
                        players.display_name,
                        evaluations.checkpoint_id,
                        evaluations.model_name
                    FROM best_scores AS best
                    JOIN players ON players.player_id = best.player_id
                    JOIN evaluations
                      ON evaluations.evaluation_id = best.evaluation_id
                    WHERE best.challenge_id = ?
                )
            """
            rows = connection.execute(
                ranked_sql
                + """
                SELECT * FROM ranked
                ORDER BY score_micros DESC, achieved_at ASC, player_id ASC
                LIMIT ? OFFSET ?
                """,
                (challenge_id, limit, offset),
            ).fetchall()
            total_players = connection.execute(
                "SELECT COUNT(*) FROM best_scores WHERE challenge_id = ?",
                (challenge_id,),
            ).fetchone()[0]
            caller_rank = None
            if caller_player_id is not None:
                caller = connection.execute(
                    ranked_sql + "SELECT rank FROM ranked WHERE player_id = ?",
                    (challenge_id, caller_player_id),
                ).fetchone()
                if caller is not None:
                    caller_rank = int(caller["rank"])

        entries = [
            {
                "rank": int(row["rank"]),
                "playerId": row["player_id"],
                "displayName": row["display_name"],
                "f1Score": row["score_micros"] / 1_000_000,
                "checkpointId": row["checkpoint_id"],
                "modelName": row["model_name"],
                "achievedAt": row["achieved_at"],
            }
            for row in rows
        ]
        return {
            "challengeId": challenge_id,
            "season": challenge["season"],
            "dataset": challenge["dataset"],
            "metric": "macroF1",
            "totalPlayers": total_players,
            "limit": limit,
            "offset": offset,
            "callerRank": caller_rank,
            "entries": entries,
        }
