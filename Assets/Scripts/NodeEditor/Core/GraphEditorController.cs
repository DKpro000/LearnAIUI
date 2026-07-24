using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

public class GraphEditorController : MonoBehaviour
{
    [Header("Graph")]
    public GraphData rootGraph;
    public GraphData currentGraph;

    private readonly List<GraphData> graphPath = new List<GraphData>();
    private readonly List<NodeData> containerNodePath = new List<NodeData>();

    [Header("UI")]
    public RectTransform nodeParent;
    public RectTransform edgeParent;
    public GameObject nodePrefab;
    public GameObject edgeLinePrefab;
    public TMP_Text pathText;

    [Header("Backend")]
    public GraphBackendClient graphBackendClient;

    [Header("Training UI")]
    public TrainSettingsPopup trainSettingsPopup;
    public GraphTrainSettings defaultTrainSettings = new GraphTrainSettings();

    [Header("Container Templates")]
    public ContainerTemplateLibrary containerTemplateLibrary;
    public NodeLibraryMenuController nodeLibraryMenuController;

    [Header("Evaluate Popup")]
    public EvaluateSettingsPopup evaluateSettingsPopup;

    private int spawnIndex = 0;

    private readonly Dictionary<string, PortView> portViewLookup =
        new Dictionary<string, PortView>();
    private readonly HashSet<string> selectedNodeIds = new HashSet<string>();
    private readonly Dictionary<string, NodeView> nodeViewLookup = new Dictionary<string, NodeView>();
    private readonly HashSet<string> selectedEdgeIds = new HashSet<string>();
    private readonly Dictionary<string, EdgeView> edgeViewLookup =
        new Dictionary<string, EdgeView>();

    private PortView pendingOutputPort;

    private void Start()
    {
        rootGraph = new GraphData("Root Graph");
        currentGraph = rootGraph;

        graphPath.Clear();
        graphPath.Add(rootGraph);

        RenderGraph();
        RefreshPathText();

        Debug.Log("testing");
    }

    public void AddNodeFromDefinition(NodeDefinition definition)
    {
        if (definition == null)
        {
            Debug.LogError("Cannot add null node definition.");
            return;
        }

        NodeData node = CreateNodeData(definition);
        currentGraph.nodes.Add(node);

        RenderGraph();
    }

    private NodeData CreateNodeData(NodeDefinition definition)
    {
        NodeData node = new NodeData();

        node.nodeId = Guid.NewGuid().ToString();

        node.definitionId = definition.id;
        node.title = definition.displayName;
        node.symbol = definition.symbol;
        node.category = definition.category;
        node.nodeKind = definition.nodeKind;

        node.position = GetSpawnPosition();

        node.parameters = new List<NodeParam>();
        node.inputPorts = new List<NodePortData>();
        node.outputPorts = new List<NodePortData>();

        if (definition.initParams != null)
        {
            foreach (ParamDefinition param in definition.initParams)
            {
                node.parameters.Add(
                    new NodeParam(
                        param.name,
                        param.defaultValue,
                        param.type,
                        param.required,
                        param.controlType,
                        param.options,
                        param.advanced
                    )
                );
            }
        }

        if (definition.inputPorts != null)
        {
            foreach (PortDefinition port in definition.inputPorts)
            {
                node.inputPorts.Add(new NodePortData(port.name, port.portType));
            }
        }

        if (definition.outputPorts != null)
        {
            foreach (PortDefinition port in definition.outputPorts)
            {
                node.outputPorts.Add(new NodePortData(port.name, port.portType));
            }
        }

        if (node.IsContainer())
        {
            string graphName = GetContainerDisplayName(node);
            node.title = graphName;
            node.innerGraph = new GraphData(graphName);
        }

        return node;
    }

    private string GetContainerDisplayName(NodeData node)
    {
        string graphName = node.title;

        if (node.parameters == null)
        {
            return graphName;
        }

        foreach (NodeParam param in node.parameters)
        {
            if (param.key == "container_name" || param.key == "name")
            {
                if (!string.IsNullOrWhiteSpace(param.value))
                {
                    graphName = param.value.Trim();
                }
            }
        }

        return graphName;
    }

    private Vector2 GetSpawnPosition()
    {
        float x = -250 + (spawnIndex % 3) * 280;
        float y = 160 - (spawnIndex / 3) * 170;

        spawnIndex++;

        return new Vector2(x, y);
    }

    public void EnterContainer(NodeData containerNode)
    {
        if (containerNode == null)
        {
            return;
        }

        if (!containerNode.IsContainer())
        {
            Debug.Log("This node is not a container: " + containerNode.title);
            return;
        }

        if (containerNode.innerGraph == null)
        {
            string graphName = GetContainerDisplayName(containerNode);
            containerNode.innerGraph = new GraphData(graphName);
        }

        pendingOutputPort = null;

        currentGraph = containerNode.innerGraph;
        graphPath.Add(currentGraph);
        containerNodePath.Add(containerNode);

        spawnIndex = 0;

        RenderGraph();
        RefreshPathText();

        Debug.Log("Entered container: " + currentGraph.displayName);
    }

    public void BackToParentGraph()
    {
        if (graphPath.Count <= 1)
        {
            Debug.Log("Already at root graph.");
            return;
        }

        pendingOutputPort = null;
        if (containerNodePath.Count > 0)
        {
            NodeData leavingContainer =
                containerNodePath[containerNodePath.Count - 1];

            SyncContainerPortsFromInnerGraph(leavingContainer);

            containerNodePath.RemoveAt(containerNodePath.Count - 1);
        }

        graphPath.RemoveAt(graphPath.Count - 1);
        currentGraph = graphPath[graphPath.Count - 1];

        spawnIndex = 0;

        RenderGraph();
        RefreshPathText();

        Debug.Log("Back to graph: " + currentGraph.displayName);
    }

    private void SyncContainerPortsFromInnerGraph(NodeData containerNode)
    {
        if (containerNode == null)
        {
            return;
        }

        if (!containerNode.IsContainer())
        {
            return;
        }

        if (containerNode.innerGraph == null)
        {
            return;
        }

        containerNode.inputPorts.Clear();
        containerNode.outputPorts.Clear();

        HashSet<string> usedInputNames = new HashSet<string>();
        HashSet<string> usedOutputNames = new HashSet<string>();

        foreach (NodeData innerNode in containerNode.innerGraph.nodes)
        {
            if (innerNode.nodeKind == "InputNode")
            {
                string inputName = GetNodeParamValue(innerNode, "input_name");

                if (string.IsNullOrWhiteSpace(inputName))
                {
                    inputName = "x";
                }

                inputName = MakeUniquePortName(inputName.Trim(), usedInputNames);
                usedInputNames.Add(inputName);

                containerNode.inputPorts.Add(
                    new NodePortData(inputName, "Tensor")
                );
            }
            else if (innerNode.nodeKind == "OutputNode")
            {
                string outputName = GetNodeParamValue(innerNode, "output_name");

                if (string.IsNullOrWhiteSpace(outputName))
                {
                    outputName = "out";
                }

                outputName = MakeUniquePortName(outputName.Trim(), usedOutputNames);
                usedOutputNames.Add(outputName);

                containerNode.outputPorts.Add(
                    new NodePortData(outputName, "Tensor")
                );
            }
        }

        Debug.Log(
            "Synced container ports: " +
            containerNode.title +
            " inputs=" +
            containerNode.inputPorts.Count +
            " outputs=" +
            containerNode.outputPorts.Count
        );
    }

    private string GetNodeParamValue(NodeData node, string key)
    {
        if (node == null || node.parameters == null)
        {
            return "";
        }

        foreach (NodeParam param in node.parameters)
        {
            if (param.key == key)
            {
                return param.value;
            }
        }

        return "";
    }

    private string MakeUniquePortName(string baseName, HashSet<string> usedNames)
    {
        if (!usedNames.Contains(baseName))
        {
            return baseName;
        }

        int index = 2;

        while (usedNames.Contains(baseName + "_" + index))
        {
            index++;
        }

        return baseName + "_" + index;
    }

    public void RefreshPathText()
    {
        if (pathText == null)
        {
            return;
        }

        List<string> names = new List<string>();

        foreach (GraphData graph in graphPath)
        {
            names.Add(graph.displayName);
        }

        pathText.text = string.Join(" / ", names);
    }

    public void RegisterPortView(PortView portView)
    {
        if (portView == null)
        {
            return;
        }

        string key = MakePortKey(
            portView.NodeId,
            portView.PortName,
            portView.Direction
        );

        portViewLookup[key] = portView;
    }

    private string MakePortKey(string nodeId, string portName, PortDirection direction)
    {
        return direction + ":" + nodeId + ":" + portName;
    }

    public void OnPortClicked(PortView portView)
    {
        if (portView == null)
        {
            return;
        }

        if (portView.Direction == PortDirection.Output)
        {
            pendingOutputPort = portView;
            Debug.Log("Selected output port: " + portView.NodeId + "." + portView.PortName);
            return;
        }

        if (portView.Direction == PortDirection.Input)
        {
            if (pendingOutputPort == null)
            {
                Debug.Log("Please click an output port first.");
                return;
            }

            CreateEdge(pendingOutputPort, portView);
            pendingOutputPort = null;
        }
    }

    private void CreateEdge(PortView fromPort, PortView toPort)
    {
        if (fromPort == null || toPort == null)
        {
            return;
        }

        if (fromPort.NodeId == toPort.NodeId)
        {
            Debug.LogWarning("Cannot connect a node to itself.");
            return;
        }

        if (fromPort.Direction != PortDirection.Output || toPort.Direction != PortDirection.Input)
        {
            Debug.LogWarning("Edge must be from output to input.");
            return;
        }

        if (HasDuplicateEdge(fromPort, toPort))
        {
            Debug.LogWarning("This edge already exists.");
            return;
        }

        if (InputPortAlreadyConnected(toPort))
        {
            Debug.LogWarning("This input port is already connected.");
            return;
        }

        EdgeData edge = new EdgeData();
        edge.edgeId = Guid.NewGuid().ToString();

        edge.fromNodeId = fromPort.NodeId;
        edge.fromPortName = fromPort.PortName;

        edge.toNodeId = toPort.NodeId;
        edge.toPortName = toPort.PortName;

        currentGraph.edges.Add(edge);

        RenderGraph();
    }

    private bool HasDuplicateEdge(PortView fromPort, PortView toPort)
    {
        foreach (EdgeData edge in currentGraph.edges)
        {
            bool same =
                edge.fromNodeId == fromPort.NodeId &&
                edge.fromPortName == fromPort.PortName &&
                edge.toNodeId == toPort.NodeId &&
                edge.toPortName == toPort.PortName;

            if (same)
            {
                return true;
            }
        }

        return false;
    }

    private bool InputPortAlreadyConnected(PortView inputPort)
    {
        foreach (EdgeData edge in currentGraph.edges)
        {
            bool sameInput =
                edge.toNodeId == inputPort.NodeId &&
                edge.toPortName == inputPort.PortName;

            if (sameInput)
            {
                return true;
            }
        }

        return false;
    }

    private void RenderGraph()
    {
        ClearViews();

        portViewLookup.Clear();
        nodeViewLookup.Clear();
        edgeViewLookup.Clear();

        if (currentGraph == null)
        {
            return;
        }

        foreach (NodeData node in currentGraph.nodes)
        {
            RenderSingleNode(node);
        }

        foreach (EdgeData edge in currentGraph.edges)
        {
            RenderSingleEdge(edge);
        }

        RefreshSelectionVisuals();
    }

    private void RenderSingleNode(NodeData node)
    {
        GameObject nodeObject = Instantiate(nodePrefab, nodeParent);

        NodeView nodeView = nodeObject.GetComponent<NodeView>();
        if (nodeView == null)
        {
            Debug.LogError("Node prefab does not have NodeView component.");
            return;
        }

        nodeView.Setup(node, this);
        nodeViewLookup[node.nodeId] = nodeView;
    }

    private void RenderSingleEdge(EdgeData edge)
    {
        if (edgeLinePrefab == null || edgeParent == null)
        {
            return;
        }

        string fromKey = MakePortKey(
            edge.fromNodeId,
            edge.fromPortName,
            PortDirection.Output
        );

        string toKey = MakePortKey(
            edge.toNodeId,
            edge.toPortName,
            PortDirection.Input
        );

        if (!portViewLookup.ContainsKey(fromKey))
        {
            Debug.LogWarning("Cannot find from port view: " + fromKey);
            return;
        }

        if (!portViewLookup.ContainsKey(toKey))
        {
            Debug.LogWarning("Cannot find to port view: " + toKey);
            return;
        }

        PortView fromPort = portViewLookup[fromKey];
        PortView toPort = portViewLookup[toKey];

        GameObject lineObject = Instantiate(edgeLinePrefab, edgeParent);

        EdgeView edgeView = lineObject.GetComponent<EdgeView>();
        if (edgeView == null)
        {
            Debug.LogError("Edge line prefab does not have EdgeView component.");
            return;
        }

        edgeView.Setup(
            edge,
            fromPort.RectTransform,
            toPort.RectTransform,
            edgeParent,
            this
        );

        edgeViewLookup[edge.edgeId] = edgeView;
    }

    private void ClearViews()
    {
        if (nodeParent != null)
        {
            for (int i = nodeParent.childCount - 1; i >= 0; i--)
            {
                Destroy(nodeParent.GetChild(i).gameObject);
            }
        }

        if (edgeParent != null)
        {
            for (int i = edgeParent.childCount - 1; i >= 0; i--)
            {
                Destroy(edgeParent.GetChild(i).gameObject);
            }
        }
    }

    public void ClearGraph()
    {
        if (currentGraph == null)
        {
            return;
        }

        currentGraph.nodes.Clear();
        currentGraph.edges.Clear();

        pendingOutputPort = null;

        RenderGraph();
    }

    public void OnNodeClicked(NodeView nodeView, PointerEventData eventData)
    {
        if (nodeView == null)
        {
            return;
        }

        string nodeId = nodeView.GetNodeId();

        if (string.IsNullOrEmpty(nodeId))
        {
            return;
        }

        bool multiSelect =
            Input.GetKey(KeyCode.LeftControl) ||
            Input.GetKey(KeyCode.RightControl) ||
            Input.GetKey(KeyCode.LeftShift) ||
            Input.GetKey(KeyCode.RightShift);

        if (!multiSelect)
        {
            selectedNodeIds.Clear();
            selectedEdgeIds.Clear();
            selectedNodeIds.Add(nodeId);
        }
        else
        {
            if (selectedNodeIds.Contains(nodeId))
            {
                selectedNodeIds.Remove(nodeId);
            }
            else
            {
                selectedNodeIds.Add(nodeId);
            }
        }

        RefreshSelectionVisuals();
    }

    private void RefreshSelectionVisuals()
    {
        foreach (KeyValuePair<string, NodeView> pair in nodeViewLookup)
        {
            bool selected = selectedNodeIds.Contains(pair.Key);
            pair.Value.SetSelected(selected);
        }

        foreach (KeyValuePair<string, EdgeView> pair in edgeViewLookup)
        {
            bool selected = selectedEdgeIds.Contains(pair.Key);
            pair.Value.SetSelected(selected);
        }
    }

    public void ClearSelection()
    {
        selectedNodeIds.Clear();
        RefreshSelectionVisuals();
    }

    public void PackSelectedNodesIntoContainer()
    {
        if (currentGraph == null)
        {
            return;
        }

        if (selectedNodeIds.Count == 0)
        {
            Debug.LogWarning("No nodes selected.");
            return;
        }

        List<NodeData> selectedNodes = new List<NodeData>();

        foreach (NodeData node in currentGraph.nodes)
        {
            if (selectedNodeIds.Contains(node.nodeId))
            {
                selectedNodes.Add(node);
            }
        }

        if (selectedNodes.Count == 0)
        {
            Debug.LogWarning("Selected node ids do not exist in current graph.");
            return;
        }

        string containerName = "PackedContainer";

        NodeData containerNode = CreatePackedContainerNode(containerName, selectedNodes);

        MoveSelectedNodesIntoContainer(containerNode, selectedNodes);

        currentGraph.nodes.Add(containerNode);

        selectedNodeIds.Clear();

        RenderGraph();

        Debug.Log("Packed " + selectedNodes.Count + " nodes into container: " + containerName);
    }

    private NodeData CreatePackedContainerNode(string containerName, List<NodeData> selectedNodes)
    {
        Vector2 center = GetSelectedNodesCenter(selectedNodes);

        NodeData containerNode = new NodeData();

        containerNode.nodeId = Guid.NewGuid().ToString();
        containerNode.definitionId = "custom.Container";
        containerNode.title = containerName;
        containerNode.symbol = "custom.Container";
        containerNode.category = "Custom Graph Nodes";
        containerNode.nodeKind = "ContainerNode";
        containerNode.position = center;

        containerNode.parameters = new List<NodeParam>();
        containerNode.parameters.Add(
            new NodeParam(
                "container_name",
                containerName,
                "str",
                true
            )
        );

        containerNode.inputPorts = new List<NodePortData>();
        containerNode.outputPorts = new List<NodePortData>();

        containerNode.innerGraph = new GraphData(containerName);

        return containerNode;
    }

    private Vector2 GetSelectedNodesCenter(List<NodeData> selectedNodes)
    {
        if (selectedNodes == null || selectedNodes.Count == 0)
        {
            return GetSpawnPosition();
        }

        Vector2 sum = Vector2.zero;

        foreach (NodeData node in selectedNodes)
        {
            sum += node.position;
        }

        return sum / selectedNodes.Count;
    }

    private void MoveSelectedNodesIntoContainer(NodeData containerNode, List<NodeData> selectedNodes)
    {
        if (containerNode == null || containerNode.innerGraph == null)
        {
            return;
        }

        HashSet<string> selectedSet = new HashSet<string>();

        foreach (NodeData node in selectedNodes)
        {
            selectedSet.Add(node.nodeId);
        }

        List<EdgeData> internalEdges = new List<EdgeData>();
        List<EdgeData> remainingEdges = new List<EdgeData>();

        foreach (EdgeData edge in currentGraph.edges)
        {
            bool fromSelected = selectedSet.Contains(edge.fromNodeId);
            bool toSelected = selectedSet.Contains(edge.toNodeId);

            if (fromSelected && toSelected)
            {
                internalEdges.Add(edge);
            }
            else
            {
                // For now, remove crossing edges.
                // Later we will convert crossing edges into exposed container ports.
                remainingEdges.Add(edge);
            }
        }

        foreach (NodeData node in selectedNodes)
        {
            currentGraph.nodes.Remove(node);
            containerNode.innerGraph.nodes.Add(node);
        }

        foreach (EdgeData edge in internalEdges)
        {
            containerNode.innerGraph.edges.Add(edge);
        }

        currentGraph.edges = remainingEdges;

        NormalizeInnerGraphNodePositions(containerNode.innerGraph);
    }

    private void NormalizeInnerGraphNodePositions(GraphData innerGraph)
    {
        if (innerGraph == null || innerGraph.nodes == null || innerGraph.nodes.Count == 0)
        {
            return;
        }

        Vector2 center = Vector2.zero;

        foreach (NodeData node in innerGraph.nodes)
        {
            center += node.position;
        }

        center /= innerGraph.nodes.Count;

        foreach (NodeData node in innerGraph.nodes)
        {
            node.position -= center;
        }
    }

    public void ValidateRootGraph()
    {
        if (graphBackendClient == null)
        {
            Debug.LogError("GraphBackendClient is not assigned.");
            return;
        }

        if (rootGraph == null)
        {
            Debug.LogError("Root graph is null.");
            return;
        }

        StartCoroutine(graphBackendClient.ValidateGraph(rootGraph));
    }

    public void DryRunRootGraph()
    {
        Debug.Log("DryRunRootGraph button clicked.");

        if (graphBackendClient == null)
        {
            Debug.LogError("GraphBackendClient is not assigned.");
            return;
        }

        if (rootGraph == null)
        {
            Debug.LogError("Root graph is null.");
            return;
        }

        StartCoroutine(graphBackendClient.DryRunGraph(rootGraph));
    }

    public void TrainRootGraph()
    {
        Debug.Log("TrainRootGraph button clicked.");

        string dataset = GetDatasetFromRootGraph();

        if (trainSettingsPopup != null)
        {
            trainSettingsPopup.Setup(this);
            trainSettingsPopup.Show(defaultTrainSettings, dataset);
            return;
        }

        GraphTrainSettings settings = defaultTrainSettings;
        settings.dataset = dataset;

        StartTrainingWithSettings(settings);
    }

    public void StartTrainingWithSettings(GraphTrainSettings settings)
    {
        if (graphBackendClient == null)
        {
            Debug.LogError("GraphBackendClient is not assigned.");
            return;
        }

        if (rootGraph == null)
        {
            Debug.LogError("Root graph is null.");
            return;
        }

        if (settings == null)
        {
            settings = new GraphTrainSettings();
        }

        settings.dataset = GetDatasetFromRootGraph();
        defaultTrainSettings = settings;

        StartCoroutine(graphBackendClient.TrainGraph(rootGraph, settings));
    }

    private string GetDatasetFromRootGraph()
    {
        string dataset = FindDatasetInGraph(rootGraph);

        if (string.IsNullOrWhiteSpace(dataset))
        {
            return "MNIST";
        }

        return dataset;
    }

    private string FindDatasetInGraph(GraphData graph)
    {
        if (graph == null || graph.nodes == null)
        {
            return "";
        }

        foreach (NodeData node in graph.nodes)
        {
            if (node.nodeKind == "DatasetNode")
            {
                string value = GetNodeParamValue(node, "dataset_name");

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            if (node.innerGraph != null)
            {
                string innerValue = FindDatasetInGraph(node.innerGraph);

                if (!string.IsNullOrWhiteSpace(innerValue))
                {
                    return innerValue;
                }
            }
        }

        return "";
    }

    public void ApplyResultNodeOutputs(List<ResultNodeResponse> results)
    {
        if (results == null)
        {
            return;
        }

        foreach (ResultNodeResponse result in results)
        {
            NodeData node = FindNodeByIdRecursive(rootGraph, result.nodeId);

            if (node != null)
            {
                node.resultText = result.text;
            }
        }

        RenderGraph();
    }

    private NodeData FindNodeByIdRecursive(GraphData graph, string nodeId)
    {
        if (graph == null || graph.nodes == null)
        {
            return null;
        }

        foreach (NodeData node in graph.nodes)
        {
            if (node.nodeId == nodeId)
            {
                return node;
            }

            if (node.innerGraph != null)
            {
                NodeData found = FindNodeByIdRecursive(node.innerGraph, nodeId);

                if (found != null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    public void SaveContainerAsTemplate(NodeData containerNode)
    {
        if (containerTemplateLibrary == null)
        {
            Debug.LogError("ContainerTemplateLibrary is not assigned.");
            return;
        }

        if (containerNode == null)
        {
            return;
        }

        if (!containerNode.IsContainer())
        {
            Debug.LogWarning("Only container node can be saved as template.");
            return;
        }

        containerTemplateLibrary.SaveContainerTemplate(containerNode);

        if (nodeLibraryMenuController != null)
        {
            nodeLibraryMenuController.RefreshMenu();
        }
    }

    public void AddContainerTemplateToGraph(ContainerTemplateData template)
    {
        if (containerTemplateLibrary == null)
        {
            Debug.LogError("ContainerTemplateLibrary is not assigned.");
            return;
        }

        NodeData node = containerTemplateLibrary.CreateNodeFromTemplate(template);

        if (node == null)
        {
            Debug.LogError("Failed to create node from template.");
            return;
        }

        node.position = GetSpawnPosition();

        currentGraph.nodes.Add(node);

        RenderGraph();
    }

    public void OnEdgeClicked(EdgeView edgeView, PointerEventData eventData)
    {
        if (edgeView == null)
        {
            return;
        }

        string edgeId = edgeView.EdgeId;

        if (string.IsNullOrEmpty(edgeId))
        {
            return;
        }

        bool multiSelect =
            Input.GetKey(KeyCode.LeftControl) ||
            Input.GetKey(KeyCode.RightControl) ||
            Input.GetKey(KeyCode.LeftShift) ||
            Input.GetKey(KeyCode.RightShift);

        if (!multiSelect)
        {
            selectedNodeIds.Clear();
            selectedEdgeIds.Clear();
            selectedEdgeIds.Add(edgeId);
        }
        else
        {
            if (selectedEdgeIds.Contains(edgeId))
            {
                selectedEdgeIds.Remove(edgeId);
            }
            else
            {
                selectedEdgeIds.Add(edgeId);
            }
        }

        RefreshSelectionVisuals();
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace))
        {
            if (IsTypingInInputField())
            {
                return;
            }

            DeleteSelected();
        }
    }

    private bool IsTypingInInputField()
    {
        if (EventSystem.current == null)
        {
            return false;
        }

        GameObject selected = EventSystem.current.currentSelectedGameObject;

        if (selected == null)
        {
            return false;
        }

        TMP_InputField input = selected.GetComponent<TMP_InputField>();

        if (input != null && input.isFocused)
        {
            return true;
        }

        return false;
    }

    public void DeleteSelected()
    {
        if (currentGraph == null)
        {
            return;
        }

        if (selectedNodeIds.Count == 0 && selectedEdgeIds.Count == 0)
        {
            return;
        }

        // Delete selected edges first.
        currentGraph.edges.RemoveAll(edge =>
            selectedEdgeIds.Contains(edge.edgeId)
        );

        // Delete selected nodes and all connected edges.
        if (selectedNodeIds.Count > 0)
        {
            currentGraph.nodes.RemoveAll(node =>
                selectedNodeIds.Contains(node.nodeId)
            );

            currentGraph.edges.RemoveAll(edge =>
                selectedNodeIds.Contains(edge.fromNodeId) ||
                selectedNodeIds.Contains(edge.toNodeId)
            );
        }

        selectedNodeIds.Clear();
        selectedEdgeIds.Clear();
        pendingOutputPort = null;

        RenderGraph();
    }

    public void FinalEvaluateRootGraph()
    {
        Debug.Log("FinalEvaluateRootGraph button clicked.");

        if (graphBackendClient == null)
        {
            Debug.LogError("GraphBackendClient is not assigned.");
            return;
        }

        if (rootGraph == null)
        {
            Debug.LogError("Root graph is null.");
            return;
        }

        string dataset = GetDatasetFromRootGraph();

        if (evaluateSettingsPopup != null)
        {
            evaluateSettingsPopup.Setup(this, graphBackendClient);
            evaluateSettingsPopup.Show(defaultTrainSettings, dataset);
            return;
        }

        StartFinalEvaluateWithSettings(defaultTrainSettings);
    }

    public void ShowLeaderboard()
    {
        if (graphBackendClient == null)
        {
            Debug.LogError("GraphBackendClient is not assigned.");
            return;
        }

        graphBackendClient.ShowLeaderboard(GetDatasetFromRootGraph());
    }

    public void StartFinalEvaluateWithSettings(GraphTrainSettings settings)
    {
        if (settings == null)
        {
            settings = new GraphTrainSettings();
        }

        settings.dataset = GetDatasetFromRootGraph();

        defaultTrainSettings = settings;

        StartCoroutine(graphBackendClient.FinalEvaluateGraph(rootGraph, settings));
    }
    //nanami's changes
    public void SaveGraphToFile(string filePath = "")
    {
        if (rootGraph == null)
        {
            Debug.LogError("Cannot save: Root graph is null.");
            return;
        }

        if (string.IsNullOrEmpty(filePath))
        {
            filePath = Path.Combine(Application.persistentDataPath, "saved_graph.json");
        }

        try
        {
            // Formatting.Indented keeps the JSON readable for debugging
            string json = JsonConvert.SerializeObject(rootGraph, Formatting.Indented, new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            });

            File.WriteAllText(filePath, json);
            Debug.Log("Graph saved successfully to: " + filePath);
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to save graph: " + e.Message);
        }
    }

    public void LoadGraphFromFile(string filePath = "")
    {
        if (string.IsNullOrEmpty(filePath))
        {
            filePath = Path.Combine(Application.persistentDataPath, "saved_graph.json");
        }

        if (!File.Exists(filePath))
        {
            Debug.LogWarning("Save file not found at: " + filePath);
            return;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            GraphData loadedGraph = JsonConvert.DeserializeObject<GraphData>(json);

            if (loadedGraph == null)
            {
                Debug.LogError("Deserialized graph was null.");
                return;
            }

            // Reset breadcrumbs/navigation back to root
            graphPath.Clear();
            containerNodePath.Clear();
            pendingOutputPort = null;

            // Swap out the current graph state with loaded data
            rootGraph = loadedGraph;
            currentGraph = rootGraph;
            graphPath.Add(rootGraph);

            // Re-render views in Unity
            RenderGraph();
            RefreshPathText();

            Debug.Log("Graph loaded successfully from: " + filePath);
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to load graph: " + e.Message);
        }
    }
}