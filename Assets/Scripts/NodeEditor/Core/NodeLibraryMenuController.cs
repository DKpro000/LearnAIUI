using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class NodeLibraryMenuController : MonoBehaviour
{
    [Header("Backend")]
    public NodeLibraryClient nodeLibraryClient;

    [Header("Graph Editor")]
    public GraphEditorController graphEditorController;

    [Header("UI")]
    public RectTransform contentParent;
    public GameObject categoryHeaderPrefab;
    public GameObject nodeButtonPrefab;

    [Header("Filter")]
    public bool showAdvancedNodes = true;

    [Header("Container Templates")]
    public ContainerTemplateLibrary containerTemplateLibrary;

    private void Start()
    {
        FixContentLayout();
        LoadNodeLibrary();
    }

    private void FixContentLayout()
    {
        if (contentParent == null)
        {
            Debug.LogError("Content Parent is null. Please assign ScrollView/Viewport/Content.");
            return;
        }

        contentParent.anchorMin = new Vector2(0f, 1f);
        contentParent.anchorMax = new Vector2(1f, 1f);
        contentParent.pivot = new Vector2(0.5f, 1f);
        contentParent.anchoredPosition = Vector2.zero;
        contentParent.offsetMin = new Vector2(0f, contentParent.offsetMin.y);
        contentParent.offsetMax = new Vector2(0f, contentParent.offsetMax.y);
        contentParent.sizeDelta = new Vector2(0f, 100f);

        VerticalLayoutGroup layout = contentParent.GetComponent<VerticalLayoutGroup>();
        if (layout == null)
        {
            layout = contentParent.gameObject.AddComponent<VerticalLayoutGroup>();
        }

        layout.padding = new RectOffset(6, 6, 6, 6);
        layout.spacing = 4f;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        ContentSizeFitter fitter = contentParent.GetComponent<ContentSizeFitter>();
        if (fitter == null)
        {
            fitter = contentParent.gameObject.AddComponent<ContentSizeFitter>();
        }

        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    public void LoadNodeLibrary()
    {
        if (nodeLibraryClient == null)
        {
            Debug.LogError("NodeLibraryClient is not assigned.");
            return;
        }

        StartCoroutine(nodeLibraryClient.FetchNodeLibrary(OnNodeLibraryLoaded));
    }

    private void OnNodeLibraryLoaded(NodeLibraryResponse response)
    {
        Debug.Log("Loaded node library count: " + response.count);
        BuildMenu(response.library);
    }

    private void BuildMenu(List<NodeDefinition> definitions)
    {
        ClearMenu();

        string currentCategory = "";

        foreach (NodeDefinition definition in definitions)
        {
            if (!ShouldShowDefinition(definition))
            {
                continue;
            }

            if (definition.category != currentCategory)
            {
                currentCategory = definition.category;
                CreateCategoryHeader(currentCategory);
            }

            CreateNodeButton(definition);
        }

        BuildSavedContainerTemplates();
        LayoutRebuilder.ForceRebuildLayoutImmediate(contentParent);
    }

    private bool ShouldShowDefinition(NodeDefinition definition)
    {
        if (showAdvancedNodes)
        {
            return true;
        }

        if (definition.nodeKind == "UtilityNode")
        {
            return false;
        }

        if (definition.nodeKind == "WrapperNode")
        {
            return false;
        }

        if (definition.nodeKind == "ParameterNode")
        {
            return false;
        }

        return true;
    }

    private void ClearMenu()
    {
        if (contentParent == null)
        {
            return;
        }

        for (int i = contentParent.childCount - 1; i >= 0; i--)
        {
            Destroy(contentParent.GetChild(i).gameObject);
        }
    }

    private void CreateCategoryHeader(string categoryName)
    {
        GameObject headerObject = Instantiate(categoryHeaderPrefab, contentParent);

        RectTransform rect = headerObject.GetComponent<RectTransform>();
        if (rect != null)
        {
            rect.localScale = Vector3.one;
        }

        LayoutElement layout = headerObject.GetComponent<LayoutElement>();
        if (layout == null)
        {
            layout = headerObject.AddComponent<LayoutElement>();
        }

        layout.minHeight = 28f;
        layout.preferredHeight = 28f;
        layout.flexibleHeight = 0f;

        TMP_Text text = headerObject.GetComponentInChildren<TMP_Text>();
        if (text != null)
        {
            text.text = categoryName;
            text.fontSize = 14f;
            text.color = Color.black;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
        }
    }

    private void CreateNodeButton(NodeDefinition definition)
    {
        GameObject buttonObject = Instantiate(nodeButtonPrefab, contentParent);

        RectTransform rect = buttonObject.GetComponent<RectTransform>();
        if (rect != null)
        {
            rect.localScale = Vector3.one;
        }

        LayoutElement layout = buttonObject.GetComponent<LayoutElement>();
        if (layout == null)
        {
            layout = buttonObject.AddComponent<LayoutElement>();
        }

        layout.minHeight = 32f;
        layout.preferredHeight = 32f;
        layout.flexibleHeight = 0f;

        TMP_Text text = buttonObject.GetComponentInChildren<TMP_Text>();
        if (text != null)
        {
            text.text = definition.displayName;
            text.fontSize = 14f;
            text.color = Color.black;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
        }

        Button button = buttonObject.GetComponent<Button>();
        if (button != null)
        {
            NodeDefinition capturedDefinition = definition;

            button.onClick.AddListener(() =>
            {
                graphEditorController.AddNodeFromDefinition(capturedDefinition);
            });
        }
    }

    private void BuildSavedContainerTemplates()
    {
        if (containerTemplateLibrary == null)
        {
            return;
        }

        var templates = containerTemplateLibrary.GetTemplates();

        if (templates == null || templates.Count == 0)
        {
            return;
        }

        CreateCategoryHeader("Saved Containers");

        foreach (ContainerTemplateData template in templates)
        {
            CreateTemplateButton(template);
        }
    }

    private void CreateTemplateButton(ContainerTemplateData template)
    {
        GameObject buttonObject = Instantiate(nodeButtonPrefab, contentParent);

        LayoutElement layout = buttonObject.GetComponent<LayoutElement>();
        if (layout == null)
        {
            layout = buttonObject.AddComponent<LayoutElement>();
        }

        layout.minHeight = 32f;
        layout.preferredHeight = 32f;
        layout.flexibleHeight = 0f;

        TMP_Text text = buttonObject.GetComponentInChildren<TMP_Text>();
        if (text != null)
        {
            text.text = template.displayName;
            text.fontSize = 14f;
            text.color = Color.black;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
        }

        Button button = buttonObject.GetComponent<Button>();
        if (button != null)
        {
            button.onClick.RemoveAllListeners();
        }

        ContainerTemplateButtonView view =
            buttonObject.GetComponent<ContainerTemplateButtonView>();

        if (view == null)
        {
            view = buttonObject.AddComponent<ContainerTemplateButtonView>();
        }

        view.Setup(
            template,
            graphEditorController,
            containerTemplateLibrary,
            this
        );
    }

    public void RefreshMenu()
    {
        LoadNodeLibrary();
    }
}