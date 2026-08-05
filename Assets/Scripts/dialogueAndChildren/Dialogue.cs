using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;


[System.Serializable]
public class SaveData
{
    public string playerName;
}
public class Dialogue : MonoBehaviour
{
    // Define a clean structure for Inspector setup
    [System.Serializable]
    public class DialogueLine
    {
        [TextArea(2, 5)] public string text;
        public bool Self; // true = Self, false = Other
        public Sprite Finch;
        public bool FinchPresent;
        public bool inputBox;
        public string name;
        public AudioClip bgm;
        public AudioClip sfx;
        [Header("Showing items")]
        public bool showSomething;
        public Sprite showItem;
        [Header("picking game")]
        public bool pickingGame;
        public GameObject pickingGameCanvas;
    }

    [System.Serializable]
    public class DialogueSceneData
    {
        public int sceneID;
        public Sprite sceneBackground;
        public List<DialogueLine> dialogueLines = new List<DialogueLine>();
    }

    

    [Header("UI Elements")]
    public TextMeshProUGUI textComponent;
    public Image SelfDialogueBox;
    public Image OtherDialogueBox;
    public Image Finch;
    public GameObject CoverBG;
    public TextMeshProUGUI NameText;
    public AudioSource bgmSource;
    public AudioSource sfxSource;
    public GameObject showItemCanvas;
    public Image showItemCanvasImage;

    [Header("Settings")]
    public float textSpeed = 0.05f;
    public float fadeDuration = 0.5f;

    [Header("Dialogue Content")]
    public List<DialogueSceneData> scenes = new List<DialogueSceneData>();

    [Header("Background")]
    public Image backgroundImageComponent;

    [Header("Input")]
    public GameObject nextButton;
    public TMP_InputField TextInput;

    [Header("Extras")]
    public GameObject ExtraStuff;
    public GraphEditorController GEC;

    private string userName;
    private SaveData gameSaveData = new SaveData();
    private string saveFilePath;

    private int currentSceneIndex = 0;
    private int currentLineIndex = 0;
    private Image currentDialogueBox;
    private GameObject currentBg;
    private Coroutine typingCoroutine;
    private string data;
    

    void Start()
    {
        textComponent.text = string.Empty;

        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        saveFilePath = Path.Combine(projectRoot, "player_data.json");

        textComponent.gameObject.SetActive(true);
        

        StartCoroutine(FadeInAndStart());
    }

    private void UpdateActiveDialogueBox()
    {
        DialogueLine currentLine = GetCurrentLine();
        if (currentLine == null) return;

        nextButton.SetActive(true);
        NameText.gameObject.SetActive(true);
        textComponent.gameObject.SetActive(true);

        Sprite newFinchAction = currentLine.Finch;

        if (currentLine.Self)
        {
            SelfDialogueBox.gameObject.SetActive(true);
            OtherDialogueBox.gameObject.SetActive(false);
            currentDialogueBox = SelfDialogueBox;
        }
        else
        {
            SelfDialogueBox.gameObject.SetActive(false);
            OtherDialogueBox.gameObject.SetActive(true);
            currentDialogueBox = OtherDialogueBox;
        }

        //changing finch the teacher's image based on the current line
        if(currentLine.FinchPresent)
        {
            Finch.gameObject.SetActive(true);
            if (currentLine.Finch != null)
            {
                Finch.sprite = newFinchAction;
            }
        }
        else
        {
            Finch.gameObject.SetActive(false);
        }

        //turning on and off input
        if (currentLine.inputBox)
        {
            //activate the input
            TextInput.gameObject.SetActive(true);
            TextInput.onSubmit.RemoveListener(OnPlayerFinishedTyping);
            TextInput.onSubmit.AddListener(OnPlayerFinishedTyping);

            //darken the back
            CoverBG.SetActive(true);
        }
        else
        {
            TextInput.gameObject.SetActive(false);
            TextInput.onSubmit.RemoveListener(OnPlayerFinishedTyping);

            CoverBG.SetActive(false);
        }

        //changing the name of the speaker
        if(currentLine.name != null)
        {
            NameText.gameObject.SetActive(true);
            if (currentLine.name != "{name}")
            {
                NameText.text = currentLine.name;
            }
            else
            {
                NameText.text = userName;
            }
        }
        else
        {
            NameText.gameObject.SetActive(false);
        }

        // music
        // bg music
        if (currentLine.bgm != null)
        {
            bgmSource.clip = currentLine.bgm;
            bgmSource.loop = true;
            bgmSource.Play();
        }
        // sfx music
        if (currentLine.sfx != null)
        {
            sfxSource.PlayOneShot(currentLine.sfx);
        }

        // show items
        if (currentLine.showSomething)
        {
            //load image and display
            showItemCanvasImage.sprite = currentLine.showItem;
            showItemCanvas.SetActive(true);
        }
        else
        {
            showItemCanvas.SetActive(false);
        }

        //display picking game
        if (currentLine.pickingGame)
        {
            currentLine.pickingGameCanvas.SetActive(true);
            
            StartCoroutine(DisableParentAtEndOfFrame());
        }
    }
    private System.Collections.IEnumerator DisableParentAtEndOfFrame()
    {
        yield return new WaitForSeconds(2);
        if (transform.parent != null)
        {
            transform.parent.gameObject.SetActive(false);
        }
        if (GEC != null)
        {
            GEC.LoadGraphFromFile();
            Debug.Log("Graph loaded from file.");
        }
    }

    private void OnPlayerFinishedTyping(string finalText)
    {
        Debug.Log($"Player submitted the value: {finalText}");

        // storing palyer's name
        data = finalText;
        userName = finalText;
        gameSaveData.playerName = finalText;
        //saving into user preferences
        SaveGame();

        NextLine();
        Debug.Log(userName);
        
    }

    public void SaveGame()
    {
        // Convert the C# object to a formatted JSON string
        string json = JsonUtility.ToJson(gameSaveData, true); // 'true' formats it nicely with line breaks

        // Write the string to a text file
        File.WriteAllText(saveFilePath, json);

        Debug.Log($"<color=green>Game Saved!</color> Path: {saveFilePath}");
    }

    void UpdateActiveBg()
    {
        if (currentSceneIndex >= scenes.Count) return;

        Sprite newBg = scenes[currentSceneIndex].sceneBackground;
        if (backgroundImageComponent != null && newBg != null)
        {
            backgroundImageComponent.sprite = newBg;
        }
    }
    private string ProcessDialogueText(string rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return "";

        string nameToInsert = string.IsNullOrEmpty(data) ? "Player" : data;


        return rawText
        .Replace("{name}", userName)
        .Replace("{input}", nameToInsert);
    }
    IEnumerator TypeLine()
    {
        UpdateActiveDialogueBox();
        textComponent.text = string.Empty;

        //allows inputs
        string fullText = ProcessDialogueText(GetCurrentLine().text);

        foreach (char c in fullText.ToCharArray())
        {
            textComponent.text += c;
            yield return new WaitForSeconds(textSpeed);
        }

        typingCoroutine = null;
    }

    IEnumerator FadeInAndStart()
    {
        nextButton.SetActive(true);

        currentLineIndex = 0;
        UpdateActiveDialogueBox();
        UpdateActiveBg();

        float elapsedTime = 0f;
        Color boxColor = currentDialogueBox.color;
        Color textColor = textComponent.color;

        boxColor.a = 0f;
        textColor.a = 0f;
        currentDialogueBox.color = boxColor;
        textComponent.color = textColor;

        while (elapsedTime < fadeDuration)
        {
            elapsedTime += Time.deltaTime;
            float lerpValue = Mathf.Lerp(0f, 1f, elapsedTime / fadeDuration);

            boxColor.a = lerpValue;
            textColor.a = lerpValue;
            currentDialogueBox.color = boxColor;
            textComponent.color = textColor;

            yield return null;
        }

        boxColor.a = 1f;
        textColor.a = 1f;
        currentDialogueBox.color = boxColor;
        textComponent.color = textColor;

        StartDialogue();
    }

    void StartDialogue()
    {
        if (typingCoroutine != null) StopCoroutine(typingCoroutine);
        typingCoroutine = StartCoroutine(TypeLine());
    }
    

    public void NextLine()
    {
        DialogueLine currentLine = GetCurrentLine();
        if (currentLine == null) return;

        // If currently typing, skip animation and instantly show full text
        if (typingCoroutine != null)
        {
            StopCoroutine(typingCoroutine);
            typingCoroutine = null;
            textComponent.text = ProcessDialogueText(currentLine.text);
            return;
        }

        // Advance to next line in scene
        List<DialogueLine> currentSceneLines = scenes[currentSceneIndex].dialogueLines;
        if (currentLineIndex < currentSceneLines.Count - 1)
        {
            currentLineIndex++;
            typingCoroutine = StartCoroutine(TypeLine());
        }
        else
        {
            // End of current scene
            StartCoroutine(FadeOutAndClose());
        }
    }

    IEnumerator FadeOutAndClose()
    {
        float elapsedTime = 0f;
        Color boxColor = currentDialogueBox.color;
        Color textColor = textComponent.color;

        while (elapsedTime < fadeDuration)
        {
            elapsedTime += Time.deltaTime;
            float lerpValue = Mathf.Lerp(1f, 0f, elapsedTime / fadeDuration);

            boxColor.a = lerpValue;
            textColor.a = lerpValue;
            currentDialogueBox.color = boxColor;
            textComponent.color = textColor;

            yield return null;
        }

        nextButton.SetActive(false);
        NameText.gameObject.SetActive(false);
        textComponent.gameObject.SetActive(false);
        CoverBG.SetActive(false);
        Finch.gameObject.SetActive(false);
        if(ExtraStuff != null)
        {
            ExtraStuff.SetActive(false);
        }
        

        // Prepare next scene pointer
        currentSceneIndex++;
        currentLineIndex = 0;
        if (currentSceneIndex != scenes.Count)
        {
            boxColor.a = 1f;
            textColor.a = 1f;
            currentDialogueBox.color = boxColor;
            textComponent.color = textColor;
            StartCoroutine(FadeInAndStart());
        }
    }

    private DialogueLine GetCurrentLine()
    {
        if (currentSceneIndex < scenes.Count && currentLineIndex < scenes[currentSceneIndex].dialogueLines.Count)
        {
            return scenes[currentSceneIndex].dialogueLines[currentLineIndex];
        }
        return null;
    }
}