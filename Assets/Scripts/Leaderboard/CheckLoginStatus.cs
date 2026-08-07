using UnityEngine;

/// <summary>
/// Debug script to check current login information.
/// Attach to any GameObject and check the Console output.
/// </summary>
public class CheckLoginStatus : MonoBehaviour
{
    // PlayerPrefs keys (same as GraphBackendClient)
    private const string PlayerIdKey = "NNBuilder.PlayerId";
    private const string PlayerNameKey = "NNBuilder.PlayerName";
    private const string PlayerEmailKey = "NNBuilder.PlayerEmail";
    private const string PlayerTokenKey = "NNBuilder.PlayerToken";

    void Start()
    {
        Debug.Log("=== Current Login Status ===");

        string playerId = PlayerPrefs.GetString(PlayerIdKey, "");
        string playerName = PlayerPrefs.GetString(PlayerNameKey, "");
        string playerEmail = PlayerPrefs.GetString(PlayerEmailKey, "");
        string playerToken = PlayerPrefs.GetString(PlayerTokenKey, "");

        if (string.IsNullOrEmpty(playerId))
        {
            Debug.LogWarning("No user is currently logged in!");
            Debug.Log("Player ID: (empty)");
            Debug.Log("Player Name: (empty)");
            Debug.Log("Player Email: (empty)");
        }
        else
        {
            Debug.Log($"Player ID: {playerId}");
            Debug.Log($"Player Name: {playerName}");
            Debug.Log($"Player Email: {playerEmail}");
            Debug.Log($"Player Token: {playerToken.Substring(0, Mathf.Min(20, playerToken.Length))}...");
            Debug.Log("Status: Logged in ✓");
        }

        Debug.Log("===========================");
    }

    void OnGUI()
    {
        // Simple GUI to show login status
        GUILayout.Label("=== Login Status ===");

        string playerId = PlayerPrefs.GetString(PlayerIdKey, "");
        string playerName = PlayerPrefs.GetString(PlayerNameKey, "");

        if (string.IsNullOrEmpty(playerId))
        {
            GUILayout.Label("Status: Not Logged In");
            if (GUILayout.Button("Clear Saved Data"))
            {
                PlayerPrefs.DeleteKey(PlayerIdKey);
                PlayerPrefs.DeleteKey(PlayerNameKey);
                PlayerPrefs.DeleteKey(PlayerEmailKey);
                PlayerPrefs.DeleteKey(PlayerTokenKey);
                PlayerPrefs.Save();
                Debug.Log("Saved player data cleared");
            }
        }
        else
        {
            GUILayout.Label($"Status: Logged In ✓");
            GUILayout.Label($"Name: {playerName}");
            GUILayout.Label($"ID: {playerId}");

            if (GUILayout.Button("Log Out"))
            {
                PlayerPrefs.DeleteKey(PlayerIdKey);
                PlayerPrefs.DeleteKey(PlayerNameKey);
                PlayerPrefs.DeleteKey(PlayerEmailKey);
                PlayerPrefs.DeleteKey(PlayerTokenKey);
                PlayerPrefs.Save();
                Debug.Log("Player logged out");
            }
        }
    }
}
