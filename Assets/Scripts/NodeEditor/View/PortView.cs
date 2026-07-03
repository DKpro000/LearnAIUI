using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class PortView : MonoBehaviour, IPointerClickHandler
{
    [Header("UI")]
    public TMP_Text labelText;
    public Image portImage;

    private NodeData nodeData;
    private NodePortData portData;
    private GraphEditorController graphEditor;

    public PortDirection Direction { get; private set; }

    public string NodeId
    {
        get { return nodeData.nodeId; }
    }

    public string PortName
    {
        get { return portData.name; }
    }

    public string PortType
    {
        get { return portData.portType; }
    }

    public RectTransform RectTransform
    {
        get { return GetComponent<RectTransform>(); }
    }

    public void Setup(
        NodeData nodeData,
        NodePortData portData,
        PortDirection direction,
        GraphEditorController graphEditor
    )
    {
        this.nodeData = nodeData;
        this.portData = portData;
        this.Direction = direction;
        this.graphEditor = graphEditor;

        if (labelText != null)
        {
            labelText.text = portData.name;
        }

        if (portImage != null)
        {
            portImage.raycastTarget = true;
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (graphEditor != null)
        {
            graphEditor.OnPortClicked(this);
        }
    }
}