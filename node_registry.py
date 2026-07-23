import inspect
import torch.nn as nn
import torch.nn.functional as F
import torch.nn.utils as nn_utils

FUNCTION_RUNTIME_INPUT_NAMES = {
    "input",
    "x",
    "tensor",
    "tensors",
    "target",
    "targets",
    "label",
    "labels",
}

MODULE_CATEGORY_RULES = [
    ("torch.nn.modules.container", "Containers"),
    ("torch.nn.modules.conv", "Convolution Layers"),
    ("torch.nn.modules.pooling", "Pooling layers"),
    ("torch.nn.modules.padding", "Padding Layers"),
    ("torch.nn.modules.activation", "Non-linear Activations"),
    ("torch.nn.modules.normalization", "Normalization Layers"),
    ("torch.nn.modules.batchnorm", "Normalization Layers"),
    ("torch.nn.modules.instancenorm", "Normalization Layers"),
    ("torch.nn.modules.rnn", "Recurrent Layers"),
    ("torch.nn.modules.transformer", "Transformer Layers"),
    ("torch.nn.modules.linear", "Linear Layers"),
    ("torch.nn.modules.dropout", "Dropout Layers"),
    ("torch.nn.modules.sparse", "Sparse Layers"),
    ("torch.nn.modules.distance", "Distance Functions"),
    ("torch.nn.modules.loss", "Loss Functions"),
    ("torch.nn.modules.pixelshuffle", "Vision Layers"),
    ("torch.nn.modules.channelshuffle", "Shuffle Layers"),
    ("torch.nn.parallel", "DataParallel Layers"),
]


FUNCTIONAL_CATEGORY_RULES = {
    "Convolution Functions": [
        "conv1d", "conv2d", "conv3d",
        "conv_transpose1d", "conv_transpose2d", "conv_transpose3d",
        "unfold", "fold",
    ],
    "Pooling Functions": [
        "avg_pool1d", "avg_pool2d", "avg_pool3d",
        "max_pool1d", "max_pool2d", "max_pool3d",
        "max_unpool1d", "max_unpool2d", "max_unpool3d",
        "lp_pool1d", "lp_pool2d", "lp_pool3d",
        "adaptive_max_pool1d", "adaptive_max_pool2d", "adaptive_max_pool3d",
        "adaptive_avg_pool1d", "adaptive_avg_pool2d", "adaptive_avg_pool3d",
        "fractional_max_pool2d", "fractional_max_pool3d",
    ],
    "Activation Functions": [
        "threshold", "relu", "hardtanh", "hardswish", "relu6",
        "elu", "selu", "celu", "leaky_relu", "prelu", "rrelu",
        "glu", "gelu", "logsigmoid", "hardshrink", "tanhshrink",
        "softsign", "softplus", "softmin", "softmax", "softshrink",
        "gumbel_softmax", "log_softmax", "tanh", "sigmoid",
        "hardsigmoid", "silu", "mish",
    ],
    "Normalization Functions": [
        "batch_norm", "group_norm", "instance_norm",
        "layer_norm", "local_response_norm", "rms_norm", "normalize",
    ],
    "Linear Functions": [
        "linear", "bilinear",
    ],
    "Dropout Functions": [
        "dropout", "alpha_dropout", "feature_alpha_dropout",
        "dropout1d", "dropout2d", "dropout3d",
    ],
    "Sparse Functions": [
        "embedding", "embedding_bag", "one_hot",
    ],
    "Distance Functions": [
        "pairwise_distance", "cosine_similarity", "pdist",
    ],
    "Loss Functions": [
        "binary_cross_entropy",
        "binary_cross_entropy_with_logits",
        "poisson_nll_loss",
        "cosine_embedding_loss",
        "cross_entropy",
        "ctc_loss",
        "gaussian_nll_loss",
        "hinge_embedding_loss",
        "kl_div",
        "l1_loss",
        "mse_loss",
        "margin_ranking_loss",
        "multilabel_margin_loss",
        "multilabel_soft_margin_loss",
        "multi_margin_loss",
        "nll_loss",
        "huber_loss",
        "smooth_l1_loss",
        "soft_margin_loss",
        "triplet_margin_loss",
        "triplet_margin_with_distance_loss",
    ],
    "Vision Functions": [
        "pixel_shuffle", "pixel_unshuffle", "pad", "interpolate",
        "upsample", "upsample_nearest", "upsample_bilinear",
        "grid_sample", "affine_grid",
    ],
}


EXPLICIT_PARAMETER_NAMES = {
    "Parameter",
    "Buffer",
    "UninitializedParameter",
    "UninitializedBuffer",
}


EXPLICIT_CONTAINER_NAMES = {
    "Module",
    "Sequential",
    "ModuleList",
    "ModuleDict",
    "ParameterList",
    "ParameterDict",
}

BOOLEAN_PARAM_NAMES = {
    "bias",
    "inplace",
    "affine",
    "track_running_stats",
    "return_indices",
    "ceil_mode",
    "batch_first",
    "bidirectional",
    "norm_first",
    "elementwise_affine",
    "add_bias_kv",
    "add_zero_attn",
}

INT_PARAM_NAMES = {
    "in_channels",
    "out_channels",
    "num_features",
    "in_features",
    "out_features",
    "embedding_dim",
    "num_embeddings",
    "hidden_size",
    "input_size",
    "num_layers",
    "num_heads",
    "groups",
    "dilation",
    "stride",
    "padding",
    "output_padding",
    "kernel_size",
    "start_dim",
    "end_dim",
    "dim",
}

FLOAT_PARAM_NAMES = {
    "p",
    "eps",
    "momentum",
    "negative_slope",
    "alpha",
    "beta",
    "threshold",
    "value",
    "dropout",
    "label_smoothing",
}

ADVANCED_PARAM_NAMES = {
    "device",
    "dtype",
    "factory_kwargs",
}

DROPDOWN_PARAM_OPTIONS = {
    "padding_mode": ["zeros", "reflect", "replicate", "circular"],
    "mode": [
        "nearest",
        "linear",
        "bilinear",
        "bicubic",
        "trilinear",
        "area",
        "nearest-exact",
    ],
    "reduction": ["none", "mean", "sum"],
    "nonlinearity": [
        "linear",
        "conv1d",
        "conv2d",
        "conv3d",
        "conv_transpose1d",
        "conv_transpose2d",
        "conv_transpose3d",
        "sigmoid",
        "tanh",
        "relu",
        "leaky_relu",
        "selu",
    ],
}


def infer_control_type(name: str, default_value_text: str, type_text: str):
    if name in ADVANCED_PARAM_NAMES:
        return "hidden", [], True

    if name in DROPDOWN_PARAM_OPTIONS:
        return "dropdown", DROPDOWN_PARAM_OPTIONS[name], False

    if name in BOOLEAN_PARAM_NAMES:
        return "checkbox", [], False

    if default_value_text in ["True", "False"]:
        return "checkbox", [], False

    if name in FLOAT_PARAM_NAMES:
        return "float", [], False

    if name in INT_PARAM_NAMES:
        if name in ["kernel_size", "stride", "padding", "dilation", "output_padding", "output_size"]:
            return "text", [], False

        return "int", [], False

    if "bool" in type_text:
        return "checkbox", [], False

    if "float" in type_text:
        return "float", [], False

    if "int" in type_text:
        return "int", [], False

    if "Tuple" in type_text or "tuple" in type_text:
        return "text", [], False

    if "List" in type_text or "list" in type_text:
        return "text", [], False

    return "text", [], False


def safe_default(value):
    if value is inspect.Parameter.empty:
        return ""

    if value is None:
        return "None"

    if isinstance(value, bool):
        return "True" if value else "False"

    if isinstance(value, (int, float, str)):
        return str(value)

    if isinstance(value, tuple):
        return str(value)

    return str(value)


def parse_signature(obj):
    """
    把 Python function/class 的参数转换成 Unity 可以显示的参数表。
    并且为每个参数附加 UI controlType。
    """
    try:
        target = obj.__init__ if inspect.isclass(obj) else obj
        sig = inspect.signature(target)
    except Exception:
        return []

    params = []

    for name, param in sig.parameters.items():
        if name in ["self", "args", "kwargs"]:
            continue

        if param.kind in [
            inspect.Parameter.VAR_POSITIONAL,
            inspect.Parameter.VAR_KEYWORD,
        ]:
            continue

        required = param.default is inspect.Parameter.empty

        if param.annotation is inspect.Parameter.empty:
            type_text = "Any"
        else:
            type_text = str(param.annotation)

        default_value = safe_default(param.default)

        control_type, options, advanced = infer_control_type(
            name=name,
            default_value_text=default_value,
            type_text=type_text,
        )

        params.append(
            {
                "name": name,
                "type": type_text,
                "required": required,
                "defaultValue": default_value,
                "controlType": control_type,
                "options": options,
                "advanced": advanced,
            }
        )

    return params


def get_module_category(obj, name):
    if name in EXPLICIT_PARAMETER_NAMES:
        return "Parameters / Buffers"

    module_path = getattr(obj, "__module__", "")

    for prefix, category in MODULE_CATEGORY_RULES:
        if module_path.startswith(prefix):
            return category

    return "Other torch.nn Modules"


def get_functional_category(name):
    for category, names in FUNCTIONAL_CATEGORY_RULES.items():
        if name in names:
            return "Functional / " + category

    return "Functional / Other"


def get_node_kind(category, name, is_function=False):
    if is_function:
        if "Loss Functions" in category:
            return "LossFunctionNode"
        return "FunctionNode"

    if category == "Containers":
        return "ContainerNode"

    if category == "Loss Functions":
        return "LossNode"

    if category == "Parameters / Buffers":
        return "ParameterNode"

    if category == "DataParallel Layers":
        return "WrapperNode"

    if category.startswith("Utilities"):
        return "UtilityNode"

    return "ModuleNode"


def get_allowed_flags(node_kind):
    """
    不是所有 node 都能放进模型 forward。
    例如 CrossEntropyLoss 应该在 Loss Graph。
    clip_grad_norm_ 应该在 Training Graph。
    """
    if node_kind in ["ModuleNode", "FunctionNode"]:
        return {
            "allowedInModelGraph": True,
            "allowedInSequential": node_kind == "ModuleNode",
            "allowedInLossGraph": False,
            "allowedInTrainingGraph": False,
        }

    if node_kind == "ContainerNode":
        return {
            "allowedInModelGraph": True,
            "allowedInSequential": False,
            "allowedInLossGraph": False,
            "allowedInTrainingGraph": False,
        }

    if node_kind in ["LossNode", "LossFunctionNode"]:
        return {
            "allowedInModelGraph": False,
            "allowedInSequential": False,
            "allowedInLossGraph": True,
            "allowedInTrainingGraph": True,
        }

    if node_kind in ["UtilityNode", "WrapperNode"]:
        return {
            "allowedInModelGraph": False,
            "allowedInSequential": False,
            "allowedInLossGraph": False,
            "allowedInTrainingGraph": True,
        }

    if node_kind == "ParameterNode":
        return {
            "allowedInModelGraph": True,
            "allowedInSequential": False,
            "allowedInLossGraph": False,
            "allowedInTrainingGraph": False,
        }

    return {
        "allowedInModelGraph": False,
        "allowedInSequential": False,
        "allowedInLossGraph": False,
        "allowedInTrainingGraph": False,
    }


def get_ports(node_kind):
    if node_kind in ["ModuleNode", "FunctionNode", "ContainerNode"]:
        return {
            "inputPorts": [
                {"name": "x", "portType": "Tensor"}
            ],
            "outputPorts": [
                {"name": "out", "portType": "Tensor"}
            ],
        }

    if node_kind in ["LossNode", "LossFunctionNode"]:
        return {
            "inputPorts": [
                {"name": "prediction", "portType": "Tensor"},
                {"name": "target", "portType": "Tensor"},
            ],
            "outputPorts": [
                {"name": "loss", "portType": "Tensor"}
            ],
        }

    if node_kind == "ParameterNode":
        return {
            "inputPorts": [],
            "outputPorts": [
                {"name": "value", "portType": "Tensor"}
            ],
        }

    return {
        "inputPorts": [
            {"name": "input", "portType": "Any"}
        ],
        "outputPorts": [
            {"name": "output", "portType": "Any"}
        ],
    }

def get_doc_url(definition_id: str) -> str:
    if definition_id.startswith("custom."):
        return ""
    return f"https://pytorch.org/docs/stable/generated/{definition_id}.html"

def make_definition(
    definition_id,
    display_name,
    symbol,
    category,
    node_kind,
    obj,
):
    flags = get_allowed_flags(node_kind)
    ports = get_ports(node_kind)

    return {
        "id": definition_id,
        "displayName": display_name,
        "symbol": symbol,
        "category": category,
        "nodeKind": node_kind,
        "docUrl": get_doc_url(definition_id),
        "initParams": parse_signature(obj),
        "inputPorts": ports["inputPorts"],
        "outputPorts": ports["outputPorts"],
        **flags,
    }


def build_torch_nn_module_definitions():
    definitions = []

    for name, obj in inspect.getmembers(nn):
        if name.startswith("_"):
            continue

        if not inspect.isclass(obj):
            continue

        module_path = getattr(obj, "__module__", "")

        if not module_path.startswith("torch.nn"):
            continue

        category = get_module_category(obj, name)

        if category == "Containers":
            continue

        node_kind = get_node_kind(category, name, is_function=False)

        definitions.append(
            make_definition(
                definition_id=f"torch.nn.{name}",
                display_name=name,
                symbol=f"torch.nn.{name}",
                category=category,
                node_kind=node_kind,
                obj=obj,
            )
        )

    return definitions


def build_functional_definitions():
    definitions = []

    for name, obj in inspect.getmembers(F):
        if name.startswith("_"):
            continue

        if not inspect.isfunction(obj):
            continue

        category = get_functional_category(name)
        node_kind = get_node_kind(category, name, is_function=True)

        definition = make_definition(
            definition_id=f"torch.nn.functional.{name}",
            display_name=name,
            symbol=f"torch.nn.functional.{name}",
            category=category,
            node_kind=node_kind,
            obj=obj,
        )

        filtered_params = []

        for param in definition["initParams"]:
            param_name = param.get("name", "")

            if param_name in FUNCTION_RUNTIME_INPUT_NAMES:
                continue

            filtered_params.append(param)

        definition["initParams"] = filtered_params

        definitions.append(definition)

    return definitions


def build_utils_definitions():
    definitions = []

    for name, obj in inspect.getmembers(nn_utils):
        if name.startswith("_"):
            continue

        if not inspect.isfunction(obj) and not inspect.isclass(obj):
            continue

        category = "Utilities"
        node_kind = "UtilityNode"

        definitions.append(
            make_definition(
                definition_id=f"torch.nn.utils.{name}",
                display_name=name,
                symbol=f"torch.nn.utils.{name}",
                category=category,
                node_kind=node_kind,
                obj=obj,
            )
        )

    return definitions


def build_custom_operation_definitions():
    custom_nodes = [
        {
            "id": "custom.Dataset",
            "displayName": "Dataset",
            "symbol": "custom.Dataset",
            "category": "Custom Graph Nodes",
            "nodeKind": "DatasetNode",
            "docUrl": "",
            "initParams": [
                {
                    "name": "dataset_name",
                    "type": "str",
                    "required": True,
                    "defaultValue": "MNIST",
                    "controlType": "dropdown",
                    "options": [
                        "MNIST",
                        "FashionMNIST",
                        "CIFAR10",
                        "ChihuahuaMuffin",
                        "Titanic",
                        "WeatherPrediction"
                    ],
                    "advanced": False,
                },
                {
                    "name": "input_shape",
                    "type": "list[int]",
                    "required": True,
                    "defaultValue": "[1, 784]",
                    "controlType": "text",
                    "options": [],
                    "advanced": False,
                }
            ],
            "inputPorts": [],
            "outputPorts": [
                {"name": "out", "portType": "Tensor"}
            ],
            "allowedInModelGraph": True,
            "allowedInSequential": False,
            "allowedInLossGraph": False,
            "allowedInTrainingGraph": True,
        },
        {
            "id": "custom.Container",
            "displayName": "Container",
            "symbol": "custom.Container",
            "category": "Custom Graph Nodes",
            "nodeKind": "ContainerNode",
            "docUrl": "",
            "initParams": [
                {
                    "name": "container_name",
                    "type": "str",
                    "required": True,
                    "defaultValue": "MyContainer",
                    "controlType": "text",
                    "options": [],
                    "advanced": False,
                }
            ],
            "inputPorts": [],
            "outputPorts": [],
            "allowedInModelGraph": True,
            "allowedInSequential": False,
            "allowedInLossGraph": False,
            "allowedInTrainingGraph": False,
        },
        {
            "id": "custom.Input",
            "displayName": "Input",
            "symbol": "custom.Input",
            "category": "Custom Graph Nodes",
            "nodeKind": "InputNode",
            "docUrl": "",
            "initParams": [
                {
                    "name": "input_name",
                    "type": "str",
                    "required": True,
                    "defaultValue": "x",
                    "controlType": "text",
                    "options": [],
                    "advanced": False,
                },
                {
                    "name": "shape",
                    "type": "list[int]",
                    "required": True,
                    "defaultValue": "[1, 784]",
                    "controlType": "text",
                    "options": [],
                    "advanced": False,
                }
            ],
            "inputPorts": [],
            "outputPorts": [
                {"name": "out", "portType": "Tensor"}
            ],
            "allowedInModelGraph": True,
            "allowedInSequential": False,
            "allowedInLossGraph": False,
            "allowedInTrainingGraph": False,
        },
        {
            "id": "custom.Output",
            "displayName": "Model Output",
            "symbol": "custom.Output",
            "category": "Custom Graph Nodes",
            "nodeKind": "OutputNode",
            "docUrl": "",
            "initParams": [
                {
                    "name": "output_name",
                    "type": "str",
                    "required": True,
                    "defaultValue": "Accuracy",
                    "controlType": "dropdown",
                    "options": [
                        "Accuracy",
                        "F1 Score",
                        "Confusion Matrix",
                        "Loss Graph",
                        "Logits",
                        "Probabilities",
                        "Predicted Class",
                        "Features",
                        "Embedding"
                    ],
                    "advanced": False,
                }
            ],
            "inputPorts": [
                {"name": "x", "portType": "Tensor"}
            ],
            "outputPorts": [
                {"name": "out", "portType": "Tensor"}
            ],
            "allowedInModelGraph": True,
            "allowedInSequential": False,
            "allowedInLossGraph": False,
            "allowedInTrainingGraph": True,
        },
        {
            "id": "custom.ResultOutput",
            "displayName": "Result Output",
            "symbol": "custom.ResultOutput",
            "category": "Result Outputs",
            "nodeKind": "ResultOutputNode",
            "docUrl": "",
            "initParams": [
                {
                    "name": "result_type",
                    "type": "str",
                    "required": True,
                    "defaultValue": "Accuracy",
                    "controlType": "dropdown",
                    "options": [
                        "Accuracy",
                        "F1 Score",
                        "Confusion Matrix",
                        "Loss Graph",
                        "Training Loss",
                        "Training Accuracy"
                    ],
                    "advanced": False,
                }
            ],
            "inputPorts": [
                {"name": "x", "portType": "Tensor"}
            ],
            "outputPorts": [],
            "allowedInModelGraph": True,
            "allowedInSequential": False,
            "allowedInLossGraph": False,
            "allowedInTrainingGraph": True,
        },
        {
            "id": "custom.FinalResult",
            "displayName": "Final Result",
            "symbol": "custom.FinalResult",
            "category": "Result Outputs",
            "nodeKind": "FinalResultNode",
            "docUrl": "",
            "initParams": [
                {
                    "name": "result_type",
                    "type": "str",
                    "required": True,
                    "defaultValue": "All Metrics",
                    "controlType": "dropdown",
                    "options": [
                        "All Metrics",
                        "Accuracy",
                        "F1 Score",
                        "Confusion Matrix"
                    ],
                    "advanced": False,
                }
            ],
            "inputPorts": [
                {"name": "x", "portType": "Tensor"}
            ],
            "outputPorts": [],
            "allowedInModelGraph": True,
            "allowedInSequential": False,
            "allowedInLossGraph": False,
            "allowedInTrainingGraph": True,
        },
        {
            "id": "custom.Add",
            "displayName": "Add",
            "symbol": "torch.add",
            "category": "Custom Tensor Operations",
            "nodeKind": "OperationNode",
            "docUrl": "",
            "initParams": [],
            "inputPorts": [
                {"name": "a", "portType": "Tensor"},
                {"name": "b", "portType": "Tensor"},
            ],
            "outputPorts": [
                {"name": "out", "portType": "Tensor"}
            ],
            "allowedInModelGraph": True,
            "allowedInSequential": False,
            "allowedInLossGraph": False,
            "allowedInTrainingGraph": False,
        },
        {
            "id": "custom.Cat",
            "displayName": "Cat",
            "symbol": "torch.cat",
            "category": "Custom Tensor Operations",
            "nodeKind": "OperationNode",
            "docUrl": "",
            "initParams": [
                {
                    "name": "dim",
                    "type": "int",
                    "required": False,
                    "defaultValue": "1",
                }
            ],
            "inputPorts": [
                {"name": "a", "portType": "Tensor"},
                {"name": "b", "portType": "Tensor"},
            ],
            "outputPorts": [
                {"name": "out", "portType": "Tensor"}
            ],
            "allowedInModelGraph": True,
            "allowedInSequential": False,
            "allowedInLossGraph": False,
            "allowedInTrainingGraph": False,
        },
    ]

    return custom_nodes


def build_node_library():
    definitions = []
    definitions.extend(build_custom_operation_definitions())
    definitions.extend(build_torch_nn_module_definitions())
    definitions.extend(build_functional_definitions())
    definitions.extend(build_utils_definitions())

    definitions.sort(key=lambda x: (x["category"], x["displayName"]))

    return definitions