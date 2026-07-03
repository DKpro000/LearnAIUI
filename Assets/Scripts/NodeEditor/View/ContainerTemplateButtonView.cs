using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class ContainerTemplateButtonView : MonoBehaviour, IPointerClickHandler
{
    private ContainerTemplateData template;
    private GraphEditorController graphEditor;
    private ContainerTemplateLibrary templateLibrary;
    private NodeLibraryMenuController menuController;

    public void Setup(
        ContainerTemplateData template,
        GraphEditorController graphEditor,
        ContainerTemplateLibrary templateLibrary,
        NodeLibraryMenuController menuController
    )
    {
        this.template = template;
        this.graphEditor = graphEditor;
        this.templateLibrary = templateLibrary;
        this.menuController = menuController;

        TMP_Text text = GetComponentInChildren<TMP_Text>();
        if (text != null)
        {
            text.text = template.displayName;
        }

        Button button = GetComponent<Button>();
        if (button != null)
        {
            button.onClick.RemoveAllListeners();
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (template == null)
        {
            return;
        }

        if (eventData.button == PointerEventData.InputButton.Left)
        {
            if (graphEditor != null)
            {
                graphEditor.AddContainerTemplateToGraph(template);
            }
        }
        else if (eventData.button == PointerEventData.InputButton.Right)
        {
            if (templateLibrary != null)
            {
                templateLibrary.DeleteTemplate(template.templateId);
            }

            if (menuController != null)
            {
                menuController.RefreshMenu();
            }
        }
    }
}