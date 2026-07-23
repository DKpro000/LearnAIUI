using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class NodeData
{
    public string nodeId;

    public string definitionId;
    public string title;
    public string symbol;
    public string category;
    public string nodeKind;
    public string docUrl;

    public Vector2 position;

    public List<NodeParam> parameters = new List<NodeParam>();
    public List<NodePortData> inputPorts = new List<NodePortData>();
    public List<NodePortData> outputPorts = new List<NodePortData>();

    public GraphData innerGraph;

    public string resultText = "";

    public bool IsContainer()
    {
        return nodeKind == "ContainerNode";
    }
}