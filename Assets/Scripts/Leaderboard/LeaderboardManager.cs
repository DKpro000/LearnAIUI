using System.Collections.Generic;
using UnityEngine;

public class LeaderboardManager : MonoBehaviour
{
    [Header("UI References")]
    public Transform content;          // Scroll View Content
    public GameObject rowPrefab;       // Leaderboard Row prefab


    void Start()
    {
        Debug.Log("LeaderboardManager Started — waiting for data from LeaderboardAPIClient");
    }


    public void PopulateLeaderboard(List<PlayerData> players)
    {
        Debug.Log("Creating leaderboard...");
        Debug.Log("Number of players: " + players.Count);

        // Remove existing rows
        foreach (Transform child in content)
        {
            Destroy(child.gameObject);
        }

        // Create rows
        for (int i = 0; i < players.Count; i++)
        {
            GameObject newRow = Instantiate(rowPrefab, content);
            Debug.Log("Created row: " + newRow.name);

            LeaderboardRow rowScript = newRow.GetComponent<LeaderboardRow>();

            if (rowScript != null)
            {
                rowScript.SetData(
                    players[i].rank,
                    players[i].playerName,
                    players[i].score
                );
            }
            else
            {
                Debug.LogError("LeaderboardRow script missing on prefab!");
            }
        }
    }
}



[System.Serializable]
public class PlayerData
{
    public int rank;
    public string playerName;
    public int score;


    public PlayerData(int rank, string name, int playerScore)
    {
        this.rank = rank;
        playerName = name;
        score = playerScore;
    }
}