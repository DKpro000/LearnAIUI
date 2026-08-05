using UnityEngine;
using UnityEngine.SceneManagement;

public class toLogin : MonoBehaviour
{
    public GameObject mainMenu;
    public GameObject GameMode;

    // Called when Play button is clicked on Main Menu
    public void GoToLogin()
    {
        SceneManager.LoadScene("Login (Placeholder)", LoadSceneMode.Additive);
    }

    // Called by LoginScene's script once login succeeds
    public void OnLoginComplete()
    {
        SceneManager.UnloadSceneAsync("Login (Placeholder)");
        ShowModeSelection();
    }

    public void ShowMainMenu()
    {
        mainMenu.SetActive(true);
        GameMode.SetActive(false);
    }

    public void ShowModeSelection()
    {
        mainMenu.SetActive(false);
        GameMode.SetActive(true);
    }
}



