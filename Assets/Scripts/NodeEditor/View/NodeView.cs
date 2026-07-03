using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class NodeView : MonoBehaviour, IBeginDragHandler, IDragHandler, IPointerClickHandler
{
    [Header("UI")]
    public TMP_Text titleText;
    public TMP_Text categoryText;
    public TMP_Text kindText;
    public TMP_Text resultText;

    [Header("Parameter UI")]
    public RectTransform paramsParent;
    public GameObject paramRowPrefab;

    [Header("Ports")]
    public RectTransform inputPortsParent;
    public RectTransform outputPortsParent;
    public GameObject portPrefab;

    [Header("Layout")]
    public float nodeWidth = 320f;
    public float headerHeight = 76f;
    public float paramRowHeight = 30f;
    public float paramSpacing = 4f;
    public float portHeight = 26f;
    public float bottomPadding = 12f;

    private NodeData nodeData;
    private RectTransform rectTransform;
    private Canvas canvas;
    private GraphEditorController graphEditor;
    private Image backgroundImage;

    public void Setup(NodeData data, GraphEditorController editor)
    {
        nodeData = data;
        graphEditor = editor;

        rectTransform = GetComponent<RectTransform>();
        canvas = GetComponentInParent<Canvas>();
        backgroundImage = GetComponent<Image>();

        AutoBindReferences();
        RefreshTexts();

        if (rectTransform != null)
        {
            rectTransform.anchoredPosition = data.position;
        }

        BuildParameterRows();
        BuildPorts();
        ApplyFullLayout();
    }

    private void AutoBindReferences()
    {
        if (titleText == null)
        {
            Transform t = transform.Find("TitleText");
            if (t != null) titleText = t.GetComponent<TMP_Text>();
        }

        if (categoryText == null)
        {
            Transform t = transform.Find("CategoryText");
            if (t != null) categoryText = t.GetComponent<TMP_Text>();
        }

        if (kindText == null)
        {
            Transform t = transform.Find("KindText");
            if (t != null) kindText = t.GetComponent<TMP_Text>();
        }

        if (paramsParent == null)
        {
            Transform t = transform.Find("ParamsParent");
            if (t != null) paramsParent = t.GetComponent<RectTransform>();
        }

        if (inputPortsParent == null)
        {
            Transform t = transform.Find("InputPortsParent");
            if (t != null) inputPortsParent = t.GetComponent<RectTransform>();
        }

        if (outputPortsParent == null)
        {
            Transform t = transform.Find("OutputPortsParent");
            if (t != null) outputPortsParent = t.GetComponent<RectTransform>();
        }

        if (resultText == null)
        {
            Transform t = transform.Find("ResultText");
            if (t != null)
            {
                resultText = t.GetComponent<TMP_Text>();
            }
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.button == PointerEventData.InputButton.Right)
        {
            if (nodeData != null && nodeData.IsContainer())
            {
                graphEditor.SaveContainerAsTemplate(nodeData);
            }

            return;
        }

        if (eventData.clickCount == 2)
        {
            if (nodeData != null && nodeData.IsContainer())
            {
                graphEditor.EnterContainer(nodeData);
            }

            return;
        }

        if (graphEditor != null)
        {
            graphEditor.OnNodeClicked(this, eventData);
        }
    }

    private void RefreshTexts()
    {
        if (nodeData == null)
        {
            return;
        }

        if (titleText != null)
        {
            titleText.text = nodeData.title;
            titleText.fontSize = 20f;
            titleText.color = Color.black;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.enableWordWrapping = false;
            titleText.overflowMode = TextOverflowModes.Ellipsis;
        }

        if (categoryText != null)
        {
            categoryText.text = nodeData.category;
            categoryText.fontSize = 10f;
            categoryText.color = Color.black;
            categoryText.alignment = TextAlignmentOptions.Center;
            categoryText.enableWordWrapping = false;
            categoryText.overflowMode = TextOverflowModes.Ellipsis;
        }

        if (kindText != null)
        {
            kindText.text = nodeData.nodeKind;
            kindText.fontSize = 9f;
            kindText.color = Color.black;
            kindText.alignment = TextAlignmentOptions.Center;
            kindText.enableWordWrapping = false;
            kindText.overflowMode = TextOverflowModes.Ellipsis;
        }

        RefreshResultText();
    }
    
    private void RefreshResultText()
    {
        if (resultText == null || nodeData == null)
        {
            return;
        }

        bool isResultNode =
            nodeData.nodeKind == "ResultOutputNode" ||
            nodeData.nodeKind == "FinalResultNode";
        resultText.gameObject.SetActive(isResultNode);

        if (!isResultNode)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(nodeData.resultText))
        {
            resultText.text = "No result yet.\nClick Train to update.";
        }
        else
        {
            resultText.text = nodeData.resultText;
        }

        resultText.fontSize = 11f;
        resultText.color = Color.black;
        resultText.alignment = TextAlignmentOptions.TopLeft;
        resultText.enableWordWrapping = true;
        resultText.overflowMode = TextOverflowModes.Ellipsis;
    }

    private void BuildParameterRows()
    {
        if (paramsParent == null || paramRowPrefab == null)
        {
            return;
        }

        for (int i = paramsParent.childCount - 1; i >= 0; i--)
        {
            Destroy(paramsParent.GetChild(i).gameObject);
        }

        if (nodeData.parameters == null)
        {
            return;
        }

        foreach (NodeParam param in nodeData.parameters)
        {
            if (param.controlType == "hidden")
            {
                continue;
            }

            GameObject rowObject = Instantiate(paramRowPrefab, paramsParent);

            LayoutElement layout = rowObject.GetComponent<LayoutElement>();
            if (layout == null)
            {
                layout = rowObject.AddComponent<LayoutElement>();
            }

            layout.minHeight = paramRowHeight;
            layout.preferredHeight = paramRowHeight;
            layout.flexibleHeight = 0f;

            ParamRowView rowView = rowObject.GetComponent<ParamRowView>();
            if (rowView == null)
            {
                rowView = rowObject.GetComponentInChildren<ParamRowView>(true);
            }

            if (rowView == null)
            {
                rowView = rowObject.AddComponent<ParamRowView>();
            }

            rowView.Setup(param, OnParamValueChanged);
        }
    }

    private void BuildPorts()
    {
        ClearPortParent(inputPortsParent);
        ClearPortParent(outputPortsParent);

        if (portPrefab == null)
        {
            return;
        }

        if (nodeData.inputPorts != null)
        {
            foreach (NodePortData inputPort in nodeData.inputPorts)
            {
                CreatePort(inputPort, PortDirection.Input, inputPortsParent);
            }
        }

        if (nodeData.outputPorts != null)
        {
            foreach (NodePortData outputPort in nodeData.outputPorts)
            {
                CreatePort(outputPort, PortDirection.Output, outputPortsParent);
            }
        }
    }

    private void ClearPortParent(RectTransform parent)
    {
        if (parent == null)
        {
            return;
        }

        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Destroy(parent.GetChild(i).gameObject);
        }
    }

    private void CreatePort(
        NodePortData portData,
        PortDirection direction,
        RectTransform parent
    )
    {
        if (parent == null)
        {
            return;
        }

        GameObject portObject = Instantiate(portPrefab, parent);

        LayoutElement layout = portObject.GetComponent<LayoutElement>();
        if (layout == null)
        {
            layout = portObject.AddComponent<LayoutElement>();
        }

        layout.minWidth = 60f;
        layout.preferredWidth = 60f;
        layout.minHeight = 22f;
        layout.preferredHeight = 22f;
        layout.flexibleWidth = 0f;
        layout.flexibleHeight = 0f;

        PortView portView = portObject.GetComponent<PortView>();
        if (portView == null)
        {
            Debug.LogError("Port prefab does not have PortView component.");
            return;
        }

        portView.Setup(nodeData, portData, direction, graphEditor);

        if (graphEditor != null)
        {
            graphEditor.RegisterPortView(portView);
        }
    }

    private void ApplyFullLayout()
    {
        if (rectTransform == null || nodeData == null)
        {
            return;
        }

        int paramCount = GetVisibleParamCount();
        int inputCount = nodeData.inputPorts == null ? 0 : nodeData.inputPorts.Count;
        int outputCount = nodeData.outputPorts == null ? 0 : nodeData.outputPorts.Count;

        bool hasPorts = inputCount > 0 || outputCount > 0;

        float paramHeight = 0f;

        if (paramCount > 0)
        {
            paramHeight = paramCount * paramRowHeight + (paramCount - 1) * paramSpacing;
        }

        float totalHeight =
            headerHeight +
            paramHeight +
            (hasPorts ? portHeight : 0f) +
            bottomPadding;

        totalHeight = Mathf.Max(totalHeight, 120f);

        rectTransform.sizeDelta = new Vector2(nodeWidth, totalHeight);

        LayoutHeader();
        LayoutParams(paramHeight);
        LayoutPorts(hasPorts, paramHeight);

        LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);

        LayoutResultText();
    }

    private void LayoutResultText()
    {
        if (resultText == null || nodeData == null)
        {
            return;
        }

        if (nodeData.nodeKind != "ResultOutputNode" &&
            nodeData.nodeKind != "FinalResultNode")
        {
            resultText.gameObject.SetActive(false);
            return;
        }

        RectTransform r = resultText.GetComponent<RectTransform>();

        r.anchorMin = new Vector2(0f, 0f);
        r.anchorMax = new Vector2(1f, 0f);
        r.pivot = new Vector2(0.5f, 0f);

        r.anchoredPosition = new Vector2(0f, 40f);
        r.sizeDelta = new Vector2(-20f, 100f);

        resultText.gameObject.SetActive(true);

        // Make result node taller.
        if (rectTransform != null)
        {
            Vector2 size = rectTransform.sizeDelta;
            size.y = Mathf.Max(size.y, 230f);
            rectTransform.sizeDelta = size;
        }
    }

    private void LayoutHeader()
    {
        if (titleText != null)
        {
            RectTransform r = titleText.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0f, 1f);
            r.anchorMax = new Vector2(1f, 1f);
            r.pivot = new Vector2(0.5f, 1f);
            r.anchoredPosition = new Vector2(0f, -8f);
            r.sizeDelta = new Vector2(-16f, 26f);
        }

        if (categoryText != null)
        {
            RectTransform r = categoryText.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0f, 1f);
            r.anchorMax = new Vector2(1f, 1f);
            r.pivot = new Vector2(0.5f, 1f);
            r.anchoredPosition = new Vector2(0f, -38f);
            r.sizeDelta = new Vector2(-16f, 16f);
        }

        if (kindText != null)
        {
            RectTransform r = kindText.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0f, 1f);
            r.anchorMax = new Vector2(1f, 1f);
            r.pivot = new Vector2(0.5f, 1f);
            r.anchoredPosition = new Vector2(0f, -56f);
            r.sizeDelta = new Vector2(-16f, 14f);
        }
    }

    private void LayoutParams(float paramHeight)
    {
        if (paramsParent == null)
        {
            return;
        }

        paramsParent.anchorMin = new Vector2(0f, 1f);
        paramsParent.anchorMax = new Vector2(1f, 1f);
        paramsParent.pivot = new Vector2(0.5f, 1f);
        paramsParent.anchoredPosition = new Vector2(0f, -headerHeight);
        paramsParent.sizeDelta = new Vector2(-20f, paramHeight);

        VerticalLayoutGroup layout = paramsParent.GetComponent<VerticalLayoutGroup>();
        if (layout == null)
        {
            layout = paramsParent.gameObject.AddComponent<VerticalLayoutGroup>();
        }

        layout.padding = new RectOffset(0, 0, 0, 0);
        layout.spacing = paramSpacing;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        ContentSizeFitter fitter = paramsParent.GetComponent<ContentSizeFitter>();
        if (fitter != null)
        {
            Destroy(fitter);
        }
    }

    private void LayoutPorts(bool hasPorts, float paramHeight)
    {
        float y = -(headerHeight + paramHeight + 4f);

        if (inputPortsParent != null)
        {
            inputPortsParent.gameObject.SetActive(hasPorts && nodeData.inputPorts.Count > 0);

            inputPortsParent.anchorMin = new Vector2(0f, 1f);
            inputPortsParent.anchorMax = new Vector2(0.5f, 1f);
            inputPortsParent.pivot = new Vector2(0f, 1f);
            inputPortsParent.anchoredPosition = new Vector2(10f, y);
            inputPortsParent.sizeDelta = new Vector2(120f, portHeight);

            VerticalLayoutGroup layout = inputPortsParent.GetComponent<VerticalLayoutGroup>();
            if (layout == null)
            {
                layout = inputPortsParent.gameObject.AddComponent<VerticalLayoutGroup>();
            }

            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
        }

        if (outputPortsParent != null)
        {
            outputPortsParent.gameObject.SetActive(hasPorts && nodeData.outputPorts.Count > 0);

            outputPortsParent.anchorMin = new Vector2(0.5f, 1f);
            outputPortsParent.anchorMax = new Vector2(1f, 1f);
            outputPortsParent.pivot = new Vector2(1f, 1f);
            outputPortsParent.anchoredPosition = new Vector2(-10f, y);
            outputPortsParent.sizeDelta = new Vector2(120f, portHeight);

            VerticalLayoutGroup layout = outputPortsParent.GetComponent<VerticalLayoutGroup>();
            if (layout == null)
            {
                layout = outputPortsParent.gameObject.AddComponent<VerticalLayoutGroup>();
            }

            layout.childAlignment = TextAnchor.UpperRight;
            layout.childControlWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
        }
    }

    private void OnParamValueChanged(NodeParam param, string newValue)
    {
        if (nodeData == null || param == null)
        {
            return;
        }

        param.value = newValue;

        if (nodeData.nodeKind == "DatasetNode" && param.key == "dataset_name")
        {
            ApplyDatasetDefaultShape(newValue);
            BuildParameterRows();
            ApplyFullLayout();
        }

        if (nodeData.IsContainer() &&
            (param.key == "container_name" || param.key == "name"))
        {
            if (!string.IsNullOrWhiteSpace(newValue))
            {
                string newTitle = newValue.Trim();

                nodeData.title = newTitle;

                if (nodeData.innerGraph != null)
                {
                    nodeData.innerGraph.displayName = newTitle;
                }

                RefreshTexts();

                if (graphEditor != null)
                {
                    graphEditor.RefreshPathText();
                }
            }
        }
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (rectTransform == null || canvas == null || nodeData == null)
        {
            return;
        }

        rectTransform.anchoredPosition += eventData.delta / canvas.scaleFactor;
        nodeData.position = rectTransform.anchoredPosition;
    }

    public NodeData GetNodeData()
    {
        return nodeData;
    }

    public string GetNodeId()
    {
        if (nodeData == null)
        {
            return "";
        }

        return nodeData.nodeId;
    }

    public void SetSelected(bool selected)
    {
        if (backgroundImage == null)
        {
            backgroundImage = GetComponent<Image>();
        }

        if (backgroundImage == null)
        {
            return;
        }

        if (selected)
        {
            backgroundImage.color = new Color(0.72f, 0.82f, 1.0f, 1.0f);
        }
        else
        {
            backgroundImage.color = new Color(0.72f, 0.74f, 0.78f, 1.0f);
        }
    }

    private void ApplyDatasetDefaultShape(string datasetName)
    {
        if (nodeData == null || nodeData.parameters == null)
        {
            return;
        }

        string shape = "[1, 784]";

        if (datasetName == "MNIST")
        {
            shape = "[1, 784]";
        }
        else if (datasetName == "FashionMNIST")
        {
            shape = "[1, 784]";
        }
        else if (datasetName == "CIFAR10")
        {
            shape = "[1, 3072]";
        }
        else if (datasetName == "ChihuahuaMuffin")
        {
            shape = "[1, 3, 224, 224]";
        }
        else if (datasetName == "Titanic")
        {
            // 临时值。真实特征数以 /dataset_metadata/Titanic 返回为准。
            shape = "[1, 8]";
        }
        else if (datasetName == "WeatherPrediction")
        {
            // 临时值。真实特征数以 /dataset_metadata/WeatherPrediction 返回为准。
            shape = "[1, 10]";
        }

        foreach (NodeParam p in nodeData.parameters)
        {
            if (p.key == "input_shape")
            {
                p.value = shape;
                return;
            }
        }

        nodeData.parameters.Add(
            new NodeParam(
                "input_shape",
                shape,
                "list[int]",
                true
            )
        );
    }

    private int GetVisibleParamCount()
    {
        if (nodeData == null || nodeData.parameters == null)
        {
            return 0;
        }

        int count = 0;

        foreach (NodeParam param in nodeData.parameters)
        {
            if (param.controlType == "hidden")
            {
                continue;
            }

            count++;
        }

        return count;
    }
}