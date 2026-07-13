import copy
import io
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

import torch

from compute_store import ComputeStore
from distributed_training import TrainingCoordinator
from leaderboard_store import ConflictError
from model_builder import GeneratedGraphModel, get_topological_order


def small_graph_payload():
    return {
        "graph": {
            "nodes": [
                {
                    "nodeId": "dataset",
                    "definitionId": "custom.Dataset",
                    "title": "Dataset",
                    "symbol": "custom.Dataset",
                    "nodeKind": "DatasetNode",
                    "parameters": [
                        {"key": "dataset_name", "value": "MNIST", "required": True},
                        {"key": "input_shape", "value": "[1, 784]", "required": True},
                    ],
                },
                {
                    "nodeId": "linear",
                    "definitionId": "torch.nn.Linear",
                    "title": "Linear",
                    "symbol": "torch.nn.Linear",
                    "nodeKind": "ModuleNode",
                    "parameters": [
                        {"key": "in_features", "value": "784", "required": True},
                        {"key": "out_features", "value": "10", "required": True},
                        {"key": "bias", "value": "True"},
                        {"key": "device", "value": "None"},
                        {"key": "dtype", "value": "None"},
                    ],
                },
                {
                    "nodeId": "output",
                    "definitionId": "custom.Output",
                    "title": "Model Output",
                    "symbol": "custom.Output",
                    "nodeKind": "OutputNode",
                    "parameters": [
                        {"key": "output_name", "value": "F1 Score", "required": True}
                    ],
                },
            ],
            "edges": [
                {
                    "edgeId": "e1",
                    "fromNodeId": "dataset",
                    "fromPortName": "out",
                    "toNodeId": "linear",
                    "toPortName": "x",
                },
                {
                    "edgeId": "e2",
                    "fromNodeId": "linear",
                    "fromPortName": "out",
                    "toNodeId": "output",
                    "toPortName": "x",
                },
            ],
        },
        "training": {
            "dataset": "MNIST",
            "epochs": 1,
            "batchSize": 32,
            "learningRate": 0.001,
            "optimizer": "Adam",
            "loss": "CrossEntropyLoss",
            "maxTrainSamples": 100,
            "modelName": "Small",
            "weightName": "Worker",
        },
    }


class ComputeStoreTests(unittest.TestCase):
    def setUp(self):
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.store = ComputeStore(
            Path(self.temporary_directory.name) / "compute.db"
        )

    def tearDown(self):
        self.temporary_directory.cleanup()

    def test_counts_distinct_active_players_and_leases_jobs(self):
        first = self.store.register_worker("player-a", "A1")
        self.store.register_worker("player-a", "A2")
        second = self.store.register_worker("player-b", "B")
        self.assertEqual(self.store.active_worker_count(), 2)

        queued = self.store.enqueue_job("player-a", {"graph": {}, "training": {}})
        claimed = self.store.claim_next_job(first["workerId"])
        self.assertEqual(claimed["jobId"], queued["jobId"])
        self.assertIsNone(self.store.claim_next_job(second["workerId"]))

        with self.assertRaises(ConflictError):
            self.store.complete_job(queued["jobId"], second["workerId"], {})

        self.store.renew_lease(queued["jobId"], first["workerId"])
        self.store.complete_job(
            queued["jobId"], first["workerId"], {"success": True}
        )
        job = self.store.get_job(queued["jobId"], player_id="player-a")
        self.assertEqual(job["status"], "completed")
        self.assertEqual(job["result"], {"success": True})

    def test_failed_jobs_retry_at_most_three_attempts(self):
        worker = self.store.register_worker("player-a", "A")
        job = self.store.enqueue_job("player-a", {"graph": {}})
        for expected_attempt in (1, 2, 3):
            claimed = self.store.claim_next_job(worker["workerId"])
            self.assertEqual(claimed["attempt"], expected_attempt)
            result = self.store.fail_job(
                job["jobId"], worker["workerId"], "failure", retry=True
            )
        self.assertEqual(result["status"], "failed")
        self.assertEqual(self.store.get_job(job["jobId"])["status"], "failed")

    def test_worker_claims_only_supported_dataset_jobs(self):
        worker = self.store.register_worker("player-a", "CPU worker")
        unsupported = self.store.enqueue_job(
            "player-a",
            {"training": {"dataset": "ChihuahuaMuffin"}},
        )
        supported = self.store.enqueue_job(
            "player-a",
            {"training": {"dataset": "MNIST"}},
        )
        claimed = self.store.claim_next_job(
            worker["workerId"],
            supported_datasets={"MNIST", "FashionMNIST", "CIFAR10"},
        )
        self.assertEqual(claimed["jobId"], supported["jobId"])
        self.assertEqual(self.store.get_job(unsupported["jobId"])["status"], "queued")


class TrainingCoordinatorTests(unittest.TestCase):
    def setUp(self):
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.store = ComputeStore(
            Path(self.temporary_directory.name) / "compute.db"
        )
        self.coordinator = TrainingCoordinator(self.store)
        self.payload = {"graph": {"nodes": [], "edges": []}, "training": {}}
        self.coordinator.validate_payload = Mock(return_value=self.payload)

    def tearDown(self):
        self.temporary_directory.cleanup()

    @patch("distributed_training.train_graph")
    def test_uses_server_below_threshold_and_workers_at_threshold(self, train):
        train.return_value = {
            "success": True,
            "checkpointPath": "private.pt",
            "checkpointMetadata": {"checkpointPath": "private.pt"},
        }
        self.store.register_worker("player-a", "A")
        local = self.coordinator.schedule(self.payload, "player-a")
        self.assertEqual(local["executionMode"], "server")
        self.assertNotIn("checkpointPath", local)
        train.assert_called_once()

        self.store.register_worker("player-b", "B")
        remote = self.coordinator.schedule(self.payload, "player-a")
        self.assertTrue(remote["queued"])
        self.assertEqual(remote["executionMode"], "remote_worker")
        train.assert_called_once()

    @patch("distributed_training.train_graph")
    def test_queued_job_falls_back_to_server_when_workers_are_unavailable(self, train):
        train.return_value = {"success": True, "checkpointMetadata": {}}
        job = self.store.enqueue_job("player-a", self.payload)
        self.assertTrue(self.coordinator.process_fallback_once())
        completed = self.store.get_job(job["jobId"], player_id="player-a")
        self.assertEqual(completed["status"], "completed")
        self.assertEqual(completed["result"]["executionMode"], "server_fallback")

    def test_policy_accepts_canonical_graph_and_rejects_tampered_symbol(self):
        coordinator = TrainingCoordinator(self.store)
        payload = small_graph_payload()
        validated = coordinator.validate_payload(payload)
        self.assertEqual(validated["training"]["device"], "auto")

        tampered = copy.deepcopy(payload)
        tampered["graph"]["nodes"][1]["symbol"] = "os.system"
        with self.assertRaisesRegex(Exception, "invalid symbol"):
            coordinator.validate_payload(tampered)

    def test_policy_allows_cnn_flatten_width_but_rejects_huge_linear(self):
        TrainingCoordinator._validate_parameter_size(12544, "in_features")
        TrainingCoordinator._validate_node_allocation(
            "torch.nn.Linear",
            {"in_features": 12544, "out_features": 128},
        )
        with self.assertRaisesRegex(Exception, "would create"):
            TrainingCoordinator._validate_node_allocation(
                "torch.nn.Linear",
                {"in_features": 16384, "out_features": 16384},
            )

    @patch("distributed_training.save_received_checkpoint")
    def test_worker_artifact_is_strictly_loaded_and_completes_job(self, save):
        coordinator = TrainingCoordinator(self.store)
        payload = coordinator.validate_payload(small_graph_payload())
        worker = self.store.register_worker("player-a", "Worker")
        queued = self.store.enqueue_job("player-a", payload)
        claimed = self.store.claim_next_job(worker["workerId"])

        model = GeneratedGraphModel(
            payload["graph"], get_topological_order(payload["graph"])
        )
        checkpoint = {
            "model_state_dict": model.state_dict(),
            "metadata": {"numClasses": 10},
            "history": {"trainLoss": [1.0], "trainAcc": [0.5]},
        }
        artifact = io.BytesIO()
        torch.save(checkpoint, artifact)
        save.return_value = {
            "checkpointId": "server-checkpoint",
            "checkpointPath": "private.pt",
        }

        result = coordinator.accept_worker_artifact(
            claimed["jobId"], worker["workerId"], artifact.getvalue()
        )
        self.assertTrue(result["success"])
        self.assertEqual(result["checkpointId"], "server-checkpoint")
        self.assertNotIn("checkpointPath", result["checkpointMetadata"])
        self.assertEqual(
            self.store.get_job(queued["jobId"])["status"], "completed"
        )


if __name__ == "__main__":
    unittest.main()
