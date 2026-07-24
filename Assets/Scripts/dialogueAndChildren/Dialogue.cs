using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class Dialogue : MonoBehaviour
{
    // Define a clean structure for Inspector setup
    [System.Serializable]
    public class DialogueLine
    {
        [TextArea(2, 5)] public string text;
        public bool isSelf; // true = Self, false = Other
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

    [Header("Settings")]
    public float textSpeed = 0.05f;
    public float fadeDuration = 0.5f;

    [Header("Dialogue Content")]
    public List<DialogueSceneData> scenes = new List<DialogueSceneData>();

    [Header("Background")]
    public Image backgroundImageComponent;

    [Header("buttons")]
    public GameObject nextButton;

    private int currentSceneIndex = 0;
    private int currentLineIndex = 0;
    private Image currentDialogueBox;
    private GameObject currentBg;
    private Coroutine typingCoroutine;

    void Start()
    {
        textComponent.text = string.Empty;
        StartCoroutine(FadeInAndStart());
    }

    private void UpdateActiveDialogueBox()
    {
        DialogueLine currentLine = GetCurrentLine();
        if (currentLine == null) return;

        if (currentLine.isSelf)
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

    IEnumerator TypeLine()
    {
        UpdateActiveDialogueBox();
        textComponent.text = string.Empty;

        string fullText = GetCurrentLine().text;

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
            textComponent.text = currentLine.text;
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

        // Prepare next scene pointer
        currentSceneIndex++;
        currentLineIndex = 0;
        if (currentSceneIndex != scenes.Count)
        {
            StartCoroutine(FadeInAndStart());
            boxColor.a = 1;
            textColor.a = 1;
            currentDialogueBox.color = boxColor;
            textComponent.color = textColor;
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