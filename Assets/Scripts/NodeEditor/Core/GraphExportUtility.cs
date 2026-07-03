using System.Collections.Generic;

public static class GraphExportUtility
{
    public static GraphExportData ExportGraph(GraphData graph)
    {
        if (graph == null)
        {
            return null;
        }

        GraphExportData exportGraph = new GraphExportData();

        exportGraph.graphId = graph.graphId;
        exportGraph.displayName = graph.displayName;

        if (graph.nodes != null)
        {
            foreach (NodeData node in graph.nodes)
            {
                exportGraph.nodes.Add(ExportNode(node));
            }
        }

        if (graph.edges != null)
        {
            foreach (EdgeData edge in graph.edges)
            {
                exportGraph.edges.Add(ExportEdge(edge));
            }
        }

        return exportGraph;
    }

    private static NodeExportData ExportNode(NodeData node)
    {
        NodeExportData exportNode = new NodeExportData();

        exportNode.nodeId = node.nodeId;

        exportNode.definitionId = node.definitionId;
        exportNode.title = node.title;
        exportNode.symbol = node.symbol;
        exportNode.category = node.category;
        exportNode.nodeKind = node.nodeKind;

        exportNode.position = new PositionExportData(
            node.position.x,
            node.position.y
        );

        exportNode.parameters = new List<NodeParam>();
        if (node.parameters != null)
        {
            foreach (NodeParam param in node.parameters)
            {
                exportNode.parameters.Add(param);
            }
        }

        exportNode.inputPorts = new List<NodePortData>();
        if (node.inputPorts != null)
        {
            foreach (NodePortData port in node.inputPorts)
            {
                exportNode.inputPorts.Add(port);
            }
        }

        exportNode.outputPorts = new List<NodePortData>();
        if (node.outputPorts != null)
        {
            foreach (NodePortData port in node.outputPorts)
            {
                exportNode.outputPorts.Add(port);
            }
        }

        if (node.innerGraph != null)
        {
            exportNode.innerGraph = ExportGraph(node.innerGraph);
        }

        return exportNode;
    }

    private static EdgeExportData ExportEdge(EdgeData edge)
    {
        EdgeExportData exportEdge = new EdgeExportData();

        exportEdge.edgeId = edge.edgeId;

        exportEdge.fromNodeId = edge.fromNodeId;
        exportEdge.fromPortName = edge.fromPortName;

        exportEdge.toNodeId = edge.toNodeId;
        exportEdge.toPortName = edge.toPortName;

        return exportEdge;
    }
}