using System;
using System.Collections.Generic;

[Serializable]
public class NodeParam
{
    public string key;
    public string value;

    public string type;
    public bool required;

    public string controlType = "text";
    public bool advanced = false;

    public List<string> options = new List<string>();

    public NodeParam()
    {
    }

    public NodeParam(string key, string value)
    {
        this.key = key;
        this.value = value;
        this.type = "Any";
        this.required = false;
        this.controlType = "text";
        this.advanced = false;
        this.options = new List<string>();
    }

    public NodeParam(
        string key,
        string value,
        string type,
        bool required
    )
    {
        this.key = key;
        this.value = value;
        this.type = type;
        this.required = required;
        this.controlType = "text";
        this.advanced = false;
        this.options = new List<string>();
    }

    public NodeParam(
        string key,
        string value,
        string type,
        bool required,
        string controlType,
        List<string> options,
        bool advanced
    )
    {
        this.key = key;
        this.value = value;
        this.type = type;
        this.required = required;
        this.controlType = string.IsNullOrWhiteSpace(controlType) ? "text" : controlType;
        this.options = options == null ? new List<string>() : new List<string>(options);
        this.advanced = advanced;
    }
}