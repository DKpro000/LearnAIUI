using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class EdgeView : MonoBehaviour, IPointerClickHandler
{
    public RectTransform lineRect;
    public Image lineImage;

    private RectTransform fromPort;
    private RectTransform toPort;
    private RectTransform edgeParent;
    private Canvas canvas;

    private EdgeData edgeData;
    private GraphEditorController graphEditor;

    public string EdgeId
    {
        get
        {
            if (edgeData == null)
            {
                return "";
            }

            return edgeData.edgeId;
        }
    }

    public void Setup(
        EdgeData edgeData,
        RectTransform fromPort,
        RectTransform toPort,
        RectTransform edgeParent,
        GraphEditorController graphEditor
    )
    {
        this.edgeData = edgeData;
        this.fromPort = fromPort;
        this.toPort = toPort;
        this.edgeParent = edgeParent;
        this.graphEditor = graphEditor;

        if (lineRect == null)
        {
            lineRect = GetComponent<RectTransform>();
        }

        if (lineImage == null)
        {
            lineImage = GetComponent<Image>();
        }

        canvas = GetComponentInParent<Canvas>();

        if (lineImage != null)
        {
            lineImage.raycastTarget = true;
            lineImage.color = Color.black;
        }

        UpdatePosition();
    }

    private void Update()
    {
        UpdatePosition();
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (graphEditor != null)
        {
            graphEditor.OnEdgeClicked(this, eventData);
        }
    }

    public void SetSelected(bool selected)
    {
        if (lineImage == null)
        {
            lineImage = GetComponent<Image>();
        }

        if (lineImage == null)
        {
            return;
        }

        if (selected)
        {
            lineImage.color = new Color(1f, 0.45f, 0.1f, 1f);
        }
        else
        {
            lineImage.color = Color.black;
        }
    }

    private void UpdatePosition()
    {
        if (fromPort == null || toPort == null || edgeParent == null || lineRect == null)
        {
            return;
        }

        Camera cam = null;

        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
        {
            cam = canvas.worldCamera;
        }

        Vector2 startScreen = RectTransformUtility.WorldToScreenPoint(cam, fromPort.position);
        Vector2 endScreen = RectTransformUtility.WorldToScreenPoint(cam, toPort.position);

        Vector2 startLocal;
        Vector2 endLocal;

        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            edgeParent,
            startScreen,
            cam,
            out startLocal
        );

        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            edgeParent,
            endScreen,
            cam,
            out endLocal
        );

        Vector2 direction = endLocal - startLocal;
        float distance = direction.magnitude;

        lineRect.anchoredPosition = startLocal + direction * 0.5f;

        // Use a thicker hit area so edge is easier to click.
        lineRect.sizeDelta = new Vector2(distance, 8f);

        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        lineRect.localRotation = Quaternion.Euler(0f, 0f, angle);
    }
}