using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;

/// <summary>
/// Displays the current user's rank and score from the leaderboard.
/// Automatically retrieves the current player's ID from PlayerPrefs.
/// If the player is in the top 10, displays their rank number.
/// If not in the top 10, displays "Unranked".
/// </summary>
public class PlayerRankDisplay : MonoBehaviour
{
    [Header("API Configuration")]
    [Tooltip("URL of the Python leaderboard API")]
    public string apiUrl = "http://localhost:5000";

    [Header("References")]
    public TMP_Text playerNameText;
    public TMP_Text rankText;
    public TMP_Text scoreText;

    // PlayerPrefs key for storing the current player's ID
    private const string PlayerIdKey = "NNBuilder.PlayerId";

    private void Start()
    {
        FetchPlayerRank();
    }

    /// <summary>
    /// Fetches the current player's rank and score from the API.
    /// Automatically gets the player ID from PlayerPrefs.
    /// Shows rank if in top 10, otherwise shows "Unranked".
    /// </summary>
    private IEnumerator FetchPlayerRank()
    {
        // Get player ID from PlayerPrefs
        string playerId = PlayerPrefs.GetString(PlayerIdKey, "");

        if (string.IsNullOrEmpty(playerId))
        {
            Debug.LogWarning("Player ID not found in PlayerPrefs. Make sure the user is logged in.");
            SetUnrankedDisplay();
            yield break;
        }

        string url = $"{apiUrl}/player/{playerId}";
        Debug.Log($"Fetching player info from: {url}");

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
                Debug.LogError($"Failed to fetch player info: {request.error}");
                SetUnrankedDisplay();
                yield break;
            }

            string json = request.downloadHandler.text;
            Debug.Log($"Player response: {json}");

            try
            {
                // Parse the response
                PlayerInfo info = JsonUtility.FromJson<PlayerInfo>(json);

                if (info == null)
                {
                    Debug.LogError("Failed to parse player info JSON");
                    SetUnrankedDisplay();
                    yield break;
                }

                // Update display
                if (playerNameText != null)
                    playerNameText.text = info.playerName;

                if (rankText != null)
                {
                    // Show rank if in top 10, otherwise show "Unranked"
                    if (info.rank.HasValue && info.rank <= 10)
                    {
                        rankText.text = info.rank.Value.ToString();
                    }
                    else
                    {
                        rankText.text = "Unranked";
                    }
                }

                if (scoreText != null)
                    scoreText.text = info.score.ToString();

                Debug.Log($"Player: {info.playerName}, Rank: {info.rank?.ToString() ?? "N/A"}, Score: {info.score}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to parse player JSON: {e.Message}");
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
            playerNameText.text = "Unknown Player";
        if (rankText != null)
            rankText.text = "Unranked";
        if (scoreText != null)
            scoreText.text = "0";
    }
}

/// <summary>
/// JSON response model for /player/<id> endpoint.
/// </summary>
[System.Serializable]
public class PlayerInfo
{
    public string playerName;
    public int? rank;   // Nullable - null if player not found
    public int score;
}
