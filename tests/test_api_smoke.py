import os
import inspect
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
os.environ["PLAYER_TOKEN_PEPPER"] = "test-only-control-plane-pepper-32-chars"

from app import (
    control_plane,
    compute_status,
    leaderboard,
    login_account,
    register_account,
    require_player,
)
from leaderboard_store import AuthenticationError
import worker_plane


class ApiSmokeTests(unittest.TestCase):
    def test_player_worker_registration_and_compute_threshold(self):
        players = [
            register_account(
                {
                    "email": f"smoke_player_{index}@example.com",
                    "password": "correct-horse-battery-staple",
                    "confirmPassword": "correct-horse-battery-staple",
                    "displayName": name,
                }
            )["player"]
            for index, name in enumerate(("Player One", "Player Two"), start=1)
        ]
        for index, player in enumerate(players):
            control_plane.register_worker(
                {"name": f"Worker {index + 1}"},
                player={
                    "playerId": player["playerId"],
                    "email": player["email"],
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

    def test_login_issues_a_formal_account_session(self):
        register_account(
            {
                "email": "login_smoke@example.com",
                "password": "a-secure-test-password",
                "confirmPassword": "a-secure-test-password",
                "displayName": "Login Smoke",
            }
        )
        player = login_account(
            {
                "email": "LOGIN_SMOKE@EXAMPLE.COM",
                "password": "a-secure-test-password",
            }
        )["player"]
        authenticated = control_plane.authenticate_player(player["token"])
        self.assertEqual(authenticated["email"], "login_smoke@example.com")

    def test_worker_plane_has_no_database_store_import(self):
        source = inspect.getsource(worker_plane)
        self.assertNotIn("from compute_store", source)
        self.assertNotIn("from leaderboard_store", source)


def tearDownModule():
    _temporary_directory.cleanup()


if __name__ == "__main__":
    unittest.main()
