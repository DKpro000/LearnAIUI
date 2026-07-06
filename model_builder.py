import ast
import importlib
from collections import defaultdict, deque

import torch
import torch.nn as nn


class GeneratedGraphModel(nn.Module):
    def __init__(self, graph: dict, execution_order: list[str]):
        super().__init__()

        self.graph = graph
        self.execution_order = execution_order

        self.node_map = {
            node["nodeId"]: node
            for node in graph.get("nodes", [])
        }

        self.incoming_edges = build_incoming_edges(graph.get("edges", []))
        self.outgoing_edges = build_outgoing_edges(graph.get("edges", []))

        self.modules_by_key = nn.ModuleDict()
        self.node_id_to_module_key = {}

        module_index = 0

        for node_id in execution_order:
            node = self.node_map[node_id]
            node_kind = node.get("nodeKind", "")

            if node_kind == "ModuleNode":
                module_key = f"m{module_index:04d}"
                module_index += 1

                self.node_id_to_module_key[node_id] = module_key
                self.modules_by_key[module_key] = instantiate_module_node(node)

            elif node_kind == "ContainerNode":
                module_key = f"m{module_index:04d}"
                module_index += 1

                self.node_id_to_module_key[node_id] = module_key
                self.modules_by_key[module_key] = instantiate_container_node(node)
    def forward(self, x):
        values = {}

        for node_id in self.execution_order:
            node = self.node_map[node_id]
            node_kind = node.get("nodeKind", "")
            title = node.get("title", "")

            if node_kind == "DatasetNode":
                # DatasetNode is the graph-level data source.
                # During dry run / training, x is the actual input batch.
                values[node_id] = x

            elif node_kind == "InputNode":
                # InputNode is used inside containers.
                # It receives the tensor passed into that container.
                values[node_id] = x

            elif node_kind == "OutputNode":
                input_value = get_single_input_value(
                    node_id,
                    values,
                    self.incoming_edges
                )
                values[node_id] = input_value

            elif node_kind in ["ResultOutputNode", "FinalResultNode"]:
                continue

            elif node_kind == "ModuleNode":
                input_value = get_single_input_value(
                    node_id,
                    values,
                    self.incoming_edges
                )
                module_key = self.node_id_to_module_key[node_id]
                module = self.modules_by_key[module_key]
                values[node_id] = module(input_value)

            elif node_kind == "ContainerNode":
                input_value = get_single_input_value(
                    node_id,
                    values,
                    self.incoming_edges
                )
                module_key = self.node_id_to_module_key[node_id]
                module = self.modules_by_key[module_key]
                values[node_id] = module(input_value)

            elif node_kind == "FunctionNode":
                input_value = get_single_input_value(
                    node_id,
                    values,
                    self.incoming_edges
                )
                values[node_id] = call_function_node(node, input_value)

            elif node_kind == "OperationNode":
                values[node_id] = call_operation_node(
                    node=node,
                    values=values,
                    incoming_edges=self.incoming_edges,
                )

            else:
                raise RuntimeError(
                    f"Unsupported node kind in model builder: {node_kind} ({title})"
                )

        output_nodes = [
            node_id
            for node_id in self.execution_order
            if self.node_map[node_id].get("nodeKind") == "OutputNode"
        ]

        if len(output_nodes) == 0:
            raise RuntimeError("No Output node found.")

        # Multiple OutputNodes are display nodes.
        # The actual model tensor is the first valid OutputNode result.
        return values[output_nodes[0]]


def dry_run_graph(payload: dict) -> dict:
    graph = payload.get("graph")

    if graph is None:
        return {
            "success": False,
            "errors": ["Missing graph field."],
            "warnings": [],
            "modelSummary": "",
            "inputShape": [],
            "outputShape": [],
        }

    try:
        execution_order = get_topological_order(graph)
        input_shape = get_input_shape(graph)

        model = GeneratedGraphModel(graph, execution_order)

        x = torch.randn(*input_shape)

        with torch.no_grad():
            y = model(x)

        return {
            "success": True,
            "errors": [],
            "warnings": [],
            "modelSummary": str(model),
            "inputShape": list(x.shape),
            "outputShape": list(y.shape),
            "executionOrder": readable_execution_order(graph, execution_order),
        }

    except Exception as e:
        return {
            "success": False,
            "errors": [str(e)],
            "warnings": [],
            "modelSummary": "",
            "inputShape": [],
            "outputShape": [],
            "executionOrder": [],
        }


def get_topological_order(graph: dict) -> list[str]:
    nodes = graph.get("nodes", [])
    edges = graph.get("edges", [])

    node_ids = [node["nodeId"] for node in nodes]
    node_id_set = set(node_ids)

    adjacency = defaultdict(list)
    indegree = {node_id: 0 for node_id in node_ids}

    for edge in edges:
        from_node = edge["fromNodeId"]
        to_node = edge["toNodeId"]

        if from_node not in node_id_set:
            raise RuntimeError(f"Edge references missing fromNodeId: {from_node}")

        if to_node not in node_id_set:
            raise RuntimeError(f"Edge references missing toNodeId: {to_node}")

        adjacency[from_node].append(to_node)
        indegree[to_node] += 1

    queue = deque()

    for node_id in node_ids:
        if indegree[node_id] == 0:
            queue.append(node_id)

    result = []

    while queue:
        current = queue.popleft()
        result.append(current)

        for next_node in adjacency[current]:
            indegree[next_node] -= 1

            if indegree[next_node] == 0:
                queue.append(next_node)

    if len(result) != len(node_ids):
        raise RuntimeError("Graph has a cycle.")

    return result


def get_input_shape(graph: dict) -> list[int]:
    dataset_nodes = [
        node
        for node in graph.get("nodes", [])
        if node.get("nodeKind") == "DatasetNode"
    ]

    if len(dataset_nodes) > 0:
        dataset_node = dataset_nodes[0]
        params = params_to_dict(dataset_node.get("parameters", []))

        shape_text = params.get("input_shape", "[1, 784]")
        shape = parse_value(shape_text)

        if not isinstance(shape, list):
            raise RuntimeError(
                "Dataset input_shape must be a list, for example [1, 784]."
            )

        return [int(v) for v in shape]

    input_nodes = [
        node
        for node in graph.get("nodes", [])
        if node.get("nodeKind") == "InputNode"
    ]

    if len(input_nodes) == 0:
        raise RuntimeError("Graph must have a Dataset node or Input node.")

    input_node = input_nodes[0]
    params = params_to_dict(input_node.get("parameters", []))

    shape_text = params.get("shape", "[1, 784]")
    shape = parse_value(shape_text)

    if not isinstance(shape, list):
        raise RuntimeError("Input shape must be a list, for example [1, 784].")

    return [int(v) for v in shape]


def build_incoming_edges(edges: list[dict]) -> dict:
    incoming = defaultdict(list)

    for edge in edges:
        incoming[edge["toNodeId"]].append(edge)

    return incoming


def build_outgoing_edges(edges: list[dict]) -> dict:
    outgoing = defaultdict(list)

    for edge in edges:
        outgoing[edge["fromNodeId"]].append(edge)

    return outgoing


def get_single_input_value(node_id: str, values: dict, incoming_edges: dict):
    edges = incoming_edges.get(node_id, [])

    if len(edges) == 0:
        raise RuntimeError(f"Node {node_id} has no input edge.")

    if len(edges) > 1:
        raise RuntimeError(
            f"Node {node_id} has multiple input edges. "
            "This minimal builder only supports linear single-input nodes."
        )

    from_node_id = edges[0]["fromNodeId"]

    if from_node_id not in values:
        raise RuntimeError(f"Input value for node {node_id} is not ready.")

    return values[from_node_id]


def instantiate_module_node(node: dict):
    symbol = node.get("symbol", "")

    cls = resolve_symbol(symbol)

    params = params_to_kwargs(
        node.get("parameters", []),
        skip_names=set()
    )

    return cls(**params)

def instantiate_container_node(node: dict):
    title = node.get("title", "Container")
    inner_graph = node.get("innerGraph")

    if inner_graph is None:
        raise RuntimeError(f"Container node {title} has no innerGraph.")

    inner_nodes = inner_graph.get("nodes", [])
    if len(inner_nodes) == 0:
        raise RuntimeError(f"Container node {title} has empty innerGraph.")

    inner_execution_order = get_topological_order(inner_graph)

    return GeneratedGraphModel(
        graph=inner_graph,
        execution_order=inner_execution_order
    )


def call_function_node(node: dict, input_value):
    symbol = node.get("symbol", "")

    fn = resolve_symbol(symbol)

    params = params_to_kwargs(
        node.get("parameters", []),
        skip_names={
            "input",
            "x",
            "tensor",
            "self",
        }
    )

    return fn(input_value, **params)

def call_operation_node(node: dict, values: dict, incoming_edges: dict):
    title = node.get("title", "")
    symbol = node.get("symbol", "")

    inputs = get_inputs_by_port(
        node_id=node["nodeId"],
        values=values,
        incoming_edges=incoming_edges,
    )

    if symbol == "torch.add" or title == "Add":
        if "a" not in inputs or "b" not in inputs:
            raise RuntimeError("Add node requires input ports 'a' and 'b'.")

        return torch.add(inputs["a"], inputs["b"])

    if symbol == "torch.cat" or title == "Cat":
        if "a" not in inputs or "b" not in inputs:
            raise RuntimeError("Cat node requires input ports 'a' and 'b'.")

        params = params_to_dict(node.get("parameters", []))
        dim = parse_value(params.get("dim", "1"))

        return torch.cat([inputs["a"], inputs["b"]], dim=dim)

    raise RuntimeError(f"Unsupported OperationNode: {title} ({symbol})")


def get_inputs_by_port(node_id: str, values: dict, incoming_edges: dict):
    result = {}

    edges = incoming_edges.get(node_id, [])

    for edge in edges:
        from_node_id = edge["fromNodeId"]
        to_port_name = edge["toPortName"]

        if from_node_id not in values:
            raise RuntimeError(
                f"Input value for node {node_id}.{to_port_name} is not ready."
            )

        result[to_port_name] = values[from_node_id]

    return result


def resolve_symbol(symbol: str):
    if not symbol:
        raise RuntimeError("Empty symbol.")

    module_path, attr_name = symbol.rsplit(".", 1)
    module = importlib.import_module(module_path)

    return getattr(module, attr_name)


def params_to_dict(parameters: list[dict]) -> dict:
    result = {}

    for param in parameters:
        key = param.get("key", "")
        value = param.get("value", "")

        if key:
            result[key] = value

    return result


def params_to_kwargs(parameters: list[dict], skip_names: set[str]) -> dict:
    kwargs = {}

    for param in parameters:
        key = param.get("key", "")
        value = param.get("value", "")

        if not key:
            continue

        if key in skip_names:
            continue

        if str(value).strip() == "":
            continue

        parsed_value = parse_value(value)

        if parsed_value is None:
            # For constructor args like device=None or dtype=None,
            # usually better to skip them.
            continue

        kwargs[key] = parsed_value

    return kwargs


def parse_value(value):
    if value is None:
        return None

    if isinstance(value, (int, float, bool, list, tuple)):
        return value

    text = str(value).strip()

    if text == "":
        return ""

    if text == "None":
        return None

    if text == "True":
        return True

    if text == "False":
        return False

    # Convert PyTorch default strings such as "zeros"
    # into normal Python strings.
    if text in ["zeros", "reflect", "replicate", "circular"]:
        return text

    try:
        return ast.literal_eval(text)
    except Exception:
        pass

    try:
        return int(text)
    except Exception:
        pass

    try:
        return float(text)
    except Exception:
        pass

    return text


def readable_execution_order(graph: dict, execution_order: list[str]) -> list[dict]:
    node_map = {
        node["nodeId"]: node
        for node in graph.get("nodes", [])
    }

    readable = []

    for node_id in execution_order:
        node = node_map[node_id]

        readable.append(
            {
                "nodeId": node_id,
                "title": node.get("title", ""),
                "nodeKind": node.get("nodeKind", ""),
                "symbol": node.get("symbol", ""),
            }
        )

    return readable