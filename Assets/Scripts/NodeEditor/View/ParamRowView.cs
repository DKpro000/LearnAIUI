using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ParamRowView : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text nameText;
    public TMP_InputField valueInput;
    public TMP_Dropdown valueDropdown;
    public Toggle valueToggle;

    private NodeParam param;
    private Action<NodeParam, string> onValueChanged;

    public void Setup(NodeParam param, Action<NodeParam, string> onValueChanged)
    {
        this.param = param;
        this.onValueChanged = onValueChanged;

        AutoBindReferences();
        ApplyLayout();
        RefreshUI();
    }

    private void AutoBindReferences()
    {
        if (nameText == null)
        {
            Transform t = transform.Find("NameText");
            if (t != null)
            {
                nameText = t.GetComponent<TMP_Text>();
            }
        }

        if (valueInput == null)
        {
            Transform t = transform.Find("ValueInput");
            if (t != null)
            {
                valueInput = t.GetComponent<TMP_InputField>();
            }
        }

        if (valueDropdown == null)
        {
            Transform t = transform.Find("ValueDropdown");
            if (t != null)
            {
                valueDropdown = t.GetComponent<TMP_Dropdown>();
            }
        }

        if (valueToggle == null)
        {
            Transform t = transform.Find("ValueToggle");
            if (t != null)
            {
                valueToggle = t.GetComponent<Toggle>();
            }
        }

        if (valueInput == null)
        {
            valueInput = GetComponentInChildren<TMP_InputField>(true);
        }

        if (valueDropdown == null)
        {
            valueDropdown = GetComponentInChildren<TMP_Dropdown>(true);
        }

        if (valueToggle == null)
        {
            valueToggle = GetComponentInChildren<Toggle>(true);
        }
    }

    private void ApplyLayout()
    {
        RectTransform rowRect = GetComponent<RectTransform>();

        if (rowRect != null)
        {
            rowRect.anchorMin = new Vector2(0f, 1f);
            rowRect.anchorMax = new Vector2(1f, 1f);
            rowRect.pivot = new Vector2(0.5f, 1f);
            rowRect.sizeDelta = new Vector2(0f, 30f);
        }

        LayoutElement layout = GetComponent<LayoutElement>();
        if (layout == null)
        {
            layout = gameObject.AddComponent<LayoutElement>();
        }

        layout.minHeight = 30f;
        layout.preferredHeight = 30f;
        layout.flexibleHeight = 0f;

        if (nameText != null)
        {
            RectTransform nameRect = nameText.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0f, 0f);
            nameRect.anchorMax = new Vector2(0.48f, 1f);
            nameRect.offsetMin = new Vector2(4f, 0f);
            nameRect.offsetMax = new Vector2(-4f, 0f);

            nameText.fontSize = 10f;
            nameText.color = Color.black;
            nameText.alignment = TextAlignmentOptions.Left;
            nameText.enableWordWrapping = false;
            nameText.overflowMode = TextOverflowModes.Ellipsis;
        }

        SetupValueRect(valueInput == null ? null : valueInput.GetComponent<RectTransform>());
        SetupValueRect(valueDropdown == null ? null : valueDropdown.GetComponent<RectTransform>());
        SetupValueRect(valueToggle == null ? null : valueToggle.GetComponent<RectTransform>());
    }

    private void SetupValueRect(RectTransform rect)
    {
        if (rect == null)
        {
            return;
        }

        rect.anchorMin = new Vector2(0.50f, 0f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.offsetMin = new Vector2(4f, 2f);
        rect.offsetMax = new Vector2(-4f, -2f);
    }

    private void RefreshUI()
    {
        if (param == null)
        {
            return;
        }

        if (nameText != null)
        {
            string label = param.key;

            if (param.required)
            {
                label += " *";
            }

            nameText.text = label;
        }

        string controlType = string.IsNullOrWhiteSpace(param.controlType)
            ? "text"
            : param.controlType;

        bool useDropdown = controlType == "dropdown";
        bool useCheckbox = controlType == "checkbox";
        bool useInput = !useDropdown && !useCheckbox;

        if (valueInput != null)
        {
            valueInput.gameObject.SetActive(useInput);
        }

        if (valueDropdown != null)
        {
            valueDropdown.gameObject.SetActive(useDropdown);
        }

        if (valueToggle != null)
        {
            valueToggle.gameObject.SetActive(useCheckbox);
        }

        if (useDropdown)
        {
            SetupDropdown();
        }
        else if (useCheckbox)
        {
            SetupToggle();
        }
        else
        {
            SetupInputField(controlType);
        }
    }

    private void SetupInputField(string controlType)
    {
        if (valueInput == null)
        {
            return;
        }

        valueInput.onEndEdit.RemoveAllListeners();
        valueInput.text = param.value;
        valueInput.onEndEdit.AddListener(OnInputEndEdit);

        if (controlType == "int")
        {
            valueInput.contentType = TMP_InputField.ContentType.IntegerNumber;
        }
        else if (controlType == "float")
        {
            valueInput.contentType = TMP_InputField.ContentType.DecimalNumber;
        }
        else
        {
            valueInput.contentType = TMP_InputField.ContentType.Standard;
        }

        TMP_Text inputText = valueInput.textComponent;
        if (inputText != null)
        {
            inputText.fontSize = 10f;
            inputText.color = Color.black;
            inputText.enableWordWrapping = false;
        }

        TMP_Text placeholder = valueInput.placeholder as TMP_Text;
        if (placeholder != null)
        {
            placeholder.text = controlType;
            placeholder.fontSize = 10f;
        }
    }

    private void SetupDropdown()
    {
        if (valueDropdown == null)
        {
            return;
        }

        valueDropdown.onValueChanged.RemoveAllListeners();
        valueDropdown.ClearOptions();

        List<string> options = param.options == null
            ? new List<string>()
            : param.options;

        if (options.Count == 0)
        {
            options.Add(param.value);
        }

        valueDropdown.AddOptions(options);

        int index = options.IndexOf(param.value);

        if (index < 0)
        {
            index = 0;
            param.value = options[0];
        }

        valueDropdown.value = index;
        valueDropdown.RefreshShownValue();
        valueDropdown.onValueChanged.AddListener(OnDropdownChanged);
    }

    private void SetupToggle()
    {
        if (valueToggle == null)
        {
            return;
        }

        valueToggle.onValueChanged.RemoveAllListeners();

        bool boolValue =
            param.value == "True" ||
            param.value == "true" ||
            param.value == "1";

        valueToggle.isOn = boolValue;
        valueToggle.onValueChanged.AddListener(OnToggleChanged);
    }

    private void OnInputEndEdit(string newValue)
    {
        if (param == null)
        {
            return;
        }

        param.value = newValue;
        onValueChanged?.Invoke(param, newValue);
    }

    private void OnDropdownChanged(int index)
    {
        if (param == null || param.options == null)
        {
            return;
        }

        if (index < 0 || index >= param.options.Count)
        {
            return;
        }

        string newValue = param.options[index];
        param.value = newValue;
        onValueChanged?.Invoke(param, newValue);
    }

    private void OnToggleChanged(bool value)
    {
        if (param == null)
        {
            return;
        }

        param.value = value ? "True" : "False";
        onValueChanged?.Invoke(param, param.value);
    }
}