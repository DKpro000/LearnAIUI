using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Refreshes the leaderboard when button is clicked.
/// Attach this to a Button GameObject and assign the LeaderboardAPIClient reference.
/// </summary>
public class LeaderboardRefresh : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The LeaderboardAPIClient component that fetches leaderboard data")]
    public LeaderboardAPIClient leaderboardAPIClient;

    private void Start()
    {
        // Auto-assign if only one LeaderboardAPIClient exists in scene
        if (leaderboardAPIClient == null)
        {
            leaderboardAPIClient = FindObjectOfType<LeaderboardAPIClient>();
            if (leaderboardAPIClient != null)
            {
                Debug.Log("Auto-assigned LeaderboardAPIClient");
            }
        }

        // Hook up button click
        Button button = GetComponent<Button>();
        if (button != null)
        {
            button.onClick.AddListener(RefreshLeaderboard);
        }
        else
        {
            Debug.LogWarning("No Button component found on this GameObject!");
        }
    }

    /// <summary>
    /// Public method to refresh the leaderboard.
    /// Can be called from button onClick or other events.
    /// </summary>
    public void RefreshLeaderboard()
    {
        if (leaderboardAPIClient != null)
        {
            Debug.Log("Refreshing leaderboard...");
            leaderboardAPIClient.RefreshLeaderboard();
        }
        else
        {
            Debug.LogError("LeaderboardAPIClient reference is missing!");
        }
    }
}
