using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class ContainerTemplateLibrary : MonoBehaviour
{
    private ContainerTemplateDatabase database = new ContainerTemplateDatabase();

    private string SavePath
    {
        get
        {
            return Path.Combine(
                Application.persistentDataPath,
                "nn_container_templates.json"
            );
        }
    }

    private void Awake()
    {
        Load();
    }

    public List<ContainerTemplateData> GetTemplates()
    {
        return database.templates;
    }

    public void SaveContainerTemplate(NodeData containerNode)
    {
        if (containerNode == null)
        {
            Debug.LogError("Cannot save null container.");
            return;
        }

        if (!containerNode.IsContainer())
        {
            Debug.LogWarning("Only ContainerNode can be saved as template.");
            return;
        }

        if (containerNode.innerGraph == null)
        {
            Debug.LogWarning("Container has no innerGraph.");
            return;
        }

        ContainerTemplateData template = new ContainerTemplateData();
        template.templateId = Guid.NewGuid().ToString();
        template.displayName = containerNode.title;
        template.containerNode = DeepCloneNode(containerNode);

        database.templates.Add(template);

        Save();

        Debug.Log("Saved container template: " + template.displayName);
        Debug.Log("Template save path: " + SavePath);
    }

    public void DeleteTemplate(string templateId)
    {
        if (string.IsNullOrEmpty(templateId))
        {
            return;
        }

        database.templates.RemoveAll(t => t.templateId == templateId);

        Save();

        Debug.Log("Deleted container template: " + templateId);
    }

    public NodeData CreateNodeFromTemplate(ContainerTemplateData template)
    {
        if (template == null || template.containerNode == null)
        {
            return null;
        }

        NodeData cloned = DeepCloneNode(template.containerNode);

        RefreshNodeIdsRecursive(cloned);

        return cloned;
    }

    private NodeData DeepCloneNode(NodeData node)
    {
        string json = JsonUtility.ToJson(node);
        return JsonUtility.FromJson<NodeData>(json);
    }

    private void RefreshNodeIdsRecursive(NodeData rootNode)
    {
        if (rootNode == null)
        {
            return;
        }

        rootNode.nodeId = Guid.NewGuid().ToString();

        if (rootNode.innerGraph != null)
        {
            RefreshGraphIds(rootNode.innerGraph);
        }
    }

    private void RefreshGraphIds(GraphData graph)
    {
        if (graph == null)
        {
            return;
        }

        graph.graphId = Guid.NewGuid().ToString();

        Dictionary<string, string> idMap = new Dictionary<string, string>();

        foreach (NodeData node in graph.nodes)
        {
            string oldId = node.nodeId;
            string newId = Guid.NewGuid().ToString();

            idMap[oldId] = newId;
            node.nodeId = newId;
        }

        foreach (EdgeData edge in graph.edges)
        {
            if (idMap.ContainsKey(edge.fromNodeId))
            {
                edge.fromNodeId = idMap[edge.fromNodeId];
            }

            if (idMap.ContainsKey(edge.toNodeId))
            {
                edge.toNodeId = idMap[edge.toNodeId];
            }

            edge.edgeId = Guid.NewGuid().ToString();
        }

        foreach (NodeData node in graph.nodes)
        {
            if (node.innerGraph != null)
            {
                RefreshGraphIds(node.innerGraph);
            }
        }
    }

    public void Load()
    {
        if (!File.Exists(SavePath))
        {
            database = new ContainerTemplateDatabase();
            return;
        }

        try
        {
            string json = File.ReadAllText(SavePath);

            if (string.IsNullOrWhiteSpace(json))
            {
                database = new ContainerTemplateDatabase();
                return;
            }

            database = JsonUtility.FromJson<ContainerTemplateDatabase>(json);

            if (database == null)
            {
                database = new ContainerTemplateDatabase();
            }

            if (database.templates == null)
            {
                database.templates = new List<ContainerTemplateData>();
            }

            Debug.Log("Loaded container templates: " + database.templates.Count);
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to load container templates: " + e.Message);
            database = new ContainerTemplateDatabase();
        }
    }

    public void Save()
    {
        try
        {
            string json = JsonUtility.ToJson(database, true);
            File.WriteAllText(SavePath, json);
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to save container templates: " + e.Message);
        }
    }
}