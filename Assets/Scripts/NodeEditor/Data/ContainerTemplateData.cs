using System;
using System.Collections.Generic;

[Serializable]
public class ContainerTemplateData
{
    public string templateId;
    public string displayName;
    public NodeData containerNode;
}

[Serializable]
public class ContainerTemplateDatabase
{
    public List<ContainerTemplateData> templates = new List<ContainerTemplateData>();
}