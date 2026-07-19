using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Runtime-created account gate with separate login and registration views.
/// Passwords and confirmation values remain in memory only for the request.
/// </summary>
public sealed class AccountLoginPanel : MonoBehaviour
{
    private GraphBackendClient client;
    private GameObject canvasObject;
    private GameObject backdrop;
    private TMP_InputField emailInput;
    private TMP_InputField displayNameInput;
    private TMP_InputField passwordInput;
    private TMP_InputField confirmPasswordInput;
    private TMP_Text subtitleText;
    private TMP_Text statusText;
    private TMP_Text accountButtonText;
    private Button loginButton;
    private Button registerButton;
    private Button showRegisterButton;
    private Button showLoginButton;
    private Button logoutButton;
    private Button closeButton;
    private bool registerMode;
    private bool initialized;

    private static readonly Color CardColor = new Color(0.075f, 0.09f, 0.13f, 0.98f);
    private static readonly Color FieldColor = new Color(0.13f, 0.16f, 0.22f, 1f);
    private static readonly Color PrimaryColor = new Color(0.20f, 0.47f, 0.95f, 1f);
    private static readonly Color SecondaryColor = new Color(0.20f, 0.67f, 0.49f, 1f);

    public void Initialize(GraphBackendClient backendClient)
    {
        client = backendClient;
        if (initialized)
        {
            Refresh(client.IsAuthenticated, "");
            return;
        }

        initialized = true;
        BuildUi();
        client.AuthenticationChanged += Refresh;
        registerMode = false;
        Refresh(
            client.IsAuthenticated,
            client.IsAuthenticated
                ? "Checking saved session..."
                : "Enter your email and password to log in."
        );
    }

    private void OnDestroy()
    {
        if (client != null)
        {
            client.AuthenticationChanged -= Refresh;
        }
        if (canvasObject != null)
        {
            Destroy(canvasObject);
        }
    }

    private void BuildUi()
    {
        canvasObject = new GameObject(
            "LearnAIUI Account Canvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster)
        );
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;
        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        backdrop = CreateUiObject("Account Backdrop", canvasObject.transform);
        RectTransform backdropRect = backdrop.GetComponent<RectTransform>();
        backdropRect.anchorMin = Vector2.zero;
        backdropRect.anchorMax = Vector2.one;
        backdropRect.offsetMin = Vector2.zero;
        backdropRect.offsetMax = Vector2.zero;
        Image backdropImage = backdrop.AddComponent<Image>();
        backdropImage.color = new Color(0.015f, 0.02f, 0.035f, 0.88f);

        GameObject card = CreateUiObject("Account Card", backdrop.transform);
        SetCenteredRect(card.GetComponent<RectTransform>(), 640f, 720f, 0f, 0f);
        card.AddComponent<Image>().color = CardColor;

        CreateText(
            card.transform,
            "LearnAIUI Account",
            34f,
            FontStyles.Bold,
            new Vector2(0f, 300f),
            new Vector2(560f, 54f)
        );
        subtitleText = CreateText(
            card.transform,
            "",
            18f,
            FontStyles.Normal,
            new Vector2(0f, 252f),
            new Vector2(560f, 54f)
        );
        subtitleText.color = new Color(0.72f, 0.77f, 0.86f, 1f);

        emailInput = CreateInput(
            card.transform,
            "Email address",
            new Vector2(0f, 168f),
            false,
            254,
            true
        );
        displayNameInput = CreateInput(
            card.transform,
            "Display name",
            new Vector2(0f, 98f),
            false,
            32
        );
        passwordInput = CreateInput(
            card.transform,
            "Password (minimum 10 characters)",
            new Vector2(0f, 28f),
            true,
            128
        );
        confirmPasswordInput = CreateInput(
            card.transform,
            "Confirm password",
            new Vector2(0f, -42f),
            true,
            128
        );

        loginButton = CreateButton(
            card.transform,
            "Log in",
            new Vector2(0f, -128f),
            new Vector2(300f, 56f),
            PrimaryColor
        );
        loginButton.onClick.AddListener(LoginClicked);
        registerButton = CreateButton(
            card.transform,
            "Create account",
            new Vector2(0f, -128f),
            new Vector2(300f, 56f),
            SecondaryColor
        );
        registerButton.onClick.AddListener(RegisterClicked);

        showRegisterButton = CreateButton(
            card.transform,
            "Need an account? Register",
            new Vector2(0f, -198f),
            new Vector2(360f, 48f),
            FieldColor
        );
        showRegisterButton.onClick.AddListener(ShowRegisterMode);
        showLoginButton = CreateButton(
            card.transform,
            "Already have an account? Log in",
            new Vector2(0f, -198f),
            new Vector2(390f, 48f),
            FieldColor
        );
        showLoginButton.onClick.AddListener(ShowLoginMode);

        logoutButton = CreateButton(
            card.transform,
            "Log out",
            new Vector2(0f, -58f),
            new Vector2(260f, 56f),
            new Color(0.80f, 0.28f, 0.28f, 1f)
        );
        logoutButton.onClick.AddListener(LogoutClicked);
        closeButton = CreateButton(
            card.transform,
            "Continue to editor",
            new Vector2(0f, -128f),
            new Vector2(280f, 52f),
            PrimaryColor
        );
        closeButton.onClick.AddListener(Hide);

        statusText = CreateText(
            card.transform,
            "",
            17f,
            FontStyles.Normal,
            new Vector2(0f, -274f),
            new Vector2(560f, 94f)
        );
        statusText.color = new Color(0.86f, 0.89f, 0.95f, 1f);

        Button accountButton = CreateButton(
            canvasObject.transform,
            "Account",
            Vector2.zero,
            new Vector2(290f, 54f),
            FieldColor
        );
        RectTransform accountRect = accountButton.GetComponent<RectTransform>();
        accountRect.anchorMin = new Vector2(1f, 1f);
        accountRect.anchorMax = new Vector2(1f, 1f);
        accountRect.pivot = new Vector2(1f, 1f);
        accountRect.anchoredPosition = new Vector2(-18f, -18f);
        accountButtonText = accountButton.GetComponentInChildren<TMP_Text>();
        accountButton.onClick.AddListener(Show);
    }

    public void Refresh(bool authenticated, string message)
    {
        if (!initialized || backdrop == null)
        {
            return;
        }

        emailInput.gameObject.SetActive(!authenticated);
        passwordInput.gameObject.SetActive(!authenticated);
        logoutButton.gameObject.SetActive(authenticated);
        closeButton.gameObject.SetActive(authenticated);

        if (authenticated)
        {
            displayNameInput.gameObject.SetActive(false);
            confirmPasswordInput.gameObject.SetActive(false);
            loginButton.gameObject.SetActive(false);
            registerButton.gameObject.SetActive(false);
            showRegisterButton.gameObject.SetActive(false);
            showLoginButton.gameObject.SetActive(false);
            subtitleText.text = "Signed in as " + client.CurrentDisplayName +
                " (" + client.CurrentEmail + ")";
        }
        else
        {
            ApplyAccountMode();
        }

        statusText.text = message ?? "";
        accountButtonText.text = authenticated
            ? "Account: " + client.CurrentDisplayName
            : "Log in / Register";
        backdrop.SetActive(!authenticated);
        SetBusy(false);
    }

    private void ApplyAccountMode()
    {
        displayNameInput.gameObject.SetActive(registerMode);
        confirmPasswordInput.gameObject.SetActive(registerMode);
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
        registerMode = false;
        ClearPasswords();
        statusText.text = "Enter your account email and password.";
        ApplyAccountMode();
    }

    private void ShowRegisterMode()
    {
        registerMode = true;
        ClearPasswords();
        statusText.text = "All four registration fields are required.";
        ApplyAccountMode();
    }

    private void Show()
    {
        backdrop.SetActive(true);
        Refresh(client.IsAuthenticated, statusText.text);
        backdrop.SetActive(true);
    }

    private void Hide()
    {
        if (client.IsAuthenticated)
        {
            backdrop.SetActive(false);
        }
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

    private void LogoutClicked()
    {
        SetBusy(true);
        statusText.text = "Logging out...";
        client.LogoutAccount(RequestFinished);
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
        string displayName = displayNameInput.text == null
            ? ""
            : displayNameInput.text.Trim();
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
        if (passwordInput != null)
        {
            passwordInput.text = "";
        }
        if (confirmPasswordInput != null)
        {
            confirmPasswordInput.text = "";
        }
    }

    private void SetBusy(bool busy)
    {
        if (loginButton == null)
        {
            return;
        }
        loginButton.interactable = !busy;
        registerButton.interactable = !busy;
        showRegisterButton.interactable = !busy;
        showLoginButton.interactable = !busy;
        logoutButton.interactable = !busy;
        closeButton.interactable = !busy;
    }

    private static GameObject CreateUiObject(string name, Transform parent)
    {
        GameObject result = new GameObject(name, typeof(RectTransform));
        result.transform.SetParent(parent, false);
        return result;
    }

    private static void SetCenteredRect(
        RectTransform rect,
        float width,
        float height,
        float x,
        float y
    )
    {
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = new Vector2(x, y);
    }

    private static TMP_Text CreateText(
        Transform parent,
        string value,
        float fontSize,
        FontStyles style,
        Vector2 position,
        Vector2 size
    )
    {
        GameObject textObject = CreateUiObject("Text", parent);
        SetCenteredRect(
            textObject.GetComponent<RectTransform>(),
            size.x,
            size.y,
            position.x,
            position.y
        );
        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.raycastTarget = false;
        return text;
    }

    private static TMP_InputField CreateInput(
        Transform parent,
        string placeholderValue,
        Vector2 position,
        bool password,
        int characterLimit,
        bool email = false
    )
    {
        GameObject inputObject = CreateUiObject("Input - " + placeholderValue, parent);
        SetCenteredRect(
            inputObject.GetComponent<RectTransform>(),
            540f,
            56f,
            position.x,
            position.y
        );
        inputObject.AddComponent<Image>().color = FieldColor;
        TMP_InputField input = inputObject.AddComponent<TMP_InputField>();

        GameObject viewportObject = CreateUiObject("Text Area", inputObject.transform);
        RectTransform viewport = viewportObject.GetComponent<RectTransform>();
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = new Vector2(16f, 8f);
        viewport.offsetMax = new Vector2(-16f, -8f);
        viewportObject.AddComponent<RectMask2D>();

        TMP_Text placeholder = CreateText(
            viewportObject.transform,
            placeholderValue,
            18f,
            FontStyles.Italic,
            Vector2.zero,
            new Vector2(500f, 40f)
        );
        placeholder.alignment = TextAlignmentOptions.MidlineLeft;
        placeholder.color = new Color(0.55f, 0.60f, 0.68f, 1f);

        TMP_Text valueText = CreateText(
            viewportObject.transform,
            "",
            19f,
            FontStyles.Normal,
            Vector2.zero,
            new Vector2(500f, 40f)
        );
        valueText.alignment = TextAlignmentOptions.MidlineLeft;

        input.textViewport = viewport;
        input.textComponent = valueText;
        input.placeholder = placeholder;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = characterLimit;
        if (password)
        {
            input.contentType = TMP_InputField.ContentType.Password;
            input.inputType = TMP_InputField.InputType.Password;
            input.asteriskChar = '\u2022';
        }
        else if (email)
        {
            input.contentType = TMP_InputField.ContentType.EmailAddress;
        }
        return input;
    }

    private static Button CreateButton(
        Transform parent,
        string label,
        Vector2 position,
        Vector2 size,
        Color color
    )
    {
        GameObject buttonObject = CreateUiObject("Button - " + label, parent);
        SetCenteredRect(
            buttonObject.GetComponent<RectTransform>(),
            size.x,
            size.y,
            position.x,
            position.y
        );
        buttonObject.AddComponent<Image>().color = color;
        Button button = buttonObject.AddComponent<Button>();
        ColorBlock colors = button.colors;
        colors.normalColor = color;
        colors.highlightedColor = Color.Lerp(color, Color.white, 0.14f);
        colors.pressedColor = Color.Lerp(color, Color.black, 0.18f);
        colors.disabledColor = new Color(color.r, color.g, color.b, 0.35f);
        button.colors = colors;
        TMP_Text text = CreateText(
            buttonObject.transform,
            label,
            18f,
            FontStyles.Bold,
            Vector2.zero,
            size
        );
        text.alignment = TextAlignmentOptions.Center;
        return button;
    }
}
