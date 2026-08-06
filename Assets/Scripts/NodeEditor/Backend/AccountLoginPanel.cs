using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Startup login / register gate. Shown when the game starts until the player signs in.
/// Account button / Continue / Logout live on AccountHudPanel instead.
/// </summary>
public sealed class AccountLoginPanel : MonoBehaviour
{
    [Header("Backend")]
    [SerializeField] private GraphBackendClient client;
    [Header("Root objects")]
    [SerializeField] private GameObject backdrop;
    [Header("Text")]
    [SerializeField] private TMP_Text subtitleText;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text confirmPasswordLabel;   // "Confirm?" text (Text3)
    [SerializeField] private TMP_Text displayNameLabel;        // "Display Name:" text (Text4)
    [Header("Input fields")]
    [SerializeField] private TMP_InputField emailInput;
    [SerializeField] private TMP_InputField displayNameInput;
    [SerializeField] private TMP_InputField passwordInput;
    [SerializeField] private TMP_InputField confirmPasswordInput;
    [Header("Buttons")]
    [SerializeField] private Button loginButton;
    [SerializeField] private Button registerButton;
    [SerializeField] private Button showRegisterButton;
    [SerializeField] private Button showLoginButton;
    [Header("Optional: password show/hide toggle")]
    [SerializeField] private Button passwordToggleButton;
    [SerializeField] private TMP_Text passwordToggleButtonText;
    [Header("Transitions")]
    [Tooltip("Add a Canvas Group component to your FormPanel and drag it here for a fade transition between Login/Register.")]
    [SerializeField] private CanvasGroup formPanelCanvasGroup;
    [SerializeField] private float fadeOutDuration = 0.12f;
    [SerializeField] private float fadeInDuration = 0.18f;

    private bool registerMode;
    private bool initialized;
    private Coroutine transitionRoutine;

    private void Awake()
    {
        loginButton.onClick.AddListener(LoginClicked);
        registerButton.onClick.AddListener(RegisterClicked);
        showRegisterButton.onClick.AddListener(ShowRegisterMode);
        showLoginButton.onClick.AddListener(ShowLoginMode);
        if (passwordToggleButton != null)
        {
            passwordToggleButton.onClick.AddListener(TogglePasswordVisibility);
        }
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
        registerMode = false;
        Refresh(
            client.IsAuthenticated,
            client.IsAuthenticated ? "" : "Enter your email and password to log in."
        );
    }

    private void OnDestroy()
    {
        if (client != null)
        {
            client.AuthenticationChanged -= Refresh;
        }
    }

    private void TogglePasswordVisibility()
    {
        bool currentlyShowing = passwordInput.contentType == TMP_InputField.ContentType.Standard;
        passwordInput.contentType = currentlyShowing
            ? TMP_InputField.ContentType.Password
            : TMP_InputField.ContentType.Standard;
        passwordInput.ForceLabelUpdate();
        if (passwordToggleButtonText != null)
        {
            passwordToggleButtonText.text = currentlyShowing ? "Show" : "Hide";
        }
    }

    public void Refresh(bool authenticated, string message)
    {
        if (!initialized)
        {
            return;
        }
        statusText.text = message ?? "";
        backdrop.SetActive(!authenticated);
        if (!authenticated)
        {
            ApplyAccountMode();
        }
        SetBusy(false);
    }

    private void ApplyAccountMode()
    {
        displayNameInput.gameObject.SetActive(registerMode);
        confirmPasswordInput.gameObject.SetActive(registerMode);
        if (displayNameLabel != null)
        {
            displayNameLabel.gameObject.SetActive(registerMode);
        }
        if (confirmPasswordLabel != null)
        {
            confirmPasswordLabel.gameObject.SetActive(registerMode);
        }
        loginButton.gameObject.SetActive(!registerMode);
        registerButton.gameObject.SetActive(registerMode);
        showRegisterButton.gameObject.SetActive(!registerMode);
        showLoginButton.gameObject.SetActive(registerMode);
        subtitleText.text = registerMode
            ? "Register with your email, display name, and matching passwords."
            : "Log in with your email address and password.";
    }

    private void ShowLoginMode()
    {
        BeginModeTransition(false);
    }

    private void ShowRegisterMode()
    {
        BeginModeTransition(true);
    }

    private void BeginModeTransition(bool toRegisterMode)
    {
        if (formPanelCanvasGroup == null)
        {
            // No Canvas Group assigned - fall back to an instant switch, no animation.
            registerMode = toRegisterMode;
            ClearPasswords();
            statusText.text = toRegisterMode
                ? "All four registration fields are required."
                : "Enter your account email and password.";
            ApplyAccountMode();
            return;
        }

        if (transitionRoutine != null)
        {
            StopCoroutine(transitionRoutine);
        }
        transitionRoutine = StartCoroutine(TransitionToMode(toRegisterMode));
    }

    private IEnumerator TransitionToMode(bool toRegisterMode)
    {
        yield return FadeCanvasGroup(formPanelCanvasGroup, 1f, 0f, fadeOutDuration);

        registerMode = toRegisterMode;
        ClearPasswords();
        statusText.text = toRegisterMode
            ? "All four registration fields are required."
            : "Enter your account email and password.";
        ApplyAccountMode();

        yield return FadeCanvasGroup(formPanelCanvasGroup, 0f, 1f, fadeInDuration);
        transitionRoutine = null;
    }

    private IEnumerator FadeCanvasGroup(CanvasGroup group, float from, float to, float duration)
    {
        if (group == null)
        {
            yield break;
        }
        group.interactable = false;
        float elapsed = 0f;
        group.alpha = from;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            group.alpha = Mathf.Lerp(from, to, elapsed / duration);
            yield return null;
        }
        group.alpha = to;
        group.interactable = true;
    }

    private void LoginClicked()
    {
        string error = ValidateLogin();
        if (!string.IsNullOrEmpty(error))
        {
            statusText.text = error;
            return;
        }
        SetBusy(true);
        statusText.text = "Signing in...";
        client.LoginWithPassword(emailInput.text, passwordInput.text, RequestFinished);
    }

    private void RegisterClicked()
    {
        string error = ValidateRegistration();
        if (!string.IsNullOrEmpty(error))
        {
            statusText.text = error;
            return;
        }
        SetBusy(true);
        statusText.text = "Creating account...";
        client.RegisterWithPassword(
            emailInput.text,
            displayNameInput.text,
            passwordInput.text,
            confirmPasswordInput.text,
            RequestFinished
        );
    }

    private string ValidateLogin()
    {
        string emailError = ValidateEmail();
        if (!string.IsNullOrEmpty(emailError))
        {
            return emailError;
        }
        if (passwordInput.text == null || passwordInput.text.Length < 10)
        {
            return "Password must contain at least 10 characters.";
        }
        return "";
    }

    private string ValidateRegistration()
    {
        string loginError = ValidateLogin();
        if (!string.IsNullOrEmpty(loginError))
        {
            return loginError;
        }
        string displayName = displayNameInput.text == null ? "" : displayNameInput.text.Trim();
        if (displayName.Length < 1 || displayName.Length > 32)
        {
            return "Display name must contain between 1 and 32 characters.";
        }
        if (confirmPasswordInput.text != passwordInput.text)
        {
            return "Password and confirmation password do not match.";
        }
        return "";
    }

    private string ValidateEmail()
    {
        string email = emailInput.text == null ? "" : emailInput.text.Trim();
        int at = email.LastIndexOf('@');
        if (
            email.Length < 5 ||
            email.Length > 254 ||
            at <= 0 ||
            at != email.IndexOf('@') ||
            at >= email.Length - 3 ||
            email.IndexOf('.', at + 2) < 0
        )
        {
            return "Enter a valid email address.";
        }
        return "";
    }

    private void RequestFinished(bool success, string message)
    {
        ClearPasswords();
        SetBusy(false);
        statusText.text = message;
        if (success)
        {
            Refresh(client.IsAuthenticated, message);
        }
    }

    private void ClearPasswords()
    {
        if (passwordInput != null) passwordInput.text = "";
        if (confirmPasswordInput != null) confirmPasswordInput.text = "";
    }

    private void SetBusy(bool busy)
    {
        if (loginButton == null) return;
        loginButton.interactable = !busy;
        registerButton.interactable = !busy;
        showRegisterButton.interactable = !busy;
        showLoginButton.interactable = !busy;
    }
}