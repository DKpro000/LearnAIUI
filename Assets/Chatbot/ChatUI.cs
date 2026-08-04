using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;

public class ChatUI : MonoBehaviour
{
    public Transform content;
    public TMP_InputField inputField;
    public Button sendButton;
    public GameObject messagePrefab;
    public ScrollRect scrollRect;

    private string apiUrl = "http://127.0.0.1:8000/chat";
    private string lastSeenUserMessage = "";
    private bool pendingBotReply = false;
    private GameObject thinkingMessage = null;

    void Start()
    {
        sendButton.onClick.AddListener(OnSendClicked);
        StartCoroutine(LoadChatHistory());
        StartCoroutine(PollingMessages());
    }

    IEnumerator LoadChatHistory()
    {
        UnityWebRequest request = UnityWebRequest.Get(apiUrl);
        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            ChatHistory history = JsonUtility.FromJson<ChatHistory>("{\"messages\":" + request.downloadHandler.text + "}");
            if (history == null || history.messages == null) yield break;
            foreach (var msg in history.messages)
            {
                AddMessage("<color=blue>You:</color> " + msg.user);
                AddMessage("<color=blue>Bot:</color> " + msg.bot);
            }
            if (history.messages.Count > 0)
                lastSeenUserMessage = history.messages[history.messages.Count - 1].user;
            yield return new WaitForEndOfFrame();
            scrollRect.verticalNormalizedPosition = 0f;
        }
    }

    void AddMessage(string text)
    {
        GameObject msg = Instantiate(messagePrefab, content);
        msg.GetComponent<TMP_Text>().text = text;
        StartCoroutine(ScrollToBottom());
    }

    IEnumerator ScrollToBottom()
    {
        yield return new WaitForEndOfFrame();
        yield return new WaitForEndOfFrame();
        scrollRect.verticalNormalizedPosition = 0f;
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift))
                OnSendClicked();
        }
    }

    void OnSendClicked()
    {
        if (string.IsNullOrEmpty(inputField.text)) return;
        string message = inputField.text;
        inputField.text = "";
        AddMessage("<color=blue>You:</color> " + message);
        thinkingMessage = Instantiate(messagePrefab, content);
        thinkingMessage.GetComponent<TMP_Text>().text = "<color=grey>Thinking.</color>";
        StartCoroutine(ScrollToBottom());
        StartCoroutine(AnimateThinking());
        pendingBotReply = true;
        StartCoroutine(PostMessage(message));
    }

    IEnumerator AnimateThinking()
    {
        string[] frames = { "Thinking.", "Thinking..", "Thinking..." };
        int i = 0;
        while (thinkingMessage != null)
        {
            thinkingMessage.GetComponent<TMP_Text>().text = "<color=grey>" + frames[i % 3] + "</color>";
            i++;
            yield return new WaitForSeconds(0.4f);
        }
    }

    IEnumerator PostMessage(string message)
    {
        string escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        string jsonBody = "{\"message\": \"" + escaped + "\"}";
        UnityWebRequest request = new UnityWebRequest(apiUrl, "POST");
        request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(jsonBody));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        yield return request.SendWebRequest();
    }

    IEnumerator PollingMessages()
    {
        while (true)
        {
            yield return new WaitForSeconds(1f);
            UnityWebRequest request = UnityWebRequest.Get(apiUrl + "?t=" + System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success) continue;
            ChatHistory history = JsonUtility.FromJson<ChatHistory>("{\"messages\":" + request.downloadHandler.text + "}");
            if (history == null || history.messages == null || history.messages.Count == 0) continue;

            string latestUser = history.messages[history.messages.Count - 1].user;
            string latestBot = history.messages[history.messages.Count - 1].bot;

            if (latestUser == lastSeenUserMessage) continue;

            lastSeenUserMessage = latestUser;
            if (pendingBotReply)
            {
                pendingBotReply = false;
                if (thinkingMessage != null) { Destroy(thinkingMessage); thinkingMessage = null; }
            }
            else
                AddMessage("<color=blue>You:</color> " + latestUser);
            AddMessage("<color=blue>Bot:</color> " + latestBot);
        }
    }

    [System.Serializable]
    class ChatMessage
    {
        public string user;
        public string bot;
    }

    [System.Serializable]
    class ChatHistory
    {
        public List<ChatMessage> messages;
    }
}
