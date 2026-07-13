import os
import tempfile
import unittest


_temporary_directory = tempfile.TemporaryDirectory()
os.environ["SERVER_DATA_DIR"] = _temporary_directory.name
os.environ["LEADERBOARD_DB_PATH"] = os.path.join(
    _temporary_directory.name, "leaderboard.db"
)
os.environ["COMPUTE_DB_PATH"] = os.path.join(
    _temporary_directory.name, "compute.db"
)

from app import (
    compute_status,
    leaderboard,
    register_player,
    register_worker,
    require_player,
)
from leaderboard_store import AuthenticationError


class ApiSmokeTests(unittest.TestCase):
    def test_player_worker_registration_and_compute_threshold(self):
        players = [
            register_player({"displayName": name})["player"]
            for name in ("Player One", "Player Two")
        ]
        for index, player in enumerate(players):
            register_worker(
                {"name": f"Worker {index + 1}"},
                player={
                    "playerId": player["playerId"],
                    "displayName": player["displayName"],
                },
            )

        status = compute_status()
        self.assertEqual(status["activeWorkers"], 2)
        self.assertEqual(status["mode"], "remote_workers")

        board = leaderboard(dataset="MNIST", limit=50, offset=0, player=None)
        self.assertTrue(board["success"])
        self.assertEqual(board["leaderboard"]["entries"], [])

    def test_protected_route_rejects_missing_player_token(self):
        with self.assertRaises(AuthenticationError):
            require_player(None)


def tearDownModule():
    _temporary_directory.cleanup()


if __name__ == "__main__":
    unittest.main()
