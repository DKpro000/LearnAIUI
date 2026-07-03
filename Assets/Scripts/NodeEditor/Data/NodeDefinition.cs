using System;
using System.Collections.Generic;

[Serializable]
public class NodeDefinition
{
    public string id;
    public string displayName;
    public string symbol;
    public string category;
    public string nodeKind;

    public bool allowedInModelGraph;
    public bool allowedInSequential;
    public bool allowedInLossGraph;
    public bool allowedInTrainingGraph;

    public List<ParamDefinition> initParams = new List<ParamDefinition>();
    public List<PortDefinition> inputPorts = new List<PortDefinition>();
    public List<PortDefinition> outputPorts = new List<PortDefinition>();
}