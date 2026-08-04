using UnityEngine;

public class ChatbotPanelToggle : MonoBehaviour
{
    public GameObject ChatPanel;

    public void TogglePanel()
    {
        ChatPanel.SetActive(!ChatPanel.activeSelf);
    }

    public void ClosePanel()
    {
        ChatPanel.SetActive(false);
    }
}
