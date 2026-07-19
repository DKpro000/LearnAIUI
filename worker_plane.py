"""Database-free HTTP worker plane.

This module deliberately does not import SQLite stores or the scheduler. Every
state transition and assignment is delegated to the authoritative control
plane after worker/player authentication.
"""

from __future__ import annotations

from typing import Protocol

from fastapi import APIRouter, Depends, Header, HTTPException, Request


class WorkerPlaneGateway(Protocol):
    @property
    def maximum_artifact_bytes(self) -> int: ...

    def authenticate_player(self, token: str) -> dict: ...
    def authenticate_worker(self, token: str) -> dict: ...
    def register_worker(self, payload: dict, player: dict) -> dict: ...
    def heartbeat_worker(self, worker: dict) -> dict: ...
    def claim_worker_job(self, worker: dict) -> dict | None: ...
    def renew_worker_job(self, job_id: str, worker: dict) -> dict: ...
    def complete_worker_job(
        self, job_id: str, worker: dict, artifact: bytes
    ) -> dict: ...
    def fail_worker_job(
        self, job_id: str, worker: dict, payload: dict
    ) -> dict: ...


def _bearer_token(authorization: str | None) -> str:
    if not authorization:
        return ""
    prefix, separator, token = authorization.partition(" ")
    if not separator or prefix.lower() != "bearer":
        return ""
    return token.strip()


def create_worker_plane_router(
    gateway: WorkerPlaneGateway,
    *,
    prefix: str = "/worker-plane",
    include_in_schema: bool = True,
) -> APIRouter:
    router = APIRouter(
        prefix=prefix,
        tags=["worker-plane"],
        include_in_schema=include_in_schema,
    )

    def require_player(authorization: str | None = Header(default=None)) -> dict:
        return gateway.authenticate_player(_bearer_token(authorization))

    def require_worker(authorization: str | None = Header(default=None)) -> dict:
        return gateway.authenticate_worker(_bearer_token(authorization))

    @router.post("/workers/register", status_code=201)
    def register_worker(
        payload: dict,
        player: dict = Depends(require_player),
    ):
        worker = gateway.register_worker(payload, player)
        return {"success": True, "worker": worker, "errors": []}

    @router.post("/workers/heartbeat")
    def worker_heartbeat(worker: dict = Depends(require_worker)):
        heartbeat = gateway.heartbeat_worker(worker)
        return {"success": True, "heartbeat": heartbeat, "errors": []}

    @router.post("/jobs/claim")
    def claim_job(worker: dict = Depends(require_worker)):
        job = gateway.claim_worker_job(worker)
        return {"success": True, "job": job, "errors": []}

    @router.post("/jobs/{job_id}/heartbeat")
    def job_heartbeat(job_id: str, worker: dict = Depends(require_worker)):
        lease = gateway.renew_worker_job(job_id, worker)
        return {"success": True, "lease": lease, "errors": []}

    @router.post("/jobs/{job_id}/complete")
    async def complete_job(
        job_id: str,
        request: Request,
        worker: dict = Depends(require_worker),
    ):
        content_length = int(request.headers.get("content-length", "0") or "0")
        if content_length > gateway.maximum_artifact_bytes:
            raise HTTPException(
                status_code=422,
                detail="Worker checkpoint exceeds the upload limit.",
            )
        chunks: list[bytes] = []
        received = 0
        async for chunk in request.stream():
            received += len(chunk)
            if received > gateway.maximum_artifact_bytes:
                raise HTTPException(
                    status_code=422,
                    detail="Worker checkpoint exceeds the upload limit.",
                )
            chunks.append(chunk)
        artifact = b"".join(chunks)
        result = gateway.complete_worker_job(job_id, worker, artifact)
        return {"success": True, "result": result, "errors": []}

    @router.post("/jobs/{job_id}/fail")
    def fail_job(
        job_id: str,
        payload: dict,
        worker: dict = Depends(require_worker),
    ):
        result = gateway.fail_worker_job(job_id, worker, payload)
        return {"success": True, "job": result, "errors": []}

    return router
