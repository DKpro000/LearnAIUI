using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class SidebarResizeHandle : MonoBehaviour, IDragHandler, IPointerDownHandler, IPointerEnterHandler, IPointerExitHandler
{
    [Header("The panel this handle resizes")]
    public RectTransform sidebarPanel;

    [Header("Other panels whose left edge should follow the sidebar's width")]
    public RectTransform[] panelsToReposition;

    [Header("Limits")]
    public float minWidth = 120f;
    public float maxWidth = 400f;

    [Header("Hover feedback")]
    public Image handleImage;
    public Color normalColor = new Color(0.55f, 0.4f, 0.25f, 1f);
    public Color hoverColor = new Color(0.85f, 0.65f, 0.35f, 1f);

    private Vector2 lastPointerPosition;

    private void Start()
    {
        if (handleImage != null)
        {
            handleImage.color = normalColor;
        }
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (handleImage != null)
        {
            handleImage.color = hoverColor;
        }
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (handleImage != null)
        {
            handleImage.color = normalColor;
        }
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        lastPointerPosition = eventData.position;
    }

    public void OnDrag(PointerEventData eventData)
    {
        float deltaX = eventData.position.x - lastPointerPosition.x;
        lastPointerPosition = eventData.position;

        float currentWidth = sidebarPanel.rect.width;
        float newWidth = Mathf.Clamp(currentWidth + deltaX, minWidth, maxWidth);
        float actualDelta = newWidth - currentWidth;

        sidebarPanel.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, newWidth);

        foreach (RectTransform panel in panelsToReposition)
        {
            if (panel != null)
            {
                Vector2 pos = panel.anchoredPosition;
                pos.x += actualDelta;
                panel.anchoredPosition = pos;
            }
        }
    }
}