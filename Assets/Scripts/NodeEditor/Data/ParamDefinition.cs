using System;
using System.Collections.Generic;

[Serializable]
public class ParamDefinition
{
    public string name;
    public string type;
    public bool required;
    public string defaultValue;

    public string controlType = "text";
    public bool advanced = false;

    public List<string> options = new List<string>();
}