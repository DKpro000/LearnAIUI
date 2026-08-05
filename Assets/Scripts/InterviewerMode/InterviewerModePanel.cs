using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Runtime-built, isolated interview workspace. It is opened from its own
/// launcher and never reparents or changes the existing graph editor UI.
/// </summary>
public sealed class InterviewerModePanel : MonoBehaviour
{
    private enum MediaIcon
    {
        Microphone,
        Camera
    }

    private static readonly Color BackgroundColor =
        new Color(0.025f, 0.035f, 0.055f, 0.995f);
    private static readonly Color PanelColor =
        new Color(0.065f, 0.085f, 0.12f, 1f);
    private static readonly Color FieldColor =
        new Color(0.105f, 0.13f, 0.18f, 1f);
    private static readonly Color PrimaryColor =
        new Color(0.20f, 0.47f, 0.95f, 1f);
    private static readonly Color SuccessColor =
        new Color(0.16f, 0.64f, 0.45f, 1f);
    private static readonly Color WarningColor =
        new Color(0.92f, 0.55f, 0.18f, 1f);

    private GraphBackendClient client;
    private IInterviewerRealtimeService realtime =
        new UnavailableInterviewerRealtimeService();
    private GameObject canvasObject;
    private GameObject overlay;
    private GameObject datasetPanel;
    private GameObject candidateDatasetNotice;
    private GameObject modeSelector;
    private GameObject deviceSettingsPanel;
    private GameObject microphoneDeviceList;
    private GameObject cameraDeviceList;
    private Button sandboxButton;
    private Button launcherButton;
    private Button roleButton;
    private TMP_Text roleButtonText;
    private TMP_Text timerText;
    private TMP_Text connectionText;
    private TMP_Text mediaStatusText;
    private TMP_Text microphoneLevelText;
    private TMP_Text datasetStatusText;
    private TMP_Text datasetPreviewText;
    private TMP_Text whiteboardStatusText;
    private TMP_Text selectedMicrophoneText;
    private TMP_Text selectedCameraText;
    private TMP_Text mediaSettingsStatusText;
    private TMP_InputField roomCodeInput;
    private TMP_InputField datasetPathInput;
    private TMP_InputField datasetNameInput;
    private TMP_InputField labelColumnInput;
    private TMP_InputField trainSplitInput;
    private TMP_InputField validationSplitInput;
    private TMP_InputField maximumRowsInput;
    private TMP_InputField epochsInput;
    private TMP_InputField batchSizeInput;
    private TMP_InputField learningRateInput;
    private Button delimiterButton;
    private TMP_Text delimiterButtonText;
    private Button normalizationButton;
    private TMP_Text normalizationButtonText;
    private Button missingValuesButton;
    private TMP_Text missingValuesButtonText;
    private Button headerButton;
    private TMP_Text headerButtonText;
    private Button shuffleButton;
    private TMP_Text shuffleButtonText;
    private Button microphoneButton;
    private Button cameraButton;
    private Button screenShareButton;
    private TMP_Text screenShareButtonText;
    private RawImage localVideoImage;
    private TMP_Text localVideoPlaceholder;
    private InterviewerWhiteboardCanvas whiteboard;

    private InterviewerParticipantRole role =
        InterviewerParticipantRole.Candidate;
    private InterviewerDelimiterMode delimiterMode =
        InterviewerDelimiterMode.Auto;
    private InterviewerNormalizationMode normalizationMode =
        InterviewerNormalizationMode.MinMax;
    private InterviewerMissingValueMode missingValueMode =
        InterviewerMissingValueMode.DropRow;
    private bool datasetHasHeader = true;
    private bool shuffleDataset = true;
    private bool microphoneEnabled;
    private bool cameraEnabled;
    private bool screenShareEnabled;
    private bool initialized;
    private float openedAt;
    private float nextMicrophoneMeterAt;
    private WebCamTexture webCamera;
    private AudioClip microphoneClip;
    private string activeCameraDevice = "";
    private string activeMicrophoneDevice = "";
    private int brushColorIndex;
    private int videoQualityIndex = 2;
    private bool noiseSuppression = true;
    private bool echoCancellation = true;
    private bool autoGainControl = true;
    private bool pushToTalk;
    private bool whiteboardExpanded;
    private RectTransform whiteboardPanelRect;
    private Button whiteboardDrawingButton;
    private Button whiteboardAnnotationButton;
    private Button whiteboardEraserButton;
    private Button whiteboardExpandButton;
    private readonly Color[] brushColors =
    {
        new Color(0.14f, 0.39f, 0.92f, 1f),
        new Color(0.88f, 0.20f, 0.22f, 1f),
        new Color(0.10f, 0.58f, 0.34f, 1f),
        new Color(0.08f, 0.10f, 0.14f, 1f)
    };

    public void Initialize(GraphBackendClient backendClient)
    {
        client = backendClient;
        if (initialized)
        {
            return;
        }
        initialized = true;
        BuildUi();
        client.AuthenticationChanged += OnAuthenticationChanged;
        launcherButton.interactable = client.IsAuthenticated;
        sandboxButton.interactable = client.IsAuthenticated;
    }

    /// <summary>
    /// Installs a production realtime adapter (for example, a LiveKit-backed
    /// component) without coupling the interview UI to that SDK.
    /// </summary>
    public void SetRealtimeService(IInterviewerRealtimeService service)
    {
        if (service == null)
        {
            realtime = new UnavailableInterviewerRealtimeService();
        }
        else
        {
            if (realtime.IsConnected)
            {
                realtime.Leave();
            }
            realtime = service;
        }
        if (connectionText != null)
        {
            UpdateConnectionStatus();
        }
    }

    private void OnDestroy()
    {
        StopCamera();
        StopMicrophone();
        realtime.Leave();
        if (client != null)
        {
            client.AuthenticationChanged -= OnAuthenticationChanged;
        }
        if (canvasObject != null)
        {
            Destroy(canvasObject);
        }
    }

    private void Update()
    {
        if (overlay == null || !overlay.activeSelf)
        {
            return;
        }
        int elapsedSeconds = Mathf.Max(
            0,
            Mathf.FloorToInt(Time.realtimeSinceStartup - openedAt)
        );
        timerText.text = string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}",
            elapsedSeconds / 60,
            elapsedSeconds % 60
        );
        if (
            microphoneEnabled &&
            Time.unscaledTime >= nextMicrophoneMeterAt
        )
        {
            nextMicrophoneMeterAt = Time.unscaledTime + 0.15f;
            UpdateMicrophoneMeter();
        }
    }

    private void BuildUi()
    {
        canvasObject = new GameObject(
            "LearnAIUI Interviewer Mode Canvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster)
        );
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 4500;
        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        modeSelector = CreatePanel(
            canvasObject.transform,
            "Workspace Mode Selector",
            new Color(0.045f, 0.06f, 0.085f, 0.98f)
        );
        RectTransform modeRect = modeSelector.GetComponent<RectTransform>();
        modeRect.anchorMin = Vector2.one;
        modeRect.anchorMax = Vector2.one;
        modeRect.pivot = Vector2.one;
        modeRect.sizeDelta = new Vector2(330f, 58f);
        modeRect.anchoredPosition = new Vector2(-18f, -82f);

        sandboxButton = CreateButton(
            modeSelector.transform,
            "Sandbox",
            SuccessColor,
            154f,
            48f
        );
        RectTransform sandboxRect = sandboxButton.GetComponent<RectTransform>();
        sandboxRect.anchorMin = new Vector2(0f, 0.5f);
        sandboxRect.anchorMax = new Vector2(0f, 0.5f);
        sandboxRect.pivot = new Vector2(0f, 0.5f);
        sandboxRect.anchoredPosition = new Vector2(5f, 0f);
        sandboxButton.onClick.AddListener(Hide);

        launcherButton = CreateButton(
            modeSelector.transform,
            "Interviewer",
            PrimaryColor,
            162f,
            48f
        );
        RectTransform launcherRect = launcherButton.GetComponent<RectTransform>();
        launcherRect.anchorMin = new Vector2(1f, 0.5f);
        launcherRect.anchorMax = new Vector2(1f, 0.5f);
        launcherRect.pivot = new Vector2(1f, 0.5f);
        launcherRect.anchoredPosition = new Vector2(-5f, 0f);
        launcherButton.onClick.AddListener(Show);

        overlay = CreateUiObject("Interviewer Mode", canvasObject.transform);
        SetStretch(overlay.GetComponent<RectTransform>(), Vector2.zero, Vector2.one);
        overlay.AddComponent<Image>().color = BackgroundColor;
        BuildHeader(overlay.transform);
        BuildMediaPanel(overlay.transform);
        BuildWhiteboardPanel(overlay.transform);
        BuildDatasetPanel(overlay.transform);
        BuildMediaSettingsPanel(overlay.transform);
        overlay.SetActive(false);
        ApplyRole();
    }

    private void BuildHeader(Transform parent)
    {
        GameObject header = CreatePanel(parent, "Interview Header", PanelColor);
        SetAnchored(
            header.GetComponent<RectTransform>(),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(0f, -82f),
            Vector2.zero
        );

        TMP_Text title = CreateText(
            header.transform,
            "INTERVIEWER MODE",
            26f,
            FontStyles.Bold
        );
        SetAnchored(
            title.rectTransform,
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(28f, 0f),
            new Vector2(280f, 0f)
        );
        title.alignment = TextAlignmentOptions.MidlineLeft;

        roleButton = CreateButton(
            header.transform,
            "Participant: Candidate",
            FieldColor,
            190f,
            44f
        );
        roleButtonText = roleButton.GetComponentInChildren<TMP_Text>();
        SetHeaderControl(roleButton.GetComponent<RectTransform>(), 330f, 190f);
        roleButton.onClick.AddListener(ToggleRole);

        roomCodeInput = CreateInput(
            header.transform,
            "Room code",
            "interview-room",
            240f,
            44f
        );
        SetHeaderControl(roomCodeInput.GetComponent<RectTransform>(), 535f, 240f);

        Button connect = CreateButton(
            header.transform,
            "Connect",
            SuccessColor,
            120f,
            44f
        );
        SetHeaderControl(connect.GetComponent<RectTransform>(), 790f, 120f);
        connect.onClick.AddListener(ConnectClicked);

        Button leave = CreateButton(
            header.transform,
            "Leave",
            new Color(0.74f, 0.22f, 0.24f, 1f),
            100f,
            44f
        );
        SetHeaderControl(leave.GetComponent<RectTransform>(), 925f, 100f);
        leave.onClick.AddListener(LeaveRoom);

        Button backToSandbox = CreateButton(
            header.transform,
            "Back to Sandbox",
            PrimaryColor,
            175f,
            44f
        );
        SetHeaderControl(
            backToSandbox.GetComponent<RectTransform>(),
            1040f,
            175f
        );
        backToSandbox.onClick.AddListener(Hide);

        connectionText = CreateText(
            header.transform,
            "Local preview",
            15f,
            FontStyles.Normal
        );
        SetHeaderText(connectionText.rectTransform, 1230f, 190f);
        connectionText.color = new Color(0.95f, 0.69f, 0.25f, 1f);

        timerText = CreateText(
            header.transform,
            "00:00",
            23f,
            FontStyles.Bold
        );
        SetHeaderText(timerText.rectTransform, 1440f, 100f);
        timerText.alignment = TextAlignmentOptions.Center;
    }

    private void BuildMediaPanel(Transform parent)
    {
        GameObject panel = CreatePanel(parent, "Media Panel", PanelColor);
        SetStretch(
            panel.GetComponent<RectTransform>(),
            new Vector2(0f, 0f),
            new Vector2(0.255f, 1f),
            new Vector2(16f, 16f),
            new Vector2(-8f, -98f)
        );

        CreateSectionTitle(panel.transform, "COMMUNICATION", -18f);
        GameObject localCard = CreatePanel(
            panel.transform,
            "Local Video",
            new Color(0.025f, 0.035f, 0.05f, 1f)
        );
        SetTopRect(
            localCard.GetComponent<RectTransform>(),
            24f,
            300f,
            72f
        );
        GameObject localVideoFeed = CreateUiObject(
            "Local Camera Feed",
            localCard.transform
        );
        SetStretch(
            localVideoFeed.GetComponent<RectTransform>(),
            Vector2.zero,
            Vector2.one
        );
        localVideoImage = localVideoFeed.AddComponent<RawImage>();
        // RawImage tint is multiplied with every webcam pixel. A black tint
        // makes a working WebCamTexture appear completely black.
        localVideoImage.color = Color.white;
        localVideoPlaceholder = CreateText(
            localCard.transform,
            "Camera is off\nLocal preview",
            18f,
            FontStyles.Normal
        );
        SetStretch(
            localVideoPlaceholder.rectTransform,
            Vector2.zero,
            Vector2.one
        );
        localVideoPlaceholder.alignment = TextAlignmentOptions.Center;
        localVideoPlaceholder.color = new Color(0.68f, 0.73f, 0.82f, 1f);

        GameObject remoteCard = CreatePanel(
            panel.transform,
            "Remote Video",
            new Color(0.025f, 0.035f, 0.05f, 1f)
        );
        SetTopRect(
            remoteCard.GetComponent<RectTransform>(),
            24f,
            190f,
            454f
        );
        TMP_Text remote = CreateText(
            remoteCard.transform,
            "Waiting for remote participant\nConnect a realtime service",
            17f,
            FontStyles.Normal
        );
        SetStretch(remote.rectTransform, Vector2.zero, Vector2.one);
        remote.alignment = TextAlignmentOptions.Center;
        remote.color = new Color(0.68f, 0.73f, 0.82f, 1f);

        GameObject deviceToolbar = CreatePanel(
            localCard.transform,
            "Device Test Toolbar",
            new Color(0.055f, 0.075f, 0.11f, 0.98f)
        );
        SetStretch(
            deviceToolbar.GetComponent<RectTransform>(),
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            Vector2.zero,
            new Vector2(0f, 92f)
        );

        TMP_Text deviceTestLabel = CreateText(
            deviceToolbar.transform,
            "DEVICE\nTEST",
            14f,
            FontStyles.Bold
        );
        SetTopText(
            deviceTestLabel.rectTransform,
            8f,
            20f,
            64f,
            48f
        );
        deviceTestLabel.alignment = TextAlignmentOptions.Center;
        deviceTestLabel.color = Color.white;

        microphoneButton = CreateIconButton(
            deviceToolbar.transform,
            "Microphone",
            MediaIcon.Microphone,
            new Color(0.16f, 0.21f, 0.30f, 1f),
            64f
        );
        SetTopControl(
            microphoneButton.GetComponent<RectTransform>(),
            78f,
            18f,
            50f,
            56f
        );
        microphoneButton.onClick.AddListener(ToggleMicrophone);

        Button microphoneDeviceButton = CreateButton(
            deviceToolbar.transform,
            "v",
            new Color(0.12f, 0.16f, 0.23f, 1f),
            26f,
            56f
        );
        SetTopControl(
            microphoneDeviceButton.GetComponent<RectTransform>(),
            132f,
            18f,
            26f,
            56f
        );
        microphoneDeviceButton.onClick.AddListener(OpenMediaSettings);

        cameraButton = CreateIconButton(
            deviceToolbar.transform,
            "Camera",
            MediaIcon.Camera,
            new Color(0.16f, 0.21f, 0.30f, 1f),
            64f
        );
        SetTopControl(
            cameraButton.GetComponent<RectTransform>(),
            166f,
            18f,
            50f,
            56f
        );
        cameraButton.onClick.AddListener(ToggleCamera);

        Button cameraDeviceButton = CreateButton(
            deviceToolbar.transform,
            "v",
            new Color(0.12f, 0.16f, 0.23f, 1f),
            26f,
            56f
        );
        SetTopControl(
            cameraDeviceButton.GetComponent<RectTransform>(),
            220f,
            18f,
            26f,
            56f
        );
        cameraDeviceButton.onClick.AddListener(OpenMediaSettings);

        screenShareButton = CreateButton(
            deviceToolbar.transform,
            "Share Screen",
            new Color(0.16f, 0.21f, 0.30f, 1f),
            132f,
            64f
        );
        screenShareButtonText =
            screenShareButton.GetComponentInChildren<TMP_Text>();
        SetTopControl(
            screenShareButton.GetComponent<RectTransform>(),
            254f,
            18f,
            82f,
            56f
        );
        screenShareButton.onClick.AddListener(ToggleScreenShare);

        Button mediaSettingsButton = CreateButton(
            deviceToolbar.transform,
            "Settings",
            new Color(0.12f, 0.16f, 0.23f, 1f),
            86f,
            56f
        );
        SetTopControl(
            mediaSettingsButton.GetComponent<RectTransform>(),
            342f,
            18f,
            86f,
            56f
        );
        mediaSettingsButton.onClick.AddListener(OpenMediaSettings);

        Button chooseDevices = CreateButton(
            panel.transform,
            "Choose microphone and camera",
            FieldColor,
            416f,
            42f
        );
        SetTopControl(
            chooseDevices.GetComponent<RectTransform>(),
            24f,
            390f,
            416f,
            42f
        );
        chooseDevices.onClick.AddListener(OpenMediaSettings);

        microphoneLevelText = CreateText(
            panel.transform,
            "Mic level: —",
            15f,
            FontStyles.Normal
        );
        SetTopText(microphoneLevelText.rectTransform, 24f, 660f, 416f, 28f);
        microphoneLevelText.alignment = TextAlignmentOptions.MidlineLeft;

        mediaStatusText = CreateText(
            panel.transform,
            "Media is local until the realtime bridge is configured.",
            15f,
            FontStyles.Normal
        );
        SetTopText(mediaStatusText.rectTransform, 24f, 700f, 416f, 120f);
        mediaStatusText.alignment = TextAlignmentOptions.TopLeft;
        mediaStatusText.textWrappingMode = TextWrappingModes.Normal;
        mediaStatusText.color = new Color(0.72f, 0.77f, 0.86f, 1f);
    }

    private void BuildMediaSettingsPanel(Transform parent)
    {
        deviceSettingsPanel = CreatePanel(
            parent,
            "Media Settings Backdrop",
            new Color(0.01f, 0.015f, 0.025f, 0.82f)
        );
        SetStretch(
            deviceSettingsPanel.GetComponent<RectTransform>(),
            Vector2.zero,
            Vector2.one
        );

        GameObject card = CreatePanel(
            deviceSettingsPanel.transform,
            "Media Settings",
            new Color(0.055f, 0.07f, 0.10f, 1f)
        );
        RectTransform cardRect = card.GetComponent<RectTransform>();
        cardRect.anchorMin = new Vector2(0.5f, 0.5f);
        cardRect.anchorMax = new Vector2(0.5f, 0.5f);
        cardRect.pivot = new Vector2(0.5f, 0.5f);
        cardRect.sizeDelta = new Vector2(800f, 790f);
        cardRect.anchoredPosition = Vector2.zero;

        TMP_Text title = CreateText(
            card.transform,
            "MEDIA SETTINGS",
            20f,
            FontStyles.Bold
        );
        SetTopText(title.rectTransform, 24f, 18f, 300f, 38f);
        title.alignment = TextAlignmentOptions.MidlineLeft;

        Button refresh = CreateButton(
            card.transform,
            "Refresh devices",
            FieldColor,
            160f,
            40f
        );
        SetTopControl(
            refresh.GetComponent<RectTransform>(),
            520f,
            16f,
            160f,
            40f
        );
        refresh.onClick.AddListener(RefreshMediaSettings);

        Button close = CreateButton(
            card.transform,
            "Close",
            new Color(0.55f, 0.18f, 0.20f, 1f),
            90f,
            40f
        );
        SetTopControl(
            close.GetComponent<RectTransform>(),
            690f,
            16f,
            90f,
            40f
        );
        close.onClick.AddListener(CloseMediaSettings);

        GameObject cameraSection = CreatePanel(
            card.transform,
            "Camera Settings",
            new Color(0.085f, 0.105f, 0.145f, 1f)
        );
        SetTopControl(
            cameraSection.GetComponent<RectTransform>(),
            20f,
            72f,
            370f,
            420f
        );
        TMP_Text cameraTitle = CreateText(
            cameraSection.transform,
            "CAMERA",
            17f,
            FontStyles.Bold
        );
        SetTopText(cameraTitle.rectTransform, 16f, 14f, 330f, 28f);
        cameraTitle.alignment = TextAlignmentOptions.MidlineLeft;
        selectedCameraText = CreateText(
            cameraSection.transform,
            "Selected: none",
            13f,
            FontStyles.Normal
        );
        SetTopText(
            selectedCameraText.rectTransform,
            16f,
            48f,
            338f,
            42f
        );
        selectedCameraText.alignment = TextAlignmentOptions.TopLeft;
        selectedCameraText.textWrappingMode = TextWrappingModes.Normal;

        cameraDeviceList = CreateUiObject(
            "Camera Device List",
            cameraSection.transform
        );
        SetTopControl(
            cameraDeviceList.GetComponent<RectTransform>(),
            16f,
            96f,
            338f,
            208f
        );

        Button testCamera = CreateButton(
            cameraSection.transform,
            "Test camera",
            PrimaryColor,
            160f,
            42f
        );
        SetTopControl(
            testCamera.GetComponent<RectTransform>(),
            16f,
            320f,
            160f,
            42f
        );
        testCamera.onClick.AddListener(ToggleCamera);

        Button quality = CreateButton(
            cameraSection.transform,
            "Quality: High",
            FieldColor,
            166f,
            42f
        );
        SetTopControl(
            quality.GetComponent<RectTransform>(),
            188f,
            320f,
            166f,
            42f
        );
        TMP_Text qualityText = quality.GetComponentInChildren<TMP_Text>();
        quality.onClick.AddListener(
            delegate
            {
                videoQualityIndex = (videoQualityIndex + 1) % 3;
                qualityText.text = "Quality: " + GetVideoQualityName();
                if (cameraEnabled)
                {
                    StopCamera();
                    StartCoroutine(StartCamera());
                }
            }
        );
        TMP_Text cameraHint = CreateText(
            cameraSection.transform,
            "Preview appears in the Communication panel.",
            12f,
            FontStyles.Normal
        );
        SetTopText(cameraHint.rectTransform, 16f, 374f, 338f, 30f);
        cameraHint.alignment = TextAlignmentOptions.MidlineLeft;
        cameraHint.color = new Color(0.65f, 0.71f, 0.82f, 1f);

        GameObject microphoneSection = CreatePanel(
            card.transform,
            "Microphone Settings",
            new Color(0.085f, 0.105f, 0.145f, 1f)
        );
        SetTopControl(
            microphoneSection.GetComponent<RectTransform>(),
            410f,
            72f,
            370f,
            420f
        );
        TMP_Text microphoneTitle = CreateText(
            microphoneSection.transform,
            "MICROPHONE",
            17f,
            FontStyles.Bold
        );
        SetTopText(microphoneTitle.rectTransform, 16f, 14f, 330f, 28f);
        microphoneTitle.alignment = TextAlignmentOptions.MidlineLeft;
        selectedMicrophoneText = CreateText(
            microphoneSection.transform,
            "Selected: none",
            13f,
            FontStyles.Normal
        );
        SetTopText(
            selectedMicrophoneText.rectTransform,
            16f,
            48f,
            338f,
            42f
        );
        selectedMicrophoneText.alignment = TextAlignmentOptions.TopLeft;
        selectedMicrophoneText.textWrappingMode = TextWrappingModes.Normal;

        microphoneDeviceList = CreateUiObject(
            "Microphone Device List",
            microphoneSection.transform
        );
        SetTopControl(
            microphoneDeviceList.GetComponent<RectTransform>(),
            16f,
            96f,
            338f,
            208f
        );

        Button testMicrophone = CreateButton(
            microphoneSection.transform,
            "Test microphone",
            SuccessColor,
            338f,
            42f
        );
        SetTopControl(
            testMicrophone.GetComponent<RectTransform>(),
            16f,
            320f,
            338f,
            42f
        );
        testMicrophone.onClick.AddListener(ToggleMicrophone);
        TMP_Text microphoneHint = CreateText(
            microphoneSection.transform,
            "The live input meter appears below the remote preview.",
            12f,
            FontStyles.Normal
        );
        SetTopText(microphoneHint.rectTransform, 16f, 374f, 338f, 30f);
        microphoneHint.alignment = TextAlignmentOptions.MidlineLeft;
        microphoneHint.color = new Color(0.65f, 0.71f, 0.82f, 1f);

        GameObject processing = CreatePanel(
            card.transform,
            "Processing Settings",
            new Color(0.075f, 0.095f, 0.13f, 1f)
        );
        SetTopControl(
            processing.GetComponent<RectTransform>(),
            20f,
            512f,
            760f,
            204f
        );
        TMP_Text processingTitle = CreateText(
            processing.transform,
            "AUDIO PROCESSING",
            14f,
            FontStyles.Bold
        );
        SetTopText(
            processingTitle.rectTransform,
            16f,
            12f,
            250f,
            26f
        );
        processingTitle.alignment = TextAlignmentOptions.MidlineLeft;

        CreateMediaToggle(
            processing.transform,
            "Noise suppression",
            16f,
            48f,
            delegate { return noiseSuppression; },
            delegate { noiseSuppression = !noiseSuppression; }
        );
        CreateMediaToggle(
            processing.transform,
            "Echo cancellation",
            252f,
            48f,
            delegate { return echoCancellation; },
            delegate { echoCancellation = !echoCancellation; }
        );
        CreateMediaToggle(
            processing.transform,
            "Auto gain",
            488f,
            48f,
            delegate { return autoGainControl; },
            delegate { autoGainControl = !autoGainControl; }
        );
        CreateMediaToggle(
            processing.transform,
            "Push to talk",
            16f,
            104f,
            delegate { return pushToTalk; },
            delegate { pushToTalk = !pushToTalk; }
        );

        mediaSettingsStatusText = CreateText(
            processing.transform,
            "Device changes take effect immediately.",
            13f,
            FontStyles.Normal
        );
        SetTopText(
            mediaSettingsStatusText.rectTransform,
            252f,
            112f,
            490f,
            58f
        );
        mediaSettingsStatusText.alignment = TextAlignmentOptions.TopLeft;
        mediaSettingsStatusText.textWrappingMode = TextWrappingModes.Normal;
        mediaSettingsStatusText.color =
            new Color(0.68f, 0.74f, 0.84f, 1f);

        deviceSettingsPanel.SetActive(false);
    }

    private void CreateMediaToggle(
        Transform parent,
        string label,
        float left,
        float top,
        Func<bool> readValue,
        Action toggleValue
    )
    {
        Button button = CreateButton(
            parent,
            label + ": " + (readValue() ? "On" : "Off"),
            readValue() ? PrimaryColor : FieldColor,
            220f,
            42f
        );
        SetTopControl(
            button.GetComponent<RectTransform>(),
            left,
            top,
            220f,
            42f
        );
        TMP_Text text = button.GetComponentInChildren<TMP_Text>();
        button.onClick.AddListener(
            delegate
            {
                toggleValue();
                bool active = readValue();
                text.text = label + ": " + (active ? "On" : "Off");
                button.GetComponent<Image>().color =
                    active ? PrimaryColor : FieldColor;
            }
        );
    }

    private void OpenMediaSettings()
    {
        if (deviceSettingsPanel == null)
        {
            return;
        }
        deviceSettingsPanel.SetActive(true);
        deviceSettingsPanel.transform.SetAsLastSibling();
        RefreshMediaSettings();
    }

    private void CloseMediaSettings()
    {
        if (deviceSettingsPanel != null)
        {
            deviceSettingsPanel.SetActive(false);
        }
    }

    private void RefreshMediaSettings()
    {
        WebCamDevice[] cameras = WebCamTexture.devices;
        string[] microphones = Microphone.devices;
        if (
            (
                string.IsNullOrWhiteSpace(activeCameraDevice) ||
                Array.FindIndex(
                    cameras,
                    item => item.name == activeCameraDevice
                ) < 0
            ) &&
            cameras.Length > 0
        )
        {
            activeCameraDevice = cameras[0].name;
        }
        if (
            (
                string.IsNullOrWhiteSpace(activeMicrophoneDevice) ||
                Array.IndexOf(
                    microphones,
                    activeMicrophoneDevice
                ) < 0
            ) &&
            microphones.Length > 0
        )
        {
            activeMicrophoneDevice = microphones[0];
        }
        selectedCameraText.text = cameras.Length == 0
            ? "No camera found. Check permission and reconnect the device."
            : "Selected: " + activeCameraDevice;
        selectedMicrophoneText.text = microphones.Length == 0
            ? "No microphone found. Check permission and reconnect the device."
            : "Selected: " + activeMicrophoneDevice;
        RebuildCameraDeviceList(cameras);
        RebuildMicrophoneDeviceList(microphones);
        mediaSettingsStatusText.text =
            cameras.Length + " camera(s), " +
            microphones.Length + " microphone(s) detected. " +
            "Processing switches are realtime-adapter preferences.";
    }

    private void RebuildCameraDeviceList(WebCamDevice[] devices)
    {
        ClearRuntimeChildren(cameraDeviceList.transform);
        int count = Mathf.Min(4, devices.Length);
        for (int index = 0; index < count; index++)
        {
            string deviceName = devices[index].name;
            bool selected = deviceName == activeCameraDevice;
            Button button = CreateButton(
                cameraDeviceList.transform,
                (selected ? "[Selected] " : "") + deviceName,
                selected ? PrimaryColor : FieldColor,
                338f,
                42f
            );
            SetTopControl(
                button.GetComponent<RectTransform>(),
                0f,
                index * 48f,
                338f,
                42f
            );
            button.onClick.AddListener(
                delegate { SelectCameraDevice(deviceName); }
            );
        }
    }

    private void RebuildMicrophoneDeviceList(string[] devices)
    {
        ClearRuntimeChildren(microphoneDeviceList.transform);
        int count = Mathf.Min(4, devices.Length);
        for (int index = 0; index < count; index++)
        {
            string deviceName = devices[index];
            bool selected = deviceName == activeMicrophoneDevice;
            Button button = CreateButton(
                microphoneDeviceList.transform,
                (selected ? "[Selected] " : "") + deviceName,
                selected ? SuccessColor : FieldColor,
                338f,
                42f
            );
            SetTopControl(
                button.GetComponent<RectTransform>(),
                0f,
                index * 48f,
                338f,
                42f
            );
            button.onClick.AddListener(
                delegate { SelectMicrophoneDevice(deviceName); }
            );
        }
    }

    private void SelectCameraDevice(string deviceName)
    {
        bool wasEnabled = cameraEnabled;
        if (wasEnabled)
        {
            StopCamera();
        }
        activeCameraDevice = deviceName;
        if (wasEnabled)
        {
            StartCoroutine(StartCamera());
        }
        RefreshMediaSettings();
        SetMediaStatus("Selected camera: " + deviceName);
    }

    private void SelectMicrophoneDevice(string deviceName)
    {
        bool wasEnabled = microphoneEnabled;
        if (wasEnabled)
        {
            StopMicrophone();
        }
        activeMicrophoneDevice = deviceName;
        if (wasEnabled)
        {
            StartCoroutine(StartMicrophone());
        }
        RefreshMediaSettings();
        SetMediaStatus("Selected microphone: " + deviceName);
    }

    private static void ClearRuntimeChildren(Transform parent)
    {
        for (int index = parent.childCount - 1; index >= 0; index--)
        {
            Destroy(parent.GetChild(index).gameObject);
        }
    }

    private string GetVideoQualityName()
    {
        if (videoQualityIndex == 0)
        {
            return "Low";
        }
        if (videoQualityIndex == 1)
        {
            return "Medium";
        }
        return "High";
    }

    private void BuildLegacyWhiteboardPanel(Transform parent)
    {
        GameObject panel = CreatePanel(parent, "Whiteboard Panel", PanelColor);
        SetStretch(
            panel.GetComponent<RectTransform>(),
            new Vector2(0.255f, 0f),
            new Vector2(0.715f, 1f),
            new Vector2(8f, 16f),
            new Vector2(-8f, -98f)
        );
        CreateSectionTitle(panel.transform, "SHARED WHITEBOARD", -18f);

        Button tool = CreateButton(
            panel.transform,
            "Tool: Pen",
            PrimaryColor,
            120f,
            42f
        );
        SetTopControl(
            tool.GetComponent<RectTransform>(),
            20f,
            58f,
            120f,
            42f
        );
        tool.onClick.AddListener(
            delegate
            {
                CycleWhiteboardTool(tool.GetComponentInChildren<TMP_Text>());
            }
        );

        Button color = CreateButton(
            panel.transform,
            "Color",
            brushColors[0],
            90f,
            42f
        );
        SetTopControl(
            color.GetComponent<RectTransform>(),
            150f,
            58f,
            90f,
            42f
        );
        color.onClick.AddListener(
            delegate
            {
                brushColorIndex =
                    (brushColorIndex + 1) % brushColors.Length;
                color.GetComponent<Image>().color =
                    brushColors[brushColorIndex];
                whiteboard.SetBrushColor(brushColors[brushColorIndex]);
            }
        );

        Button sizeDown = CreateButton(
            panel.transform,
            "−",
            FieldColor,
            44f,
            42f
        );
        SetTopControl(
            sizeDown.GetComponent<RectTransform>(),
            250f,
            58f,
            44f,
            42f
        );
        sizeDown.onClick.AddListener(
            delegate
            {
                whiteboard.SetBrushSize(whiteboard.BrushSize - 1);
                SetWhiteboardStatus();
            }
        );

        Button sizeUp = CreateButton(
            panel.transform,
            "+",
            FieldColor,
            44f,
            42f
        );
        SetTopControl(
            sizeUp.GetComponent<RectTransform>(),
            300f,
            58f,
            44f,
            42f
        );
        sizeUp.onClick.AddListener(
            delegate
            {
                whiteboard.SetBrushSize(whiteboard.BrushSize + 1);
                SetWhiteboardStatus();
            }
        );

        Button undo = CreateButton(
            panel.transform,
            "Undo",
            FieldColor,
            74f,
            42f
        );
        SetTopControl(
            undo.GetComponent<RectTransform>(),
            358f,
            58f,
            74f,
            42f
        );
        undo.onClick.AddListener(delegate { whiteboard.Undo(); });

        Button redo = CreateButton(
            panel.transform,
            "Redo",
            FieldColor,
            74f,
            42f
        );
        SetTopControl(
            redo.GetComponent<RectTransform>(),
            440f,
            58f,
            74f,
            42f
        );
        redo.onClick.AddListener(delegate { whiteboard.Redo(); });

        Button clear = CreateButton(
            panel.transform,
            "Clear",
            new Color(0.68f, 0.22f, 0.22f, 1f),
            74f,
            42f
        );
        SetTopControl(
            clear.GetComponent<RectTransform>(),
            522f,
            58f,
            74f,
            42f
        );
        clear.onClick.AddListener(delegate { whiteboard.Clear(); });

        Button save = CreateButton(
            panel.transform,
            "Save PNG",
            SuccessColor,
            108f,
            42f
        );
        SetTopControl(
            save.GetComponent<RectTransform>(),
            604f,
            58f,
            108f,
            42f
        );
        save.onClick.AddListener(SaveWhiteboard);

        GameObject boardObject = CreateUiObject(
            "Whiteboard Canvas",
            panel.transform
        );
        RectTransform boardRect = boardObject.GetComponent<RectTransform>();
        SetStretch(
            boardRect,
            new Vector2(0f, 0f),
            new Vector2(1f, 1f),
            new Vector2(20f, 58f),
            new Vector2(-20f, -116f)
        );
        boardObject.AddComponent<RawImage>();
        whiteboard = boardObject.AddComponent<InterviewerWhiteboardCanvas>();
        whiteboard.Initialize(1024, 640);
        whiteboard.SetBrushColor(brushColors[0]);
        whiteboard.Changed += WhiteboardChanged;

        whiteboardStatusText = CreateText(
            panel.transform,
            "Pen • 5 px • local board",
            14f,
            FontStyles.Normal
        );
        RectTransform statusRect = whiteboardStatusText.rectTransform;
        statusRect.anchorMin = new Vector2(0f, 0f);
        statusRect.anchorMax = new Vector2(1f, 0f);
        statusRect.pivot = new Vector2(0.5f, 0f);
        statusRect.offsetMin = new Vector2(20f, 14f);
        statusRect.offsetMax = new Vector2(-20f, 42f);
        whiteboardStatusText.alignment = TextAlignmentOptions.MidlineLeft;
        whiteboardStatusText.color =
            new Color(0.68f, 0.73f, 0.82f, 1f);
    }

    private void BuildWhiteboardPanel(Transform parent)
    {
        GameObject panel = CreatePanel(parent, "Whiteboard Panel", PanelColor);
        whiteboardPanelRect = panel.GetComponent<RectTransform>();
        SetStretch(
            whiteboardPanelRect,
            new Vector2(0.255f, 0f),
            new Vector2(0.715f, 1f),
            new Vector2(8f, 16f),
            new Vector2(-8f, -98f)
        );
        CreateSectionTitle(panel.transform, "WHITEBOARD", -18f);

        whiteboardDrawingButton = CreateButton(
            panel.transform,
            "Drawing",
            PrimaryColor,
            110f,
            42f
        );
        SetTopControl(
            whiteboardDrawingButton.GetComponent<RectTransform>(),
            20f,
            58f,
            110f,
            42f
        );
        whiteboardDrawingButton.onClick.AddListener(
            delegate
            {
                SetWhiteboardMode(InterviewerWhiteboardTool.Pen);
            }
        );

        whiteboardAnnotationButton = CreateButton(
            panel.transform,
            "Annotation",
            FieldColor,
            124f,
            42f
        );
        SetTopControl(
            whiteboardAnnotationButton.GetComponent<RectTransform>(),
            138f,
            58f,
            124f,
            42f
        );
        whiteboardAnnotationButton.onClick.AddListener(
            delegate
            {
                SetWhiteboardMode(
                    InterviewerWhiteboardTool.Highlighter
                );
            }
        );

        whiteboardEraserButton = CreateButton(
            panel.transform,
            "Eraser",
            FieldColor,
            82f,
            42f
        );
        SetTopControl(
            whiteboardEraserButton.GetComponent<RectTransform>(),
            270f,
            58f,
            82f,
            42f
        );
        whiteboardEraserButton.onClick.AddListener(
            delegate
            {
                SetWhiteboardMode(InterviewerWhiteboardTool.Eraser);
            }
        );

        Button color = CreateButton(
            panel.transform,
            "Color",
            brushColors[0],
            72f,
            42f
        );
        SetTopControl(
            color.GetComponent<RectTransform>(),
            360f,
            58f,
            72f,
            42f
        );
        color.onClick.AddListener(
            delegate
            {
                brushColorIndex =
                    (brushColorIndex + 1) % brushColors.Length;
                color.GetComponent<Image>().color =
                    brushColors[brushColorIndex];
                if (
                    whiteboard.Tool !=
                    InterviewerWhiteboardTool.Highlighter
                )
                {
                    whiteboard.SetBrushColor(
                        brushColors[brushColorIndex]
                    );
                }
                SetWhiteboardStatus();
            }
        );

        Button sizeDown = CreateButton(
            panel.transform,
            "-",
            FieldColor,
            42f,
            42f
        );
        SetTopControl(
            sizeDown.GetComponent<RectTransform>(),
            440f,
            58f,
            42f,
            42f
        );
        sizeDown.onClick.AddListener(
            delegate
            {
                whiteboard.SetBrushSize(whiteboard.BrushSize - 1);
                SetWhiteboardStatus();
            }
        );

        Button sizeUp = CreateButton(
            panel.transform,
            "+",
            FieldColor,
            42f,
            42f
        );
        SetTopControl(
            sizeUp.GetComponent<RectTransform>(),
            490f,
            58f,
            42f,
            42f
        );
        sizeUp.onClick.AddListener(
            delegate
            {
                whiteboard.SetBrushSize(whiteboard.BrushSize + 1);
                SetWhiteboardStatus();
            }
        );

        Button undo = CreateButton(
            panel.transform,
            "Undo",
            FieldColor,
            72f,
            42f
        );
        SetTopControl(
            undo.GetComponent<RectTransform>(),
            20f,
            108f,
            72f,
            42f
        );
        undo.onClick.AddListener(delegate { whiteboard.Undo(); });

        Button redo = CreateButton(
            panel.transform,
            "Redo",
            FieldColor,
            72f,
            42f
        );
        SetTopControl(
            redo.GetComponent<RectTransform>(),
            100f,
            108f,
            72f,
            42f
        );
        redo.onClick.AddListener(delegate { whiteboard.Redo(); });

        Button clear = CreateButton(
            panel.transform,
            "Clear",
            new Color(0.68f, 0.22f, 0.22f, 1f),
            72f,
            42f
        );
        SetTopControl(
            clear.GetComponent<RectTransform>(),
            180f,
            108f,
            72f,
            42f
        );
        clear.onClick.AddListener(delegate { whiteboard.Clear(); });

        Button save = CreateButton(
            panel.transform,
            "Save PNG",
            SuccessColor,
            102f,
            42f
        );
        SetTopControl(
            save.GetComponent<RectTransform>(),
            260f,
            108f,
            102f,
            42f
        );
        save.onClick.AddListener(SaveWhiteboard);

        whiteboardExpandButton = CreateButton(
            panel.transform,
            "Pop Out",
            new Color(0.16f, 0.21f, 0.30f, 1f),
            100f,
            42f
        );
        SetTopControl(
            whiteboardExpandButton.GetComponent<RectTransform>(),
            370f,
            108f,
            100f,
            42f
        );
        whiteboardExpandButton.onClick.AddListener(
            ToggleWhiteboardExpanded
        );

        GameObject boardObject = CreateUiObject(
            "Whiteboard Canvas",
            panel.transform
        );
        RectTransform boardRect = boardObject.GetComponent<RectTransform>();
        SetStretch(
            boardRect,
            Vector2.zero,
            Vector2.one,
            new Vector2(20f, 58f),
            new Vector2(-20f, -166f)
        );
        boardObject.AddComponent<RawImage>();
        whiteboard = boardObject.AddComponent<InterviewerWhiteboardCanvas>();
        whiteboard.Initialize(1024, 640);
        whiteboard.SetBrushColor(brushColors[0]);
        whiteboard.Changed += WhiteboardChanged;

        whiteboardStatusText = CreateText(
            panel.transform,
            "Drawing | 5 px | local board",
            14f,
            FontStyles.Normal
        );
        RectTransform statusRect = whiteboardStatusText.rectTransform;
        statusRect.anchorMin = new Vector2(0f, 0f);
        statusRect.anchorMax = new Vector2(1f, 0f);
        statusRect.pivot = new Vector2(0.5f, 0f);
        statusRect.offsetMin = new Vector2(20f, 14f);
        statusRect.offsetMax = new Vector2(-20f, 42f);
        whiteboardStatusText.alignment =
            TextAlignmentOptions.MidlineLeft;
        whiteboardStatusText.color =
            new Color(0.68f, 0.73f, 0.82f, 1f);
        RefreshWhiteboardModeButtons();
    }

    private void BuildDatasetPanel(Transform parent)
    {
        datasetPanel = CreatePanel(
            parent,
            "Interviewer Dataset Panel",
            PanelColor
        );
        SetStretch(
            datasetPanel.GetComponent<RectTransform>(),
            new Vector2(0.715f, 0f),
            new Vector2(1f, 1f),
            new Vector2(8f, 16f),
            new Vector2(-16f, -98f)
        );
        CreateSectionTitle(
            datasetPanel.transform,
            "INTERVIEWER DATASET",
            -18f
        );
        TMP_Text privacy = CreateText(
            datasetPanel.transform,
            "Processed locally. Raw data is not uploaded.",
            14f,
            FontStyles.Normal
        );
        SetTopText(privacy.rectTransform, 22f, 54f, 480f, 28f);
        privacy.alignment = TextAlignmentOptions.MidlineLeft;
        privacy.color = new Color(0.45f, 0.82f, 0.65f, 1f);

        datasetPathInput = CreateInput(
            datasetPanel.transform,
            "CSV or TSV file path",
            "",
            350f,
            42f
        );
        SetTopControl(
            datasetPathInput.GetComponent<RectTransform>(),
            22f,
            88f,
            350f,
            42f
        );
        Button browse = CreateButton(
            datasetPanel.transform,
            "Browse",
            PrimaryColor,
            112f,
            42f
        );
        SetTopControl(
            browse.GetComponent<RectTransform>(),
            382f,
            88f,
            112f,
            42f
        );
        browse.onClick.AddListener(BrowseDataset);

        datasetNameInput = AddDatasetField(
            datasetPanel.transform,
            "Output name",
            "interviewer-dataset",
            144f
        );
        labelColumnInput = AddDatasetField(
            datasetPanel.transform,
            "Label column",
            "blank = last column",
            194f
        );

        headerButton = CreateButton(
            datasetPanel.transform,
            "Header: Yes",
            FieldColor,
            148f,
            42f
        );
        headerButtonText = headerButton.GetComponentInChildren<TMP_Text>();
        SetTopControl(
            headerButton.GetComponent<RectTransform>(),
            22f,
            250f,
            148f,
            42f
        );
        headerButton.onClick.AddListener(
            delegate
            {
                datasetHasHeader = !datasetHasHeader;
                headerButtonText.text =
                    datasetHasHeader ? "Header: Yes" : "Header: No";
            }
        );

        shuffleButton = CreateButton(
            datasetPanel.transform,
            "Shuffle: Yes",
            FieldColor,
            148f,
            42f
        );
        shuffleButtonText = shuffleButton.GetComponentInChildren<TMP_Text>();
        SetTopControl(
            shuffleButton.GetComponent<RectTransform>(),
            180f,
            250f,
            148f,
            42f
        );
        shuffleButton.onClick.AddListener(
            delegate
            {
                shuffleDataset = !shuffleDataset;
                shuffleButtonText.text =
                    shuffleDataset ? "Shuffle: Yes" : "Shuffle: No";
            }
        );

        delimiterButton = CreateButton(
            datasetPanel.transform,
            "Delimiter: Auto",
            FieldColor,
            158f,
            42f
        );
        delimiterButtonText =
            delimiterButton.GetComponentInChildren<TMP_Text>();
        SetTopControl(
            delimiterButton.GetComponent<RectTransform>(),
            338f,
            250f,
            158f,
            42f
        );
        delimiterButton.onClick.AddListener(CycleDelimiter);

        normalizationButton = CreateButton(
            datasetPanel.transform,
            "Normalize: MinMax",
            FieldColor,
            230f,
            42f
        );
        normalizationButtonText =
            normalizationButton.GetComponentInChildren<TMP_Text>();
        SetTopControl(
            normalizationButton.GetComponent<RectTransform>(),
            22f,
            302f,
            230f,
            42f
        );
        normalizationButton.onClick.AddListener(CycleNormalization);

        missingValuesButton = CreateButton(
            datasetPanel.transform,
            "Missing: DropRow",
            FieldColor,
            234f,
            42f
        );
        missingValuesButtonText =
            missingValuesButton.GetComponentInChildren<TMP_Text>();
        SetTopControl(
            missingValuesButton.GetComponent<RectTransform>(),
            262f,
            302f,
            234f,
            42f
        );
        missingValuesButton.onClick.AddListener(CycleMissingValues);

        trainSplitInput = AddCompactDatasetField(
            datasetPanel.transform,
            "Train",
            "0.70",
            22f,
            360f,
            150f
        );
        validationSplitInput = AddCompactDatasetField(
            datasetPanel.transform,
            "Validation",
            "0.15",
            184f,
            360f,
            150f
        );
        maximumRowsInput = AddCompactDatasetField(
            datasetPanel.transform,
            "Max rows",
            "50000",
            346f,
            360f,
            150f
        );
        epochsInput = AddCompactDatasetField(
            datasetPanel.transform,
            "Epochs",
            "10",
            22f,
            430f,
            150f
        );
        batchSizeInput = AddCompactDatasetField(
            datasetPanel.transform,
            "Batch size",
            "32",
            184f,
            430f,
            150f
        );
        learningRateInput = AddCompactDatasetField(
            datasetPanel.transform,
            "Learning rate",
            "0.001",
            346f,
            430f,
            150f
        );

        Button process = CreateButton(
            datasetPanel.transform,
            "Process Dataset",
            SuccessColor,
            220f,
            48f
        );
        SetTopControl(
            process.GetComponent<RectTransform>(),
            22f,
            508f,
            220f,
            48f
        );
        process.onClick.AddListener(ProcessDatasetClicked);

        Button openFolder = CreateButton(
            datasetPanel.transform,
            "Open Output Folder",
            FieldColor,
            244f,
            48f
        );
        SetTopControl(
            openFolder.GetComponent<RectTransform>(),
            252f,
            508f,
            244f,
            48f
        );
        openFolder.onClick.AddListener(OpenDatasetFolder);

        datasetStatusText = CreateText(
            datasetPanel.transform,
            "Choose a CSV/TSV file and set its preprocessing parameters.",
            15f,
            FontStyles.Normal
        );
        SetTopText(
            datasetStatusText.rectTransform,
            22f,
            570f,
            474f,
            68f
        );
        datasetStatusText.alignment = TextAlignmentOptions.TopLeft;
        datasetStatusText.textWrappingMode = TextWrappingModes.Normal;
        datasetStatusText.color =
            new Color(0.74f, 0.79f, 0.88f, 1f);

        datasetPreviewText = CreateText(
            datasetPanel.transform,
            "Processed preview appears here.",
            13f,
            FontStyles.Normal
        );
        SetTopText(
            datasetPreviewText.rectTransform,
            22f,
            648f,
            474f,
            230f
        );
        datasetPreviewText.alignment = TextAlignmentOptions.TopLeft;
        datasetPreviewText.font = TMP_Settings.defaultFontAsset;
        datasetPreviewText.textWrappingMode = TextWrappingModes.Normal;
        datasetPreviewText.overflowMode = TextOverflowModes.Ellipsis;
        datasetPreviewText.color =
            new Color(0.68f, 0.73f, 0.82f, 1f);

        candidateDatasetNotice = CreatePanel(
            parent,
            "Candidate Dataset Notice",
            PanelColor
        );
        SetStretch(
            candidateDatasetNotice.GetComponent<RectTransform>(),
            new Vector2(0.715f, 0f),
            new Vector2(1f, 1f),
            new Vector2(8f, 16f),
            new Vector2(-16f, -98f)
        );
        CreateSectionTitle(
            candidateDatasetNotice.transform,
            "INTERVIEW DATASET",
            -18f
        );
        TMP_Text notice = CreateText(
            candidateDatasetNotice.transform,
            "Only the interviewer can select and preprocess a dataset in this mode.\n\n" +
            "When realtime synchronization is connected, the candidate will " +
            "receive the processed dataset manifest and suggested training parameters here.",
            18f,
            FontStyles.Normal
        );
        SetTopText(notice.rectTransform, 28f, 86f, 450f, 260f);
        notice.alignment = TextAlignmentOptions.TopLeft;
        notice.textWrappingMode = TextWrappingModes.Normal;
        notice.color = new Color(0.74f, 0.79f, 0.88f, 1f);
    }

    private void Show()
    {
        if (!client.IsAuthenticated)
        {
            return;
        }
        openedAt = Time.realtimeSinceStartup;
        overlay.SetActive(true);
        modeSelector.SetActive(false);
        UpdateConnectionStatus();
    }

    private void Hide()
    {
        overlay.SetActive(false);
        modeSelector.SetActive(true);
    }

    private void ToggleRole()
    {
        if (realtime.IsConnected)
        {
            SetMediaStatus(
                "Leave the current room before changing participant role."
            );
            return;
        }
        role = role == InterviewerParticipantRole.Candidate
            ? InterviewerParticipantRole.Interviewer
            : InterviewerParticipantRole.Candidate;
        ApplyRole();
    }

    private void ApplyRole()
    {
        if (roleButtonText != null)
        {
            roleButtonText.text = "Participant: " + role;
        }
        if (datasetPanel != null)
        {
            datasetPanel.SetActive(
                role == InterviewerParticipantRole.Interviewer
            );
        }
        if (candidateDatasetNotice != null)
        {
            candidateDatasetNotice.SetActive(
                role == InterviewerParticipantRole.Candidate
            );
        }
    }

    private void ConnectClicked()
    {
        string roomCode = roomCodeInput.text == null
            ? ""
            : roomCodeInput.text.Trim();
        if (string.IsNullOrWhiteSpace(roomCode))
        {
            SetMediaStatus("Enter a room code before connecting.");
            return;
        }
        string displayName = string.IsNullOrWhiteSpace(
            client.CurrentDisplayName
        )
            ? role.ToString()
            : client.CurrentDisplayName;
        if (connectionText != null)
        {
            connectionText.text = "Connecting...";
        }
        realtime.Join(
            roomCode,
            displayName,
            role,
            delegate(bool success, string message)
            {
                SetMediaStatus(
                    string.IsNullOrWhiteSpace(message)
                        ? (success
                            ? "Connected to interview room."
                            : "Could not connect to interview room.")
                        : message
                );
                UpdateConnectionStatus();
            }
        );
    }

    private void LeaveRoom()
    {
        realtime.Leave();
        screenShareEnabled = false;
        if (screenShareButtonText != null)
        {
            screenShareButtonText.text = "Share Screen";
        }
        UpdateConnectionStatus();
        SetMediaStatus(
            "Left interview room. Local tools remain available."
        );
    }

    private void UpdateConnectionStatus()
    {
        if (connectionText == null)
        {
            return;
        }
        if (realtime.IsConnected)
        {
            connectionText.text = "Connected";
            connectionText.color = new Color(0.36f, 0.88f, 0.59f, 1f);
        }
        else
        {
            connectionText.text = realtime.ConnectionState;
            connectionText.color = new Color(0.95f, 0.69f, 0.25f, 1f);
        }
    }

    private void SetMediaStatus(string message)
    {
        if (mediaStatusText == null)
        {
            Debug.LogWarning(
                "Interviewer Mode status: " + (message ?? "")
            );
            return;
        }
        mediaStatusText.text = string.IsNullOrWhiteSpace(message)
            ? "No additional status information is available."
            : message;
    }

    private void ToggleCamera()
    {
        if (cameraEnabled)
        {
            StopCamera();
        }
        else
        {
            StartCoroutine(StartCamera());
        }
    }

    private IEnumerator StartCamera()
    {
        yield return Application.RequestUserAuthorization(
            UserAuthorization.WebCam
        );
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            SetMediaStatus("Camera permission was denied.");
            yield break;
        }
        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices.Length == 0)
        {
            SetMediaStatus("No camera device was found.");
            yield break;
        }
        if (
            string.IsNullOrWhiteSpace(activeCameraDevice) ||
            Array.FindIndex(
                devices,
                item => item.name == activeCameraDevice
            ) < 0
        )
        {
            activeCameraDevice = devices[0].name;
        }
        StopCamera();
        int requestedWidth = videoQualityIndex == 0
            ? 320
            : videoQualityIndex == 1 ? 640 : 1280;
        int requestedHeight = videoQualityIndex == 0
            ? 240
            : videoQualityIndex == 1 ? 480 : 720;
        int requestedFps = videoQualityIndex == 0
            ? 15
            : videoQualityIndex == 1 ? 24 : 30;
        webCamera = new WebCamTexture(
            activeCameraDevice,
            requestedWidth,
            requestedHeight,
            requestedFps
        );
        localVideoImage.texture = webCamera;
        webCamera.Play();
        cameraEnabled = true;

        WebCamTexture startedCamera = webCamera;
        float previewDeadline = Time.realtimeSinceStartup + 4f;
        while (
            startedCamera == webCamera &&
            startedCamera.isPlaying &&
            startedCamera.width <= 16 &&
            Time.realtimeSinceStartup < previewDeadline
        )
        {
            yield return null;
        }
        if (startedCamera != webCamera)
        {
            yield break;
        }
        if (!startedCamera.isPlaying || startedCamera.width <= 16)
        {
            StopCamera();
            SetMediaStatus(
                "The camera opened but did not provide a video frame. " +
                "Choose another camera in Settings or close other apps " +
                "that may be using it."
            );
            yield break;
        }

        localVideoImage.uvRect = startedCamera.videoVerticallyMirrored
            ? new Rect(0f, 1f, 1f, -1f)
            : new Rect(0f, 0f, 1f, 1f);
        localVideoPlaceholder.gameObject.SetActive(false);
        cameraButton.GetComponent<Image>().color = PrimaryColor;
        realtime.SetCameraEnabled(true);
        SetMediaStatus(
            "Local camera: " + activeCameraDevice + " (" +
            startedCamera.width + "x" + startedCamera.height + ")"
        );
    }

    private void StopCamera()
    {
        if (webCamera != null)
        {
            if (webCamera.isPlaying)
            {
                webCamera.Stop();
            }
            Destroy(webCamera);
            webCamera = null;
        }
        cameraEnabled = false;
        if (localVideoImage != null)
        {
            localVideoImage.texture = null;
        }
        if (localVideoPlaceholder != null)
        {
            localVideoPlaceholder.gameObject.SetActive(true);
        }
        if (cameraButton != null)
        {
            cameraButton.GetComponent<Image>().color = FieldColor;
        }
        realtime.SetCameraEnabled(false);
    }

    private void SelectNextCamera()
    {
        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices.Length == 0)
        {
            SetMediaStatus("No camera device was found.");
            return;
        }
        int current = Array.FindIndex(
            devices,
            item => item.name == activeCameraDevice
        );
        activeCameraDevice =
            devices[(current + 1 + devices.Length) % devices.Length].name;
        SetMediaStatus("Selected camera: " + activeCameraDevice);
        if (cameraEnabled)
        {
            StopCamera();
            StartCoroutine(StartCamera());
        }
    }

    private void ToggleMicrophone()
    {
        if (microphoneEnabled)
        {
            StopMicrophone();
        }
        else
        {
            StartCoroutine(StartMicrophone());
        }
    }

    private IEnumerator StartMicrophone()
    {
        yield return Application.RequestUserAuthorization(
            UserAuthorization.Microphone
        );
        if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            SetMediaStatus("Microphone permission was denied.");
            yield break;
        }
        string[] devices = Microphone.devices;
        if (devices.Length == 0)
        {
            SetMediaStatus("No microphone device was found.");
            yield break;
        }
        if (
            string.IsNullOrWhiteSpace(activeMicrophoneDevice) ||
            Array.IndexOf(devices, activeMicrophoneDevice) < 0
        )
        {
            activeMicrophoneDevice = devices[0];
        }
        StopMicrophone();
        microphoneClip = Microphone.Start(
            activeMicrophoneDevice,
            true,
            1,
            16000
        );
        microphoneEnabled = microphoneClip != null;
        microphoneButton.GetComponent<Image>().color = microphoneEnabled
            ? SuccessColor
            : FieldColor;
        realtime.SetMicrophoneEnabled(microphoneEnabled);
        SetMediaStatus(
            microphoneEnabled
            ? "Local microphone: " + activeMicrophoneDevice
            : "Could not start the microphone."
        );
    }

    private void StopMicrophone()
    {
        if (
            !string.IsNullOrWhiteSpace(activeMicrophoneDevice) &&
            Microphone.IsRecording(activeMicrophoneDevice)
        )
        {
            Microphone.End(activeMicrophoneDevice);
        }
        microphoneClip = null;
        microphoneEnabled = false;
        if (microphoneButton != null)
        {
            microphoneButton.GetComponent<Image>().color = FieldColor;
        }
        if (microphoneLevelText != null)
        {
            microphoneLevelText.text = "Mic level: —";
        }
        realtime.SetMicrophoneEnabled(false);
    }

    private void SelectNextMicrophone()
    {
        string[] devices = Microphone.devices;
        if (devices.Length == 0)
        {
            SetMediaStatus("No microphone device was found.");
            return;
        }
        int current = Array.IndexOf(devices, activeMicrophoneDevice);
        activeMicrophoneDevice =
            devices[(current + 1 + devices.Length) % devices.Length];
        SetMediaStatus(
            "Selected microphone: " + activeMicrophoneDevice
        );
        if (microphoneEnabled)
        {
            StopMicrophone();
            StartCoroutine(StartMicrophone());
        }
    }

    private void UpdateMicrophoneMeter()
    {
        if (
            microphoneClip == null ||
            string.IsNullOrWhiteSpace(activeMicrophoneDevice)
        )
        {
            return;
        }
        int position = Microphone.GetPosition(activeMicrophoneDevice);
        if (position <= 0)
        {
            return;
        }
        const int sampleCount = 256;
        float[] samples = new float[sampleCount];
        int start = Mathf.Max(0, position - sampleCount);
        microphoneClip.GetData(samples, start);
        float sum = 0f;
        for (int index = 0; index < samples.Length; index++)
        {
            sum += samples[index] * samples[index];
        }
        float rms = Mathf.Sqrt(sum / samples.Length);
        int percent = Mathf.Clamp(Mathf.RoundToInt(rms * 500f), 0, 100);
        microphoneLevelText.text =
            "Mic level: " + new string('#', percent / 10) +
            new string('.', 10 - percent / 10) + " " + percent + "%";
    }

    private void ToggleScreenShare()
    {
        bool requested = !screenShareEnabled;
        realtime.SetScreenShareEnabled(
            requested,
            delegate(bool success, string message)
            {
                if (success)
                {
                    screenShareEnabled = requested;
                    screenShareButtonText.text = screenShareEnabled
                        ? "Stop Sharing"
                        : "Share Screen";
                    screenShareButton.GetComponent<Image>().color =
                        screenShareEnabled ? PrimaryColor : FieldColor;
                }
                SetMediaStatus(message);
            }
        );
    }

    private void CycleWhiteboardTool(TMP_Text buttonText)
    {
        InterviewerWhiteboardTool next;
        if (whiteboard.Tool == InterviewerWhiteboardTool.Pen)
        {
            next = InterviewerWhiteboardTool.Highlighter;
        }
        else if (
            whiteboard.Tool == InterviewerWhiteboardTool.Highlighter
        )
        {
            next = InterviewerWhiteboardTool.Eraser;
        }
        else
        {
            next = InterviewerWhiteboardTool.Pen;
        }
        whiteboard.SetTool(next);
        buttonText.text = "Tool: " + next;
        SetWhiteboardStatus();
    }

    private void SetWhiteboardMode(InterviewerWhiteboardTool mode)
    {
        whiteboard.SetTool(mode);
        if (mode == InterviewerWhiteboardTool.Highlighter)
        {
            whiteboard.SetBrushColor(
                new Color(1f, 0.55f, 0.12f, 1f)
            );
        }
        else if (mode == InterviewerWhiteboardTool.Pen)
        {
            whiteboard.SetBrushColor(brushColors[brushColorIndex]);
        }
        RefreshWhiteboardModeButtons();
        SetWhiteboardStatus();
    }

    private void RefreshWhiteboardModeButtons()
    {
        if (
            whiteboard == null ||
            whiteboardDrawingButton == null ||
            whiteboardAnnotationButton == null ||
            whiteboardEraserButton == null
        )
        {
            return;
        }
        whiteboardDrawingButton.GetComponent<Image>().color =
            whiteboard.Tool == InterviewerWhiteboardTool.Pen
                ? PrimaryColor
                : FieldColor;
        whiteboardAnnotationButton.GetComponent<Image>().color =
            whiteboard.Tool == InterviewerWhiteboardTool.Highlighter
                ? new Color(0.90f, 0.48f, 0.10f, 1f)
                : FieldColor;
        whiteboardEraserButton.GetComponent<Image>().color =
            whiteboard.Tool == InterviewerWhiteboardTool.Eraser
                ? new Color(0.68f, 0.22f, 0.22f, 1f)
                : FieldColor;
    }

    private void ToggleWhiteboardExpanded()
    {
        if (whiteboardPanelRect == null)
        {
            return;
        }
        whiteboardExpanded = !whiteboardExpanded;
        if (whiteboardExpanded)
        {
            SetStretch(
                whiteboardPanelRect,
                Vector2.zero,
                Vector2.one,
                new Vector2(16f, 16f),
                new Vector2(-16f, -98f)
            );
            whiteboardPanelRect.SetAsLastSibling();
        }
        else
        {
            SetStretch(
                whiteboardPanelRect,
                new Vector2(0.255f, 0f),
                new Vector2(0.715f, 1f),
                new Vector2(8f, 16f),
                new Vector2(-8f, -98f)
            );
        }
        if (whiteboardExpandButton != null)
        {
            whiteboardExpandButton.GetComponentInChildren<TMP_Text>().text =
                whiteboardExpanded ? "Dock" : "Pop Out";
        }
    }

    private void SetLegacyWhiteboardStatus()
    {
        whiteboardStatusText.text =
            whiteboard.Tool + " • " +
            whiteboard.BrushSize + " px • " +
            (realtime.IsConnected ? "shared board" : "local board");
    }

    private void SetWhiteboardStatus()
    {
        string modeName = whiteboard.Tool ==
            InterviewerWhiteboardTool.Highlighter
            ? "Annotation"
            : whiteboard.Tool == InterviewerWhiteboardTool.Pen
                ? "Drawing"
                : "Eraser";
        whiteboardStatusText.text =
            modeName + " | " +
            whiteboard.BrushSize + " px | " +
            (realtime.IsConnected ? "shared board" : "local board");
    }

    private void WhiteboardChanged()
    {
        if (realtime.IsConnected)
        {
            realtime.PublishWhiteboardSnapshot(whiteboard.EncodePng());
        }
        SetWhiteboardStatus();
    }

    private void SaveWhiteboard()
    {
        try
        {
            string directory = Path.Combine(
                Application.persistentDataPath,
                "InterviewWhiteboards"
            );
            string path = whiteboard.SavePng(directory);
            whiteboardStatusText.text = "Saved: " + path;
        }
        catch (Exception error)
        {
            whiteboardStatusText.text =
                "Could not save whiteboard: " + error.Message;
        }
    }

    private void BrowseDataset()
    {
        string selected = OpenDatasetFile();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            datasetPathInput.text = selected;
            if (
                string.IsNullOrWhiteSpace(datasetNameInput.text) ||
                datasetNameInput.text == "interviewer-dataset"
            )
            {
                datasetNameInput.text =
                    Path.GetFileNameWithoutExtension(selected);
            }
            datasetStatusText.text =
                "Selected " + Path.GetFileName(selected) + ".";
        }
    }

    private async void ProcessDatasetClicked()
    {
        InterviewerDatasetSettings settings;
        string validationError;
        if (!TryReadDatasetSettings(out settings, out validationError))
        {
            datasetStatusText.text = validationError;
            return;
        }
        datasetStatusText.text = "Processing dataset locally…";
        datasetPreviewText.text = "Reading and validating rows…";
        string outputRoot = Path.Combine(
            Application.persistentDataPath,
            "InterviewerDatasets"
        );
        InterviewerDatasetResult result = await Task.Run(
            delegate
            {
                return InterviewerDatasetProcessor.Process(
                    settings,
                    outputRoot
                );
            }
        );
        datasetStatusText.text = result.success
            ? result.message + "\nSaved to: " + result.outputDirectory
            : "Dataset processing failed: " + result.message;
        datasetStatusText.color = result.success
            ? new Color(0.45f, 0.82f, 0.65f, 1f)
            : new Color(1f, 0.48f, 0.48f, 1f);
        datasetPreviewText.text = result.success
            ? result.preview
            : "Correct the parameters and try again.";
        if (result.success && realtime.IsConnected)
        {
            realtime.PublishDatasetManifest(result.manifestJson);
        }
    }

    private bool TryReadDatasetSettings(
        out InterviewerDatasetSettings settings,
        out string error
    )
    {
        settings = new InterviewerDatasetSettings();
        error = "";
        settings.sourcePath = datasetPathInput.text == null
            ? ""
            : datasetPathInput.text.Trim();
        settings.outputName = datasetNameInput.text == null
            ? ""
            : datasetNameInput.text.Trim();
        settings.labelColumn = labelColumnInput.text == null
            ? ""
            : labelColumnInput.text.Trim();
        settings.hasHeader = datasetHasHeader;
        settings.shuffle = shuffleDataset;
        settings.delimiter = delimiterMode;
        settings.normalization = normalizationMode;
        settings.missingValues = missingValueMode;

        if (
            !TryParseFloat(trainSplitInput.text, out settings.trainSplit) ||
            !TryParseFloat(
                validationSplitInput.text,
                out settings.validationSplit
            ) ||
            !int.TryParse(
                maximumRowsInput.text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out settings.maximumRows
            ) ||
            !int.TryParse(
                epochsInput.text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out settings.epochs
            ) ||
            !int.TryParse(
                batchSizeInput.text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out settings.batchSize
            ) ||
            !TryParseFloat(
                learningRateInput.text,
                out settings.learningRate
            )
        )
        {
            error =
                "Use valid numbers for splits, row limit, epochs, batch size, and learning rate.";
            return false;
        }
        return true;
    }

    private static bool TryParseFloat(string value, out float parsed)
    {
        return float.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out parsed
        );
    }

    private void CycleDelimiter()
    {
        delimiterMode =
            (InterviewerDelimiterMode)(
                ((int)delimiterMode + 1) %
                Enum.GetValues(typeof(InterviewerDelimiterMode)).Length
            );
        delimiterButtonText.text = "Delimiter: " + delimiterMode;
    }

    private void CycleNormalization()
    {
        normalizationMode =
            (InterviewerNormalizationMode)(
                ((int)normalizationMode + 1) %
                Enum.GetValues(typeof(InterviewerNormalizationMode)).Length
            );
        normalizationButtonText.text =
            "Normalize: " + normalizationMode;
    }

    private void CycleMissingValues()
    {
        missingValueMode =
            (InterviewerMissingValueMode)(
                ((int)missingValueMode + 1) %
                Enum.GetValues(typeof(InterviewerMissingValueMode)).Length
            );
        missingValuesButtonText.text =
            "Missing: " + missingValueMode;
    }

    private void OpenDatasetFolder()
    {
        string directory = Path.Combine(
            Application.persistentDataPath,
            "InterviewerDatasets"
        );
        Directory.CreateDirectory(directory);
        Application.OpenURL(directory);
    }

    private void OnAuthenticationChanged(bool authenticated, string message)
    {
        if (launcherButton != null)
        {
            launcherButton.interactable = authenticated;
        }
        if (sandboxButton != null)
        {
            sandboxButton.interactable = authenticated;
        }
        if (!authenticated && overlay != null && overlay.activeSelf)
        {
            LeaveRoom();
            StopCamera();
            StopMicrophone();
            Hide();
        }
    }

    private static string OpenDatasetFile()
    {
#if UNITY_EDITOR
        return UnityEditor.EditorUtility.OpenFilePanel(
            "Choose interviewer dataset",
            "",
            "csv,tsv,txt"
        );
#elif UNITY_STANDALONE_WIN
        OpenFileName dialog = new OpenFileName();
        dialog.structSize = Marshal.SizeOf(dialog);
        dialog.filter =
            "Dataset files (*.csv;*.tsv;*.txt)\0*.csv;*.tsv;*.txt\0" +
            "All files (*.*)\0*.*\0";
        dialog.file = new string(new char[2048]);
        dialog.maxFile = dialog.file.Length;
        dialog.fileTitle = new string(new char[256]);
        dialog.maxFileTitle = dialog.fileTitle.Length;
        dialog.initialDirectory = "";
        dialog.title = "Choose interviewer dataset";
        dialog.flags = 0x00001000 | 0x00000800 | 0x00000008;
        return GetOpenFileName(dialog) ? dialog.file : "";
#else
        return "";
#endif
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class OpenFileName
    {
        public int structSize;
        public IntPtr dialogOwner;
        public IntPtr instance;
        public string filter;
        public string customFilter;
        public int maxCustomFilter;
        public int filterIndex;
        public string file;
        public int maxFile;
        public string fileTitle;
        public int maxFileTitle;
        public string initialDirectory;
        public string title;
        public int flags;
        public short fileOffset;
        public short fileExtension;
        public string defaultExtension;
        public IntPtr customData;
        public IntPtr hook;
        public string templateName;
        public IntPtr reservedPointer;
        public int reservedInt;
        public int flagsExtended;
    }

    [DllImport(
        "Comdlg32.dll",
        CharSet = CharSet.Auto,
        SetLastError = true
    )]
    private static extern bool GetOpenFileName(
        [In, Out] OpenFileName openFileName
    );
#endif

    private TMP_InputField AddDatasetField(
        Transform parent,
        string label,
        string placeholder,
        float top
    )
    {
        TMP_Text fieldLabel = CreateText(
            parent,
            label,
            14f,
            FontStyles.Normal
        );
        SetTopText(fieldLabel.rectTransform, 22f, top + 9f, 126f, 32f);
        fieldLabel.alignment = TextAlignmentOptions.MidlineLeft;
        TMP_InputField input = CreateInput(
            parent,
            placeholder,
            "",
            344f,
            42f
        );
        SetTopControl(
            input.GetComponent<RectTransform>(),
            152f,
            top,
            344f,
            42f
        );
        return input;
    }

    private TMP_InputField AddCompactDatasetField(
        Transform parent,
        string label,
        string value,
        float left,
        float top,
        float width
    )
    {
        TMP_Text fieldLabel = CreateText(
            parent,
            label,
            13f,
            FontStyles.Normal
        );
        SetTopText(fieldLabel.rectTransform, left, top, width, 24f);
        fieldLabel.alignment = TextAlignmentOptions.MidlineLeft;
        TMP_InputField input = CreateInput(
            parent,
            label,
            value,
            width,
            38f
        );
        SetTopControl(
            input.GetComponent<RectTransform>(),
            left,
            top + 26f,
            width,
            38f
        );
        return input;
    }

    private static GameObject CreateUiObject(string name, Transform parent)
    {
        GameObject result = new GameObject(name, typeof(RectTransform));
        result.transform.SetParent(parent, false);
        return result;
    }

    private static GameObject CreatePanel(
        Transform parent,
        string name,
        Color color
    )
    {
        GameObject panel = CreateUiObject(name, parent);
        panel.AddComponent<Image>().color = color;
        return panel;
    }

    private static TMP_Text CreateText(
        Transform parent,
        string value,
        float fontSize,
        FontStyles style
    )
    {
        GameObject textObject = CreateUiObject("Text", parent);
        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.color = Color.white;
        text.raycastTarget = false;
        return text;
    }

    private static void CreateSectionTitle(
        Transform parent,
        string value,
        float top
    )
    {
        TMP_Text title = CreateText(
            parent,
            value,
            17f,
            FontStyles.Bold
        );
        SetTopText(title.rectTransform, 22f, -top, 460f, 34f);
        title.alignment = TextAlignmentOptions.MidlineLeft;
        title.color = new Color(0.76f, 0.82f, 0.94f, 1f);
    }

    private static Button CreateButton(
        Transform parent,
        string label,
        Color color,
        float width,
        float height
    )
    {
        GameObject buttonObject = CreateUiObject(
            "Button - " + label,
            parent
        );
        RectTransform rect = buttonObject.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(width, height);
        Image image = buttonObject.AddComponent<Image>();
        image.color = color;
        Button button = buttonObject.AddComponent<Button>();
        ColorBlock colors = button.colors;
        colors.normalColor = color;
        colors.highlightedColor = Color.Lerp(color, Color.white, 0.14f);
        colors.pressedColor = Color.Lerp(color, Color.black, 0.18f);
        colors.disabledColor =
            new Color(color.r, color.g, color.b, 0.35f);
        button.colors = colors;
        TMP_Text text = CreateText(
            buttonObject.transform,
            label,
            15f,
            FontStyles.Bold
        );
        SetStretch(text.rectTransform, Vector2.zero, Vector2.one);
        text.alignment = TextAlignmentOptions.Center;
        return button;
    }

    private static Button CreateIconButton(
        Transform parent,
        string accessibleName,
        MediaIcon icon,
        Color color,
        float size
    )
    {
        Button button = CreateButton(
            parent,
            "",
            color,
            size,
            size
        );
        button.gameObject.name = "Button - " + accessibleName;
        TMP_Text emptyText = button.GetComponentInChildren<TMP_Text>();
        if (emptyText != null)
        {
            emptyText.gameObject.SetActive(false);
        }

        Color iconColor = new Color(0.92f, 0.95f, 1f, 1f);
        if (icon == MediaIcon.Microphone)
        {
            CreateIconShape(
                button.transform,
                "Microphone Capsule",
                new Vector2(0f, 7f),
                new Vector2(14f, 25f),
                iconColor
            );
            CreateIconShape(
                button.transform,
                "Microphone Stem",
                new Vector2(0f, -10f),
                new Vector2(4f, 12f),
                iconColor
            );
            CreateIconShape(
                button.transform,
                "Microphone Base",
                new Vector2(0f, -17f),
                new Vector2(22f, 4f),
                iconColor
            );
        }
        else
        {
            CreateIconShape(
                button.transform,
                "Camera Body",
                new Vector2(0f, -1f),
                new Vector2(32f, 23f),
                iconColor
            );
            CreateIconShape(
                button.transform,
                "Camera Lens",
                new Vector2(0f, -1f),
                new Vector2(13f, 13f),
                new Color(0.18f, 0.24f, 0.34f, 1f)
            );
            CreateIconShape(
                button.transform,
                "Camera Top",
                new Vector2(-8f, 13f),
                new Vector2(12f, 5f),
                iconColor
            );
        }
        return button;
    }

    private static void CreateIconShape(
        Transform parent,
        string name,
        Vector2 position,
        Vector2 size,
        Color color
    )
    {
        GameObject shape = CreateUiObject(name, parent);
        RectTransform rect = shape.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        Image image = shape.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
    }

    private static TMP_InputField CreateInput(
        Transform parent,
        string placeholderValue,
        string initialValue,
        float width,
        float height
    )
    {
        GameObject inputObject = CreateUiObject(
            "Input - " + placeholderValue,
            parent
        );
        RectTransform inputRect =
            inputObject.GetComponent<RectTransform>();
        inputRect.sizeDelta = new Vector2(width, height);
        inputObject.AddComponent<Image>().color = FieldColor;
        TMP_InputField input = inputObject.AddComponent<TMP_InputField>();

        GameObject viewportObject = CreateUiObject(
            "Text Area",
            inputObject.transform
        );
        RectTransform viewport =
            viewportObject.GetComponent<RectTransform>();
        SetStretch(
            viewport,
            Vector2.zero,
            Vector2.one,
            new Vector2(12f, 5f),
            new Vector2(-12f, -5f)
        );
        viewportObject.AddComponent<RectMask2D>();

        TMP_Text placeholder = CreateText(
            viewportObject.transform,
            placeholderValue,
            15f,
            FontStyles.Italic
        );
        SetStretch(placeholder.rectTransform, Vector2.zero, Vector2.one);
        placeholder.alignment = TextAlignmentOptions.MidlineLeft;
        placeholder.color = new Color(0.52f, 0.58f, 0.68f, 1f);

        TMP_Text valueText = CreateText(
            viewportObject.transform,
            initialValue,
            15f,
            FontStyles.Normal
        );
        SetStretch(valueText.rectTransform, Vector2.zero, Vector2.one);
        valueText.alignment = TextAlignmentOptions.MidlineLeft;

        input.textViewport = viewport;
        input.textComponent = valueText;
        input.placeholder = placeholder;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.text = initialValue;
        return input;
    }

    private static void SetHeaderControl(
        RectTransform rect,
        float left,
        float width
    )
    {
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.sizeDelta = new Vector2(width, 44f);
        rect.anchoredPosition = new Vector2(left, 0f);
    }

    private static void SetHeaderText(
        RectTransform rect,
        float left,
        float width
    )
    {
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.sizeDelta = new Vector2(width, 0f);
        rect.anchoredPosition = new Vector2(left, 0f);
    }

    private static void SetTopRect(
        RectTransform rect,
        float left,
        float height,
        float top
    )
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.offsetMin = new Vector2(left, -top - height);
        rect.offsetMax = new Vector2(-left, -top);
    }

    private static void SetTopControl(
        RectTransform rect,
        float left,
        float top,
        float width,
        float height
    )
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = new Vector2(left, -top);
    }

    private static void SetTopText(
        RectTransform rect,
        float left,
        float top,
        float width,
        float height
    )
    {
        SetTopControl(rect, left, top, width, height);
    }

    private static void SetAnchored(
        RectTransform rect,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 offsetMin,
        Vector2 offsetMax
    )
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }

    private static void SetStretch(
        RectTransform rect,
        Vector2 anchorMin,
        Vector2 anchorMax
    )
    {
        SetStretch(rect, anchorMin, anchorMax, Vector2.zero, Vector2.zero);
    }

    private static void SetStretch(
        RectTransform rect,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 offsetMin,
        Vector2 offsetMax
    )
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }
}
