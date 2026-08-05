using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Fetches leaderboard data from the Python API and feeds it to LeaderboardManager.
/// </summary>
public class LeaderboardAPIClient : MonoBehaviour
{
    [Header("API Configuration")]
    [Tooltip("URL of the Python leaderboard API (leaderboard_api.py)")]
    public string apiUrl = "http://localhost:5000/leaderboard";

    [Header("References")]
    public LeaderboardManager leaderboardManager;

    private void Start()
    {
        StartCoroutine(FetchAndPopulateLeaderboard());
    }

    private IEnumerator FetchAndPopulateLeaderboard()
    {
        using (UnityWebRequest request = UnityWebRequest.Get(apiUrl))
        {
            yield return request.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
            if (request.result == UnityWebRequest.Result.ConnectionError ||
                request.result == UnityWebRequest.Result.ProtocolError)
#else
            if (request.isNetworkError || request.isHttpError)
#endif
            {
                Debug.LogError($"Failed to fetch leaderboard: {request.error}");
                yield break;
            }

            string json = request.downloadHandler.text;
            Debug.Log($"Raw JSON response:\n{json}");

            try
            {
                // Parse the root wrapper object
                LeaderboardResponses response = JsonUtility.FromJson<LeaderboardResponses>(json);

                if (response == null)
                {
                    Debug.LogError("Failed to parse JSON - response is null");
                    yield break;
                }

                if (response.players == null)
                {
                    Debug.LogError("Failed to parse JSON - players array is null");
                    yield break;
                }

                // Convert to PlayerData list
                List<PlayerData> players = new List<PlayerData>();
                foreach (PlayerEntry entry in response.players)
                {
                    if (entry != null)
                    {
                        players.Add(new PlayerData(entry.rank, entry.playerName, entry.score));
                    }
                }

                Debug.Log($"Loaded {players.Count} players from leaderboard API");

                if (leaderboardManager != null)
                {
                    leaderboardManager.PopulateLeaderboard(players);
                }
                else
                {
                    Debug.LogError("LeaderboardManager reference is missing! Assign it in the Inspector.");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to parse leaderboard JSON: {e.Message}");
                Debug.LogError($"Exception type: {e.GetType().Name}");
            }
        }
    }
}

/// <summary>
/// Root JSON wrapper: { "players": [ {...}, {...} ] }
/// Must be public and top-level for JsonUtility.
/// </summary>
[System.Serializable]
public class LeaderboardResponses
{
    public PlayerEntry[] players;
}

/// <summary>
/// Each leaderboard entry from the API.
/// </summary>
[System.Serializable]
public class PlayerEntry
{
    public int rank;
    public string playerName;
    public int score;
}
