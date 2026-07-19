import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

import compute_worker


class ComputeWorkerDeviceTests(unittest.TestCase):
    def test_uses_cpu_when_cuda_is_not_available(self):
        with patch.object(compute_worker.torch.cuda, "is_available", return_value=False):
            result = compute_worker.detect_compute_device()

        self.assertEqual(result["selectedDevice"], "cpu")
        self.assertFalse(result["cudaAvailable"])

    def test_selects_cuda_after_a_successful_runtime_probe(self):
        properties = SimpleNamespace(total_memory=8 * 1024 * 1024 * 1024)
        with (
            patch.object(compute_worker.torch.cuda, "is_available", return_value=True),
            patch.object(compute_worker.torch.cuda, "device_count", return_value=1),
            patch.object(
                compute_worker.torch.cuda,
                "get_device_properties",
                return_value=properties,
            ),
            patch.object(
                compute_worker.torch.cuda,
                "get_device_name",
                return_value="Test GPU",
            ),
            patch.object(compute_worker.torch, "empty") as empty,
            patch.object(compute_worker.torch.cuda, "synchronize") as synchronize,
        ):
            result = compute_worker.detect_compute_device()

        empty.assert_called_once_with(1, device="cuda:0")
        synchronize.assert_called_once_with(0)
        self.assertEqual(result["selectedDevice"], "cuda:0")
        self.assertEqual(result["gpus"][0]["name"], "Test GPU")
        self.assertEqual(result["gpus"][0]["totalMemoryMb"], 8192)

    def test_retries_a_cuda_failure_on_cpu(self):
        client = compute_worker.WorkerClient(
            "http://server",
            "player-token",
            "Test worker",
            device_info={
                "selectedDevice": "cuda:0",
                "accelerator": "cuda",
                "cudaAvailable": True,
                "cudaBuild": "12.8",
                "gpuCount": 1,
                "gpus": [{"index": 0, "name": "Test GPU", "totalMemoryMb": 8192}],
                "fallbackReason": "",
            },
        )
        failed = {"success": False, "errors": ["CUDA out of memory"]}
        completed = {"success": True, "warnings": [], "device": "cpu"}
        job = {
            "payload": {"graph": {}, "training": {"device": "auto"}},
            "playerId": "player-a",
        }

        with (
            patch.object(
                compute_worker,
                "train_graph",
                side_effect=[failed, completed],
            ) as train,
            patch.object(compute_worker.torch.cuda, "empty_cache"),
        ):
            response = client.train_with_device_fallback(job)

        self.assertTrue(response["success"])
        self.assertEqual(train.call_count, 2)
        self.assertEqual(
            train.call_args_list[0].args[0]["training"]["device"],
            "cuda:0",
        )
        self.assertEqual(
            train.call_args_list[1].args[0]["training"]["device"],
            "cpu",
        )
        self.assertIn("completed on CPU", response["warnings"][0])

    def test_registers_detected_device_capabilities(self):
        device_info = {
            "selectedDevice": "cuda:0",
            "accelerator": "cuda",
            "cudaAvailable": True,
            "cudaBuild": "12.8",
            "gpuCount": 1,
            "gpus": [{"index": 0, "name": "Test GPU", "totalMemoryMb": 8192}],
            "fallbackReason": "",
        }
        client = compute_worker.WorkerClient(
            "http://server",
            "player-token",
            "Test worker",
            device_info=device_info,
        )
        client._request = Mock(
            return_value={"worker": {"workerId": "worker-a", "token": "worker-token"}}
        )

        client.register()

        capabilities = client._request.call_args.kwargs["payload"]["capabilities"]
        self.assertEqual(capabilities["selectedDevice"], "cuda:0")
        self.assertEqual(capabilities["gpu"], "Test GPU")
        self.assertTrue(capabilities["cuda"])


if __name__ == "__main__":
    unittest.main()
