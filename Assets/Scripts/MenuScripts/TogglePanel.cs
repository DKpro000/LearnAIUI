using UnityEngine;

public class TogglePanel : MonoBehaviour {
    [SerializeField] GameObject panel;

    public void ToggleAudioPanel() {
        panel.SetActive(!panel.activeSelf);
    }
}