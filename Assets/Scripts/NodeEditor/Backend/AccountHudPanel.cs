using TMPro;
using UnityEngine;
using UnityEngine.UI;
/// <summary>
/// In-game account HUD. The account button opens a small options panel with Continue / Logout.
/// </summary>
public sealed class AccountHudPanel : MonoBehaviour
{
    [Header("Backend")]
    [SerializeField] private GraphBackendClient client;
    [Header("Root objects")]
    [SerializeField] private GameObject accountOptionsPanel;
    [Header("Buttons")]
    [SerializeField] private Button accountButton;
    [SerializeField] private Button continueButton;
    [SerializeField] private Button logoutButton;
    [Header("Text")]
    [SerializeField] private TMP_Text accountButtonText;
    [SerializeField] private TMP_Text sessionStatusText;
    private bool initialized;
    private void Awake()
    {
        if (accountOptionsPanel != null)
        {
            accountOptionsPanel.SetActive(false);
        }
        accountButton.onClick.AddListener(AccountClicked);
        continueButton.onClick.AddListener(ContinueClicked);
        logoutButton.onClick.AddListener(LogoutClicked);
        if (client != null)
        {
            Initialize(client);
        }
    }
    public void Initialize(GraphBackendClient backendClient)
    {
        client = backendClient;
        if (initialized)
        {
            Refresh(client.IsAuthenticated, "");
            return;
        }
        initialized = true;
        client.AuthenticationChanged += Refresh;
        Refresh(client.IsAuthenticated, "");
    }
    private void OnDestroy()
    {
        if (client != null)
        {
            client.AuthenticationChanged -= Refresh;
        }
    }
    public void Refresh(bool authenticated, string message)
    {
        if (!initialized) return;
        accountButton.gameObject.SetActive(authenticated);
        if (!authenticated && accountOptionsPanel != null)
        {
            accountOptionsPanel.SetActive(false);
        }
        if (accountButtonText != null)
        {
            accountButtonText.text = authenticated ? "Account: " + client.CurrentDisplayName : "Account";
        }
        SetBusy(false);
    }
    private void AccountClicked()
    {
        if (client == null || !client.IsAuthenticated || accountOptionsPanel == null) return;
        if (sessionStatusText != null)
        {
            sessionStatusText.text = "Signed in as " + client.CurrentDisplayName + " (" + client.CurrentEmail + ")";
        }
        accountOptionsPanel.SetActive(true);
    }
    private void ContinueClicked()
    {
        if (accountOptionsPanel != null)
        {
            accountOptionsPanel.SetActive(false);
        }
    }
    private void LogoutClicked()
    {
        if (client == null) return;
        SetBusy(true);
        if (sessionStatusText != null) sessionStatusText.text = "Logging out...";
        client.LogoutAccount(LogoutFinished);
    }
    private void LogoutFinished(bool success, string message)
    {
        SetBusy(false);
        if (accountOptionsPanel != null)
        {
            accountOptionsPanel.SetActive(false);
        }
        if (success)
        {
            Refresh(client.IsAuthenticated, message);
        }
        else if (sessionStatusText != null)
        {
            sessionStatusText.text = message ?? "Logout failed.";
        }
    }
    private void SetBusy(bool busy)
    {
        if (accountButton != null) accountButton.interactable = !busy;
        if (continueButton != null) continueButton.interactable = !busy;
        if (logoutButton != null) logoutButton.interactable = !busy;
    }
}