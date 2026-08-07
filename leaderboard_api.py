"""
leaderboard_api.py — lightweight HTTP API for reading leaderboard.db

Matches the shape LeaderboardManager.cs expects:
    List<PlayerData> { playerName: string, score: int }

Usage:
    python3 leaderboard_api.py [--port 5000]

Then in Unity / C#, fetch:
    GET http://localhost:5000/leaderboard
"""

from __future__ import annotations

import json
import sqlite3
import sys
from http.server import HTTPServer, BaseHTTPRequestHandler
from pathlib import Path
from typing import Iterable

# ── Paths ────────────────────────────────────────────────────────────────────

# Database is in the NNBuilderData subfolder
DB_PATH = Path(__file__).parent / "NNBuilderData" / "leaderboard.db"
PORT = int(sys.argv[sys.argv.index("--port") + 1]) if "--port" in sys.argv else 5000

# ── Data access ──────────────────────────────────────────────────────────────

def fetch_player_info(player_id: str) -> dict:
    """Return {playerName, score, rank} for a specific player, or None if not found."""
    conn = sqlite3.connect(str(DB_PATH))
    conn.row_factory = sqlite3.Row
    try:
        # Get player's best score and display name
        player = conn.execute(
            """
            SELECT p.display_name, bs.score_micros
            FROM best_scores bs
            JOIN players p ON p.player_id = bs.player_id
            WHERE bs.player_id = ?
            """,
            (player_id,),
        ).fetchone()

        if player is None:
            return None

        # Calculate rank: count how many distinct higher scores exist, then add 1
        rank_row = conn.execute(
            """
            SELECT COUNT(DISTINCT score_micros) + 1 AS rank
            FROM best_scores
            WHERE score_micros > ?
            """,
            (player["score_micros"],),
        ).fetchone()

        rank = int(rank_row["rank"])
        return {
            "playerName": player["display_name"],
            "score": player["score_micros"],
            "rank": rank,
        }
    finally:
        conn.close()


def fetch_leaderboard() -> list[dict]:
    """Return list of {rank, playerName, score} ordered by score_micros DESC."""
    conn = sqlite3.connect(str(DB_PATH))
    conn.row_factory = sqlite3.Row
    try:
        cursor = conn.execute(
            """
            SELECT
                DENSE_RANK() OVER (ORDER BY bs.score_micros DESC) AS rank,
                p.display_name,
                bs.score_micros
            FROM best_scores bs
            JOIN players p ON p.player_id = bs.player_id
            ORDER BY bs.score_micros DESC
            LIMIT 10
            """
        )
        rows = cursor.fetchall()
    finally:
        conn.close()

    # score_micros is an integer like 837576
    return [
        {"rank": int(row["rank"]), "playerName": row["display_name"], "score": row["score_micros"]}
        for row in rows
    ]


# ── HTTP handler ─────────────────────────────────────────────────────────────

class Handler(BaseHTTPRequestHandler):

    def do_GET(self) -> None:
        if self.path == "/leaderboard":
            data = fetch_leaderboard()
            body = json.dumps({"players": data}, indent=2).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        elif self.path.startswith("/player/"):
            # Extract player_id from path: /player/<player_id>
            player_id = self.path.split("/player/")[1].strip()
            if not player_id:
                self.send_response(400)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(json.dumps({"error": "Missing player_id"}).encode())
                return

            data = fetch_player_info(player_id)
            if data is None:
                self.send_response(404)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(json.dumps({"error": "Player not found"}).encode())
            else:
                body = json.dumps(data, indent=2).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        else:
            self.send_response(404)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({"error": "Not found"}).encode())

    # Silence request logs
    def log_message(self, fmt: str, *args: object) -> None:
        pass


# ── Main ─────────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    server = HTTPServer(("0.0.0.0", PORT), Handler)
    print(f"Leaderboard API running at http://localhost:{PORT}/leaderboard")
    print(f"Database : {DB_PATH}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nShutting down.")
        server.server_close()
