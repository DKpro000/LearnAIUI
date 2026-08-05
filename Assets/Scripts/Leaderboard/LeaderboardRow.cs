using TMPro;
using UnityEngine;

public class LeaderboardRow : MonoBehaviour
{
    public TMP_Text rankText;
    public TMP_Text playerNameText;
    public TMP_Text scoreText;

    public void SetData(int rank, string playerName, int score)
    {
        rankText.text = rank.ToString();
        playerNameText.text = playerName;
        scoreText.text = score.ToString();
    }

    /// <summary>
    /// Called by LeaderboardManager after fetching from the API.
    /// </summary>
    public void RefreshRow(int rank, string playerName, int score)
    {
        SetData(rank, playerName, score);
    }
}