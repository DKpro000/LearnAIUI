using System;
using System.Collections.Generic;

[Serializable]
public class GraphData
{
    public string graphId;
    public string displayName;

    public List<NodeData> nodes = new List<NodeData>();
    public List<EdgeData> edges = new List<EdgeData>();

    public GraphData()
    {
    }

    public GraphData(string displayName)
    {
        graphId = Guid.NewGuid().ToString();
        this.displayName = displayName;
    }
}