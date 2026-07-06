using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class EvaluateSettingsPopup : MonoBehaviour
{
    public TMP_Dropdown checkpointDropdown;
    public TMP_Text checkpointInfoText;

    private GraphEditorController graphEditor;
    private GraphBackendClient backendClient;
    private GraphTrainSettings settings;

    private List<CheckpointMetadata> checkpoints = new List<CheckpointMetadata>();

    public void Setup(
        GraphEditorController graphEditor,
        GraphBackendClient backendClient
    )
    {
        this.graphEditor = graphEditor;
        this.backendClient = backendClient;
    }

    public void Show(GraphTrainSettings currentSettings, string datasetName)
    {
        gameObject.SetActive(true);

        settings = currentSettings == null
            ? new GraphTrainSettings()
            : currentSettings;

        checkpoints.Clear();

        if (checkpointDropdown != null)
        {
            checkpointDropdown.ClearOptions();
            checkpointDropdown.AddOptions(new List<string> { "Loading..." });
        }

        if (backendClient != null)
        {
            StartCoroutine(
                backendClient.LoadCheckpoints(
                    datasetName,
                    OnCheckpointsLoaded
                )
            );
        }
    }

    private void OnCheckpointsLoaded(List<CheckpointMetadata> loaded)
    {
        checkpoints = loaded ?? new List<CheckpointMetadata>();

        if (checkpointDropdown == null)
        {
            return;
        }

        checkpointDropdown.onValueChanged.RemoveAllListeners();
        checkpointDropdown.ClearOptions();

        List<string> options = new List<string>();

        if (checkpoints.Count == 0)
        {
            options.Add("No saved weights");
            checkpointDropdown.AddOptions(options);
            UpdateInfoText();
            return;
        }

        foreach (CheckpointMetadata item in checkpoints)
        {
            string label =
                item.modelName + " / " +
                item.weightName + " / " +
                item.datasetName + " / " +
                item.savedAt;

            options.Add(label);
        }

        checkpointDropdown.AddOptions(options);
        checkpointDropdown.value = 0;
        checkpointDropdown.RefreshShownValue();

        checkpointDropdown.onValueChanged.AddListener(OnDropdownChanged);

        OnDropdownChanged(0);
    }

    private void OnDropdownChanged(int index)
    {
        if (index < 0 || index >= checkpoints.Count)
        {
            return;
        }

        CheckpointMetadata selected = checkpoints[index];

        settings.checkpointId = selected.checkpointId;

        if (backendClient != null)
        {
            backendClient.SetSelectedCheckpointId(selected.checkpointId);
        }

        UpdateInfoText();
    }

    private void UpdateInfoText()
    {
        if (checkpointInfoText == null)
        {
            return;
        }

        if (checkpoints.Count == 0 ||
            checkpointDropdown == null ||
            checkpointDropdown.value < 0 ||
            checkpointDropdown.value >= checkpoints.Count)
        {
            checkpointInfoText.text = "No saved weights found for this dataset.";
            return;
        }

        CheckpointMetadata selected = checkpoints[checkpointDropdown.value];

        checkpointInfoText.text =
            "Model: " + selected.modelName + "\n" +
            "Weight: " + selected.weightName + "\n" +
            "Dataset: " + selected.datasetName + "\n" +
            "Saved At: " + selected.savedAt + "\n" +
            "Classes: " + selected.numClasses;
    }

    public void OnEvaluateClicked()
    {
        if (checkpoints.Count == 0)
        {
            return;
        }

        if (graphEditor != null)
        {
            graphEditor.StartFinalEvaluateWithSettings(settings);
        }

        gameObject.SetActive(false);
    }

    public void OnCancelClicked()
    {
        gameObject.SetActive(false);
    }
}