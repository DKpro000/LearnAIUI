"""Opt-in training worker that runs on a player's computer."""

from __future__ import annotations

import argparse
import copy
import json
import os
import platform
import socket
import sys
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

import torch

from checkpoint_manager import delete_checkpoint
from trainer import train_graph


WORKER_PLANE_PREFIX = "/worker-plane"


def detect_compute_device(preference: str = "auto") -> dict:
    """Probe the packaged PyTorch runtime and return a safe training device."""
    preference = (preference or "auto").strip().lower()
    result = {
        "selectedDevice": "cpu",
        "accelerator": "cpu",
        "cudaAvailable": False,
        "cudaBuild": torch.version.cuda or "",
        "gpuCount": 0,
        "gpus": [],
        "fallbackReason": "",
    }
    if preference == "cpu":
        result["fallbackReason"] = "CPU was selected by worker configuration."
        return result

    try:
        if not torch.cuda.is_available():
            result["fallbackReason"] = (
                "The packaged PyTorch runtime or this computer has no usable "
                "NVIDIA CUDA device."
            )
            return result

        device_count = torch.cuda.device_count()
        gpus = []
        for index in range(device_count):
            properties = torch.cuda.get_device_properties(index)
            gpus.append(
                {
                    "index": index,
                    "name": torch.cuda.get_device_name(index),
                    "totalMemoryMb": int(properties.total_memory // (1024 * 1024)),
                }
            )

        # A small allocation catches missing/incompatible drivers before the
        # worker registers itself as GPU-capable.
        torch.empty(1, device="cuda:0")
        torch.cuda.synchronize(0)
        result.update(
            {
                "selectedDevice": "cuda:0",
                "accelerator": "cuda",
                "cudaAvailable": True,
                "gpuCount": device_count,
                "gpus": gpus,
            }
        )
        return result
    except Exception as error:
        result["fallbackReason"] = f"CUDA probe failed: {error}"
        return result


def _is_gpu_failure(response: dict) -> bool:
    if response.get("success"):
        return False
    error_text = "\n".join(str(item) for item in response.get("errors", [])).lower()
    markers = (
        "cuda",
        "cudnn",
        "cublas",
        "gpu",
        "out of memory",
        "device-side",
        "driver",
    )
    return any(marker in error_text for marker in markers)


def supported_datasets() -> list[str]:
    supported = ["MNIST", "FashionMNIST", "CIFAR10"]
    dataset_root = Path(
        os.environ.get(
            "NN_BUILDER_LOCAL_DATASET_DIR",
            Path(__file__).resolve().parent / "dataset",
        )
    )
    local_datasets = {
        "ChihuahuaMuffin": "chiwawa_muffin",
        "Titanic": "titanic",
        "WeatherPrediction": "weather_prediction",
    }
    for dataset_name, folder_name in local_datasets.items():
        if (dataset_root / folder_name).exists():
            supported.append(dataset_name)
    return supported


class WorkerClient:
    def __init__(
        self,
        server_url: str,
        player_token: str,
        name: str,
        *,
        device_preference: str = "auto",
        device_info: dict | None = None,
    ) -> None:
        self.server_url = server_url.rstrip("/")
        self.player_token = player_token
        self.name = name
        self.worker_token = ""
        self.worker_id = ""
        self.device_info = device_info or detect_compute_device(device_preference)

    def _request(
        self,
        path: str,
        method: str = "POST",
        payload: dict | None = None,
        body: bytes | None = None,
        worker_auth: bool = True,
        timeout: int = 30,
    ) -> dict:
        headers = {}
        token = self.worker_token if worker_auth else self.player_token
        if token:
            headers["Authorization"] = f"Bearer {token}"
        if body is None:
            body = json.dumps(payload or {}).encode("utf-8")
            headers["Content-Type"] = "application/json"
        else:
            headers["Content-Type"] = "application/octet-stream"
        request = urllib.request.Request(
            self.server_url + path,
            data=body,
            headers=headers,
            method=method,
        )
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as error:
            detail = error.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"Server returned HTTP {error.code}: {detail}") from error

    def register(self) -> None:
        capabilities = {
            "hostname": socket.gethostname(),
            "platform": platform.platform(),
            "torchVersion": torch.__version__,
            "cuda": self.device_info["cudaAvailable"],
            "cudaBuild": self.device_info["cudaBuild"],
            "selectedDevice": self.device_info["selectedDevice"],
            "accelerator": self.device_info["accelerator"],
            "gpuCount": self.device_info["gpuCount"],
            "gpus": self.device_info["gpus"],
            "supportedDatasets": supported_datasets(),
            "torchThreads": torch.get_num_threads(),
        }
        if self.device_info["gpus"]:
            capabilities["gpu"] = self.device_info["gpus"][0]["name"]
        response = self._request(
            WORKER_PLANE_PREFIX + "/workers/register",
            payload={"name": self.name, "capabilities": capabilities},
            worker_auth=False,
        )
        worker = response["worker"]
        self.worker_id = worker["workerId"]
        self.worker_token = worker["token"]

    def claim(self) -> dict | None:
        return self._request(WORKER_PLANE_PREFIX + "/jobs/claim").get("job")

    def renew(self, job_id: str) -> None:
        self._request(f"{WORKER_PLANE_PREFIX}/jobs/{job_id}/heartbeat")

    def fail(self, job_id: str, error: str, retry: bool = True) -> None:
        self._request(
            f"{WORKER_PLANE_PREFIX}/jobs/{job_id}/fail",
            payload={"error": error[:2000], "retry": retry},
        )

    def complete(self, job_id: str, checkpoint_path: str) -> dict:
        artifact = Path(checkpoint_path).read_bytes()
        return self._request(
            f"{WORKER_PLANE_PREFIX}/jobs/{job_id}/complete",
            body=artifact,
            timeout=300,
        )

    def train_with_device_fallback(self, job: dict) -> dict:
        payload = copy.deepcopy(job["payload"])
        training = payload.setdefault("training", {})
        selected_device = self.device_info["selectedDevice"]
        training["device"] = selected_device
        response = train_graph(
            payload,
            owner_player_id=job.get("playerId"),
        )
        if not selected_device.startswith("cuda") or not _is_gpu_failure(response):
            return response

        first_error = "; ".join(
            str(item) for item in response.get("errors", [])[:1]
        )
        print(
            "GPU training failed; retrying this job on CPU. "
            + (first_error or "Unknown CUDA error.")
        )
        try:
            torch.cuda.empty_cache()
        except Exception:
            pass
        cpu_payload = copy.deepcopy(job["payload"])
        cpu_payload.setdefault("training", {})["device"] = "cpu"
        cpu_response = train_graph(
            cpu_payload,
            owner_player_id=job.get("playerId"),
        )
        if cpu_response.get("success"):
            cpu_response.setdefault("warnings", []).append(
                "The worker detected a GPU but CUDA training failed, so this "
                "job was completed on CPU."
            )
        return cpu_response

    def run_job(self, job: dict) -> None:
        job_id = job["jobId"]
        stop_heartbeat = threading.Event()

        def heartbeat_loop() -> None:
            while not stop_heartbeat.wait(30):
                try:
                    self.renew(job_id)
                except Exception as error:
                    print(f"Lease heartbeat failed: {error}")

        heartbeat = threading.Thread(target=heartbeat_loop, daemon=True)
        heartbeat.start()
        response = None
        try:
            print(f"Training job {job_id} (attempt {job.get('attempt', 1)})")
            response = self.train_with_device_fallback(job)
            if not response.get("success"):
                errors = response.get("errors", [])
                self.fail(job_id, "; ".join(errors) or "Training failed.")
                return
            self.complete(job_id, response["checkpointPath"])
            print(f"Completed training job {job_id}")
        except Exception as error:
            print(f"Training job {job_id} failed: {error}")
            try:
                self.fail(job_id, str(error), retry=True)
            except Exception as report_error:
                print(f"Could not report failure: {report_error}")
        finally:
            stop_heartbeat.set()
            heartbeat.join(timeout=2)
            if response and response.get("checkpointId"):
                try:
                    delete_checkpoint(response["checkpointId"])
                except Exception:
                    pass

    def run(self, poll_seconds: float, once: bool = False) -> None:
        print(
            "Training device: "
            f"{self.device_info['selectedDevice']} "
            f"(PyTorch CUDA build: {self.device_info['cudaBuild'] or 'none'})"
        )
        if self.device_info["fallbackReason"]:
            print(f"Device selection note: {self.device_info['fallbackReason']}")
        self.register()
        print(f"Worker {self.worker_id} connected to {self.server_url}")
        while True:
            try:
                job = self.claim()
                if job is not None:
                    self.run_job(job)
                    if once:
                        return
                elif once:
                    return
            except KeyboardInterrupt:
                return
            except Exception as error:
                print(f"Worker connection error: {error}")
            time.sleep(poll_seconds)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Use this computer as an opt-in Neural Network Builder worker."
    )
    parser.add_argument(
        "--server",
        default=os.environ.get("COMPUTE_SERVER_URL", ""),
    )
    parser.add_argument(
        "--player-token",
        default=os.environ.get("PLAYER_TOKEN", ""),
    )
    parser.add_argument(
        "--name",
        default="",
    )
    parser.add_argument(
        "--config",
        default="",
        help="Path to compute-worker.json written by the Unity client.",
    )
    parser.add_argument("--poll-seconds", type=float, default=2.0)
    parser.add_argument(
        "--device",
        choices=("auto", "cpu", "cuda"),
        default=os.environ.get("NN_BUILDER_WORKER_DEVICE", "auto"),
        help="Training device preference; auto probes CUDA and falls back to CPU.",
    )
    parser.add_argument(
        "--torch-threads",
        type=int,
        default=int(os.environ.get("NN_BUILDER_TORCH_THREADS", "0")),
        help="CPU threads used by PyTorch; default is half the logical CPUs.",
    )
    parser.add_argument(
        "--log-file",
        default="",
        help="Append worker output to this file.",
    )
    parser.add_argument(
        "--diagnose-device",
        action="store_true",
        help="Detect and report the training device, then exit without connecting.",
    )
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()

    if args.config:
        config = json.loads(Path(args.config).read_text(encoding="utf-8"))
        args.server = args.server or config.get("serverUrl", "")
        args.player_token = args.player_token or config.get("playerToken", "")
        args.name = args.name or config.get("name", "")

    args.server = args.server or "http://127.0.0.1:8000"
    args.name = args.name or f"{socket.gethostname()} worker"
    if args.log_file:
        log_path = Path(args.log_file)
        log_path.parent.mkdir(parents=True, exist_ok=True)
        log_stream = log_path.open("a", encoding="utf-8", buffering=1)
        sys.stdout = log_stream
        sys.stderr = log_stream
        print(f"\nWorker starting at {time.strftime('%Y-%m-%d %H:%M:%S')}")
    if not args.player_token and not args.diagnose_device:
        parser.error("Provide --config, --player-token, or set PLAYER_TOKEN.")
    thread_count = args.torch_threads
    if thread_count <= 0:
        thread_count = max(1, min(8, (os.cpu_count() or 2) // 2))
    torch.set_num_threads(thread_count)
    try:
        torch.set_num_interop_threads(1)
    except RuntimeError:
        pass
    print(f"PyTorch CPU threads: {thread_count}")
    device_info = detect_compute_device(args.device)
    if args.diagnose_device:
        print(json.dumps(device_info, indent=2))
        return
    WorkerClient(
        args.server,
        args.player_token,
        args.name,
        device_preference=args.device,
        device_info=device_info,
    ).run(
        max(0.5, args.poll_seconds),
        once=args.once,
    )


if __name__ == "__main__":
    main()
