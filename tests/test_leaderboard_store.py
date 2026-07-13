import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from leaderboard_store import (
    AuthenticationError,
    ConflictError,
    LeaderboardStore,
    NotFoundError,
    ValidationError,
)


class LeaderboardStoreTests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.db_path = Path(self.temp_dir.name) / "leaderboard.sqlite3"

    def test_player_names_are_normalized_unique_and_tokens_are_hashed(self):
        store = LeaderboardStore(self.db_path)
        player = store.register_player("  Alice   Smith  ")

        self.assertEqual(player["displayName"], "Alice Smith")
        self.assertEqual(
            store.authenticate(player["token"])["playerId"], player["playerId"]
        )
        self.assertEqual(
            store.authenticate("Bearer " + player["token"])["playerId"],
            player["playerId"],
        )
        with self.assertRaises(ConflictError):
            store.register_player("ＡLICE smith")
        with self.assertRaises(AuthenticationError):
            store.authenticate("wrong-token")

        with closing(sqlite3.connect(self.db_path)) as connection:
            token_hash = connection.execute("SELECT token_hash FROM players").fetchone()[0]
            journal_mode = connection.execute("PRAGMA journal_mode").fetchone()[0]
        self.assertNotIn(player["token"].encode(), token_hash)
        self.assertEqual(len(token_hash), 32)
        self.assertEqual(journal_mode.lower(), "wal")

    def test_history_is_append_only_and_only_improvements_replace_best(self):
        store = LeaderboardStore(self.db_path, season="Season 1")
        player = store.register_player("Player One")

        first = store.record_score(
            player["playerId"], "MNIST", "cp-1", "MLP", 0.7
        )
        lower = store.record_score(
            player["playerId"], "mnist", "cp-2", "MLP", 0.6
        )
        higher = store.record_score(
            player["playerId"], " MNIST ", "cp-3", "MLP", 0.8
        )

        self.assertTrue(first["isPersonalBest"])
        self.assertFalse(lower["isPersonalBest"])
        self.assertEqual(lower["personalBestF1Score"], 0.7)
        self.assertTrue(higher["isPersonalBest"])
        board = store.get_leaderboard("mnist")
        self.assertEqual(board["entries"][0]["f1Score"], 0.8)
        self.assertEqual(board["entries"][0]["checkpointId"], "cp-3")

        with closing(sqlite3.connect(self.db_path)) as connection:
            self.assertEqual(
                connection.execute("SELECT COUNT(*) FROM evaluations").fetchone()[0],
                3,
            )
            with self.assertRaisesRegex(sqlite3.IntegrityError, "immutable"):
                connection.execute("UPDATE evaluations SET f1_score = 1.0")
            with self.assertRaisesRegex(sqlite3.IntegrityError, "immutable"):
                connection.execute("DELETE FROM evaluations")

        with self.assertRaises(ConflictError):
            store.record_score(player["playerId"], "MNIST", "cp-3", "MLP", 0.9)

    def test_dense_ranks_pagination_deterministic_ties_and_caller_rank(self):
        store = LeaderboardStore(self.db_path, season="2026")
        players = [store.register_player(name) for name in ["A", "B", "C", "D"]]
        for index, (player, score) in enumerate(
            zip(players, [0.9, 0.8, 0.8, 0.7])
        ):
            store.record_score(
                player["playerId"], "CIFAR10", f"cp-{index}", "CNN", score
            )

        full = store.get_leaderboard(
            "cifar10", caller_player_id=players[3]["playerId"]
        )
        self.assertEqual([entry["rank"] for entry in full["entries"]], [1, 2, 2, 3])
        self.assertEqual(full["callerRank"], 3)
        page = store.get_leaderboard(
            "CIFAR10", limit=2, offset=1, caller_player_id=players[3]["playerId"]
        )
        self.assertEqual([entry["rank"] for entry in page["entries"]], [2, 2])
        self.assertEqual(page["callerRank"], 3)
        repeated = store.get_leaderboard("CIFAR10")
        self.assertEqual(
            [entry["playerId"] for entry in full["entries"]],
            [entry["playerId"] for entry in repeated["entries"]],
        )

    def test_seasons_are_isolated_and_empty_challenge_is_stable(self):
        season_one = LeaderboardStore(self.db_path, season="Season One")
        player = season_one.register_player("Seasonal Player")
        season_one.record_score(player["playerId"], "MNIST", "one", "Model", 0.75)

        season_two = LeaderboardStore(self.db_path, season="Season Two")
        empty_a = season_two.get_leaderboard(" MNIST ")
        empty_b = season_two.get_leaderboard("mnist")
        self.assertEqual(empty_a["entries"], [])
        self.assertEqual(empty_a["challengeId"], empty_b["challengeId"])
        self.assertNotEqual(
            empty_a["challengeId"],
            season_one.get_leaderboard("MNIST")["challengeId"],
        )

        season_two.record_score(player["playerId"], "mnist", "two", "Model", 0.5)
        self.assertEqual(
            season_two.get_leaderboard("MNIST")["entries"][0]["f1Score"], 0.5
        )
        self.assertEqual(
            season_one.get_leaderboard("MNIST")["entries"][0]["f1Score"], 0.75
        )

    def test_validation_and_missing_player_errors(self):
        store = LeaderboardStore(self.db_path)
        player = store.register_player("Valid")

        with self.assertRaises(ValidationError):
            store.register_player("   ")
        for score in [-0.1, 1.1, float("nan"), float("inf")]:
            with self.subTest(score=score), self.assertRaises(ValidationError):
                store.record_score(
                    player["playerId"], "MNIST", "cp", "Model", score
                )
        with self.assertRaises(NotFoundError):
            store.record_score("missing", "MNIST", "cp", "Model", 0.5)
        with self.assertRaises(ValidationError):
            store.get_leaderboard("MNIST", limit=0)
        with self.assertRaises(ValidationError):
            store.get_leaderboard("MNIST", limit=101)
        with self.assertRaises(ValidationError):
            store.get_leaderboard("MNIST", offset=-1)


if __name__ == "__main__":
    unittest.main()
