using System;

[Serializable]
public class NodePortData
{
    public string name;
    public string portType;

    public NodePortData()
    {
    }

    public NodePortData(string name, string portType)
    {
        this.name = name;
        this.portType = portType;
    }
}