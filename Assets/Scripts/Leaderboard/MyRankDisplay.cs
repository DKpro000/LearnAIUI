using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;

/// <summary>
/// Displays the current user's rank and score from the leaderboard.
/// Automatically retrieves the current player's ID from PlayerPrefs.
/// Shows rank if in top 10, otherwise shows "Unranked".
/// </summary>
public class MyRankDisplay : MonoBehaviour
{
    [Header("API Configuration")]
    [Tooltip("URL of the Python leaderboard API")]
    public string apiUrl = "http://localhost:5000";

    [Header("UI References")]
    public TMP_Text playerNameText;
    public TMP_Text rankText;
    public TMP_Text scoreText;

    // PlayerPrefs key for storing the current player's ID
    private const string PlayerIdKey = "NNBuilder.PlayerId";

    private void Start()
    {
        Debug.Log("=== MyRankDisplay Started ===");
        Debug.Log($"API URL: {apiUrl}");
        FetchMyRank();
    }

    /// <summary>
    /// Fetches the current player's rank and score from the API.
    /// Automatically gets the player ID from PlayerPrefs.
    /// Shows rank if in top 10, otherwise shows "Unranked".
    /// </summary>
    private IEnumerator FetchMyRank()
    {
        // Get player ID from PlayerPrefs
        string playerId = PlayerPrefs.GetString(PlayerIdKey, "");

        Debug.Log($"PlayerId from PlayerPrefs: '{playerId}'");

        if (string.IsNullOrEmpty(playerId))
        {
            Debug.LogWarning("Player ID not found in PlayerPrefs. Make sure the user is logged in.");
            Debug.Log("To fix: Log in through the AccountLoginPanel first.");
            SetUnrankedDisplay();
            yield break;
        }

        string url = $"{apiUrl}/player/{playerId}";
        Debug.Log($"Fetching from: {url}");

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            yield return request.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
            if (request.result == UnityWebRequest.Result.ConnectionError ||
                request.result == UnityWebRequest.Result.ProtocolError)
#else
            if (request.isNetworkError || request.isHttpError)
#endif
            {
                Debug.LogError($"Failed to fetch my rank: {request.error}");
                Debug.LogError($"Response code: {request.responseCode}");
                Debug.LogError($"Download handler text: {request.downloadHandler?.text}");
                SetUnrankedDisplay();
                yield break;
            }

            string json = request.downloadHandler.text;
            Debug.Log($"API Response: {json}");

            try
            {
                // Parse the response
                MyRankInfo info = JsonUtility.FromJson<MyRankInfo>(json);

                if (info == null)
                {
                    Debug.LogError("Failed to parse my rank JSON - info is null");
                    SetUnrankedDisplay();
                    yield break;
                }

                Debug.Log($"Parsed info: Name={info.playerName}, Rank={info.rank}, Score={info.score}");

                // Update display
                if (playerNameText != null)
                {
                    playerNameText.text = info.playerName;
                    Debug.Log($"Updated playerNameText: {info.playerName}");
                }
                else
                {
                    Debug.LogError("playerNameText is NOT assigned in Inspector!");
                }

                if (rankText != null)
                {
                    // Show rank if in top 10, otherwise show "Unranked"
                    if (info.rank.HasValue && info.rank <= 10)
                    {
                        rankText.text = $"#{info.rank.Value}";
                        Debug.Log($"Updated rankText: #{info.rank.Value}");
                    }
                    else
                    {
                        rankText.text = "Unranked";
                        Debug.Log("Updated rankText: Unranked");
                    }
                }
                else
                {
                    Debug.LogError("rankText is NOT assigned in Inspector!");
                }

                if (scoreText != null)
                {
                    scoreText.text = info.score.ToString();
                    Debug.Log($"Updated scoreText: {info.score}");
                }
                else
                {
                    Debug.LogError("scoreText is NOT assigned in Inspector!");
                }

                Debug.Log($"=== My Rank Display Complete ===");
                Debug.Log($"Player: {info.playerName}");
                Debug.Log($"Rank: {info.rank?.ToString() ?? "N/A"}");
                Debug.Log($"Score: {info.score}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to parse my rank JSON: {e.Message}");
                Debug.LogError(e.StackTrace);
                SetUnrankedDisplay();
            }
        }
    }

    /// <summary>
    /// Sets all display texts to show unranked state.
    /// </summary>
    private void SetUnrankedDisplay()
    {
        if (playerNameText != null)
        {
            playerNameText.text = "Unknown Player";
            Debug.Log("Set playerNameText to 'Unknown Player'");
        }
        if (rankText != null)
        {
            rankText.text = "Unranked";
            Debug.Log("Set rankText to 'Unranked'");
        }
        if (scoreText != null)
        {
            scoreText.text = "0";
            Debug.Log("Set scoreText to '0'");
        }
    }
}

/// <summary>
/// JSON response model for /player/<id> endpoint.
/// </summary>
[System.Serializable]
public class MyRankInfo
{
    public string playerName;
    public int? rank;   // Nullable - null if player not found
    public int score;
}
