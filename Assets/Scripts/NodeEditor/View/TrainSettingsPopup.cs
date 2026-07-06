using TMPro;
using UnityEngine;

public class TrainSettingsPopup : MonoBehaviour
{
    [Header("Inputs")]
    public TMP_InputField epochsInput;
    public TMP_InputField batchSizeInput;
    public TMP_InputField learningRateInput;
    public TMP_InputField maxTrainSamplesInput;
    public TMP_InputField modelNameInput;
    public TMP_InputField weightNameInput;

    [Header("Dropdowns")]
    public TMP_Dropdown optimizerDropdown;
    public TMP_Dropdown lossDropdown;

    [Header("Text")]
    public TMP_Text datasetText;

    private GraphEditorController graphEditor;
    private GraphTrainSettings currentSettings;
    private string currentDataset;

    public void Setup(GraphEditorController editor)
    {
        graphEditor = editor;
    }

    public void Show(GraphTrainSettings settings, string dataset)
    {
        currentSettings = settings == null ? new GraphTrainSettings() : settings;
        currentDataset = string.IsNullOrWhiteSpace(dataset) ? "MNIST" : dataset;

        gameObject.SetActive(true);

        if (datasetText != null)
        {
            datasetText.text = "Dataset: " + currentDataset;
        }

        if (epochsInput != null)
        {
            epochsInput.text = currentSettings.epochs.ToString();
        }

        if (batchSizeInput != null)
        {
            batchSizeInput.text = currentSettings.batchSize.ToString();
        }

        if (learningRateInput != null)
        {
            learningRateInput.text = currentSettings.learningRate.ToString("0.#######");
        }

        if (maxTrainSamplesInput != null)
        {
            maxTrainSamplesInput.text = currentSettings.maxTrainSamples.ToString();
        }

        if (modelNameInput != null)
        {
            modelNameInput.text = string.IsNullOrWhiteSpace(currentSettings.modelName)
                ? "UnnamedModel"
                : currentSettings.modelName;
        }

        if (weightNameInput != null)
        {
            weightNameInput.text = currentSettings.weightName;
        }

        SetupOptimizerDropdown();
        SetupLossDropdown();
    }

    private void SetupOptimizerDropdown()
    {
        if (optimizerDropdown == null)
        {
            return;
        }

        optimizerDropdown.ClearOptions();
        optimizerDropdown.AddOptions(new System.Collections.Generic.List<string>
        {
            "Adam",
            "SGD"
        });

        int index = optimizerDropdown.options.FindIndex(
            option => option.text == currentSettings.optimizer
        );

        optimizerDropdown.value = index < 0 ? 0 : index;
        optimizerDropdown.RefreshShownValue();
    }

    private void SetupLossDropdown()
    {
        if (lossDropdown == null)
        {
            return;
        }

        lossDropdown.ClearOptions();
        lossDropdown.AddOptions(new System.Collections.Generic.List<string>
        {
            "CrossEntropyLoss"
        });

        int index = lossDropdown.options.FindIndex(
            option => option.text == currentSettings.loss
        );

        lossDropdown.value = index < 0 ? 0 : index;
        lossDropdown.RefreshShownValue();
    }

    public void OnStartTrainingClicked()
    {
        GraphTrainSettings settings = new GraphTrainSettings();

        settings.dataset = currentDataset;
        settings.epochs = ParseInt(epochsInput, 1);
        settings.batchSize = ParseInt(batchSizeInput, 64);
        settings.learningRate = ParseFloat(learningRateInput, 0.001f);
        settings.maxTrainSamples = ParseInt(maxTrainSamplesInput, 2000);

        settings.optimizer = GetDropdownValue(optimizerDropdown, "Adam");
        settings.loss = GetDropdownValue(lossDropdown, "CrossEntropyLoss");
        settings.device = "auto";

        gameObject.SetActive(false);

        if (modelNameInput != null)
        {
            settings.modelName = modelNameInput.text;
        }

        if (weightNameInput != null)
        {
            settings.weightName = weightNameInput.text;
        }

        if (graphEditor != null)
        {
            graphEditor.StartTrainingWithSettings(settings);
        }
    }

    public void OnCancelClicked()
    {
        gameObject.SetActive(false);
    }

    private int ParseInt(TMP_InputField input, int defaultValue)
    {
        if (input == null)
        {
            return defaultValue;
        }

        int value;

        if (int.TryParse(input.text, out value))
        {
            return value;
        }

        return defaultValue;
    }

    private float ParseFloat(TMP_InputField input, float defaultValue)
    {
        if (input == null)
        {
            return defaultValue;
        }

        float value;

        if (float.TryParse(input.text, out value))
        {
            return value;
        }

        return defaultValue;
    }

    private string GetDropdownValue(TMP_Dropdown dropdown, string defaultValue)
    {
        if (dropdown == null || dropdown.options.Count == 0)
        {
            return defaultValue;
        }

        return dropdown.options[dropdown.value].text;
    }
}