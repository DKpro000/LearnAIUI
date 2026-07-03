from collections import defaultdict, deque


def validate_graph_payload(payload: dict) -> dict:
    errors = []
    warnings = []

    graph = payload.get("graph")

    if graph is None:
        return {
            "success": False,
            "errors": ["Missing field: graph"],
            "warnings": [],
            "executionOrder": [],
        }

    root_result = validate_graph_recursive(
        graph=graph,
        graph_path="Root Graph",
        errors=errors,
        warnings=warnings,
    )

    return {
        "success": len(errors) == 0,
        "errors": errors,
        "warnings": warnings,
        "executionOrder": root_result.get("executionOrder", []),
    }


def validate_graph_recursive(graph: dict, graph_path: str, errors: list, warnings: list) -> dict:
    nodes = graph.get("nodes", [])
    edges = graph.get("edges", [])

    if not isinstance(nodes, list):
        errors.append(f"{graph_path}: nodes must be a list.")
        return {"executionOrder": []}

    if not isinstance(edges, list):
        errors.append(f"{graph_path}: edges must be a list.")
        return {"executionOrder": []}

    if len(nodes) == 0:
        warnings.append(f"{graph_path}: graph has no nodes.")
        return {"executionOrder": []}

    node_map = {}
    node_ids = set()

    for node in nodes:
        node_id = node.get("nodeId", "")
        title = node.get("title", "<unnamed>")

        if not node_id:
            errors.append(f"{graph_path}: node {title} is missing nodeId.")
            continue

        if node_id in node_ids:
            errors.append(f"{graph_path}: duplicate nodeId {node_id}.")
            continue

        node_ids.add(node_id)
        node_map[node_id] = node

    validate_node_basic_fields(
        graph_path=graph_path,
        nodes=nodes,
        errors=errors,
        warnings=warnings,
    )

    validate_node_parameters(
        graph_path=graph_path,
        nodes=nodes,
        errors=errors,
        warnings=warnings,
    )

    adjacency, reverse_adjacency = validate_edges(
        graph_path=graph_path,
        edges=edges,
        node_ids=node_ids,
        node_map=node_map,
        errors=errors,
        warnings=warnings,
    )

    execution_order = topological_sort(
        graph_path=graph_path,
        node_ids=node_ids,
        adjacency=adjacency,
        errors=errors,
    )

    validate_input_output_structure(
        graph_path=graph_path,
        nodes=nodes,
        edges=edges,
        adjacency=adjacency,
        reverse_adjacency=reverse_adjacency,
        errors=errors,
        warnings=warnings,
    )

    validate_reachability(
        graph_path=graph_path,
        nodes=nodes,
        adjacency=adjacency,
        errors=errors,
        warnings=warnings,
    )

    validate_containers_recursive(
        graph_path=graph_path,
        nodes=nodes,
        errors=errors,
        warnings=warnings,
    )

    readable_order = []

    for node_id in execution_order:
        node = node_map.get(node_id)
        if node is None:
            continue

        readable_order.append(
            {
                "nodeId": node_id,
                "title": node.get("title", ""),
                "nodeKind": node.get("nodeKind", ""),
                "symbol": node.get("symbol", ""),
            }
        )

    return {
        "executionOrder": readable_order
    }


def validate_node_basic_fields(graph_path: str, nodes: list, errors: list, warnings: list):
    for node in nodes:
        node_id = node.get("nodeId", "")
        title = node.get("title", "")
        node_kind = node.get("nodeKind", "")
        symbol = node.get("symbol", "")

        if not title:
            warnings.append(f"{graph_path}: node {node_id} has empty title.")

        if not node_kind:
            errors.append(f"{graph_path}: node {title} has empty nodeKind.")

        if not symbol:
            warnings.append(f"{graph_path}: node {title} has empty symbol.")


def validate_node_parameters(graph_path: str, nodes: list, errors: list, warnings: list):
    for node in nodes:
        title = node.get("title", "")
        node_kind = node.get("nodeKind", "")
        parameters = node.get("parameters", [])

        if not isinstance(parameters, list):
            errors.append(f"{graph_path}: node {title} parameters must be a list.")
            continue

        for param in parameters:
            key = param.get("key", "")
            value = param.get("value", "")
            required = param.get("required", False)

            if required and str(value).strip() == "":
                errors.append(
                    f"{graph_path}: node {title} required parameter '{key}' is empty."
                )

        # Container name should not be empty
        if node_kind == "ContainerNode":
            container_name = get_param_value(parameters, "container_name")

            if container_name is not None and str(container_name).strip() == "":
                errors.append(f"{graph_path}: container node {title} has empty container_name.")


def validate_edges(
    graph_path: str,
    edges: list,
    node_ids: set,
    node_map: dict,
    errors: list,
    warnings: list,
):
    adjacency = defaultdict(list)
    reverse_adjacency = defaultdict(list)
    connected_inputs = set()

    for edge in edges:
        edge_id = edge.get("edgeId", "")

        from_node = edge.get("fromNodeId", "")
        from_port = edge.get("fromPortName", "")

        to_node = edge.get("toNodeId", "")
        to_port = edge.get("toPortName", "")

        if not edge_id:
            warnings.append(f"{graph_path}: edge missing edgeId.")

        if not from_node:
            errors.append(f"{graph_path}: edge missing fromNodeId.")
            continue

        if not to_node:
            errors.append(f"{graph_path}: edge missing toNodeId.")
            continue

        if from_node not in node_ids:
            errors.append(f"{graph_path}: edge references missing fromNodeId {from_node}.")
            continue

        if to_node not in node_ids:
            errors.append(f"{graph_path}: edge references missing toNodeId {to_node}.")
            continue

        if from_node == to_node:
            errors.append(f"{graph_path}: self-loop edge is not allowed on node {from_node}.")
            continue

        if not from_port:
            errors.append(f"{graph_path}: edge from {from_node} missing fromPortName.")

        if not to_port:
            errors.append(f"{graph_path}: edge to {to_node} missing toPortName.")

        input_key = f"{to_node}:{to_port}"

        if input_key in connected_inputs:
            errors.append(
                f"{graph_path}: input port {input_key} has multiple incoming edges."
            )
        else:
            connected_inputs.add(input_key)

        adjacency[from_node].append(to_node)
        reverse_adjacency[to_node].append(from_node)

    # Make sure every node exists in adjacency maps
    for node_id in node_ids:
        adjacency[node_id] = adjacency[node_id]
        reverse_adjacency[node_id] = reverse_adjacency[node_id]

    return adjacency, reverse_adjacency


def topological_sort(graph_path: str, node_ids: set, adjacency: dict, errors: list) -> list:
    indegree = {node_id: 0 for node_id in node_ids}

    for from_node, to_nodes in adjacency.items():
        for to_node in to_nodes:
            if to_node in indegree:
                indegree[to_node] += 1

    queue = deque()

    for node_id, degree in indegree.items():
        if degree == 0:
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
        errors.append(f"{graph_path}: graph has a cycle. Neural network graph must be acyclic.")

    return result


def validate_input_output_structure(
    graph_path: str,
    nodes: list,
    edges: list,
    adjacency: dict,
    reverse_adjacency: dict,
    errors: list,
    warnings: list,
):
    input_nodes = [
        n for n in nodes
        if n.get("nodeKind") in ["InputNode", "DatasetNode"]
    ]
    output_nodes = [n for n in nodes if n.get("nodeKind") == "OutputNode"]

    if len(input_nodes) == 0:
        errors.append(f"{graph_path}: graph must contain at least one Input node.")

    if len(output_nodes) == 0:
        errors.append(f"{graph_path}: graph must contain at least one Output node.")

    for node in nodes:
        node_id = node.get("nodeId", "")
        title = node.get("title", "")
        node_kind = node.get("nodeKind", "")

        incoming_count = len(reverse_adjacency[node_id])
        outgoing_count = len(adjacency[node_id])

        if node_kind == "InputNode":
            if incoming_count > 0:
                errors.append(f"{graph_path}: Input node {title} should not have incoming edges.")

            if outgoing_count == 0:
                warnings.append(f"{graph_path}: Input node {title} has no outgoing edge.")

        elif node_kind == "OutputNode":
            if incoming_count == 0:
                errors.append(f"{graph_path}: Output node {title} must have an incoming edge.")

            # Model Output is allowed to connect to ResultOutputNode.
            for edge in edges:
                if edge.get("fromNodeId") != node_id:
                    continue

                to_node_id = edge.get("toNodeId")
                to_node = find_node_by_id(nodes, to_node_id)

                if to_node is None:
                    continue

                if to_node.get("nodeKind") not in ["ResultOutputNode", "FinalResultNode"]:
                    errors.append(
                        f"{graph_path}: Output node {title} can only connect to ResultOutputNode or FinalResultNode."
                    )
        
        elif node_kind == "FinalResultNode":
            if incoming_count == 0:
                errors.append(
                    f"{graph_path}: Final Result node {title} must connect from Model Output."
                )

            if outgoing_count > 0:
                errors.append(
                    f"{graph_path}: Final Result node {title} should not have outgoing edges."
                )

        elif node_kind == "ResultOutputNode":
            if incoming_count == 0:
                errors.append(f"{graph_path}: Result Output node {title} must connect from Model Output.")

            if outgoing_count > 0:
                errors.append(f"{graph_path}: Result Output node {title} should not have outgoing edges.")

        elif node_kind == "DatasetNode":
            if incoming_count > 0:
                errors.append(f"{graph_path}: Dataset node {title} should not have incoming edges.")

            if outgoing_count == 0:
                warnings.append(f"{graph_path}: Dataset node {title} has no outgoing edge.")

        elif node_kind in ["ModuleNode", "FunctionNode"]:
            if incoming_count == 0:
                warnings.append(f"{graph_path}: node {title} has no input connection.")

            if outgoing_count == 0:
                warnings.append(f"{graph_path}: node {title} has no output connection.")

        elif node_kind == "OperationNode":
            validate_operation_node_connections(
                graph_path=graph_path,
                node=node,
                edges=edges,
                errors=errors,
                warnings=warnings,
            )

            if outgoing_count == 0:
                warnings.append(f"{graph_path}: operation node {title} has no output connection.")


def validate_reachability(
    graph_path: str,
    nodes: list,
    adjacency: dict,
    errors: list,
    warnings: list,
):
    input_nodes = [
        n for n in nodes
        if n.get("nodeKind") in ["InputNode", "DatasetNode"]
    ]
    output_nodes = [n for n in nodes if n.get("nodeKind") == "OutputNode"]

    if len(input_nodes) == 0 or len(output_nodes) == 0:
        return

    start_ids = [n.get("nodeId") for n in input_nodes]
    output_ids = set(n.get("nodeId") for n in output_nodes)

    visited = set()
    queue = deque(start_ids)

    while queue:
        current = queue.popleft()

        if current in visited:
            continue

        visited.add(current)

        for next_node in adjacency[current]:
            if next_node not in visited:
                queue.append(next_node)

    reachable_outputs = output_ids.intersection(visited)

    if len(reachable_outputs) == 0:
        errors.append(f"{graph_path}: no Output node is reachable from any Input node.")

    for node in nodes:
        node_id = node.get("nodeId", "")
        title = node.get("title", "")

        if node.get("nodeKind") in ["DatasetNode"]:
            continue

        if node.get("nodeKind") in ["DatasetNode", "ResultOutputNode", "FinalResultNode"]:
            continue

        if node_id not in visited and node.get("nodeKind") != "InputNode":
            warnings.append(f"{graph_path}: node {title} is not reachable from Input.")


def validate_containers_recursive(graph_path: str, nodes: list, errors: list, warnings: list):
    for node in nodes:
        node_kind = node.get("nodeKind", "")
        title = node.get("title", "")

        if node_kind != "ContainerNode":
            continue

        inner_graph = node.get("innerGraph")

        if inner_graph is None:
            warnings.append(f"{graph_path}: container {title} has no innerGraph.")
            continue

        validate_graph_recursive(
            graph=inner_graph,
            graph_path=f"{graph_path}/{title}",
            errors=errors,
            warnings=warnings,
        )


def get_param_value(parameters: list, key: str):
    for param in parameters:
        if param.get("key") == key:
            return param.get("value")

    return None

def validate_operation_node_connections(
    graph_path: str,
    node: dict,
    edges: list,
    errors: list,
    warnings: list,
):
    node_id = node.get("nodeId", "")
    title = node.get("title", "")
    symbol = node.get("symbol", "")

    incoming_ports = set()

    for edge in edges:
        if edge.get("toNodeId") == node_id:
            incoming_ports.add(edge.get("toPortName", ""))

    if title == "Add" or symbol == "torch.add":
        required_ports = {"a", "b"}

        missing = required_ports - incoming_ports

        if missing:
            errors.append(
                f"{graph_path}: Add node requires input ports a and b. Missing: {sorted(list(missing))}"
            )

    elif title == "Cat" or symbol == "torch.cat":
        required_ports = {"a", "b"}

        missing = required_ports - incoming_ports

        if missing:
            errors.append(
                f"{graph_path}: Cat node requires input ports a and b. Missing: {sorted(list(missing))}"
            )

def find_node_by_id(nodes: list, node_id: str):
    for node in nodes:
        if node.get("nodeId") == node_id:
            return node

    return None