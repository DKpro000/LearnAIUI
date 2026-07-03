using System;
using System.Collections.Generic;

[Serializable]
public class NodeLibraryResponse
{
    public bool success;
    public int count;
    public List<NodeDefinition> library = new List<NodeDefinition>();
}