using System;
using System.Collections.Generic;

[Serializable]
public class GraphValidationRequest
{
    public string projectName;
    public GraphExportData graph;
}

[Serializable]
public class GraphExportData
{
    public string graphId;
    public string displayName;

    public List<NodeExportData> nodes = new List<NodeExportData>();
    public List<EdgeExportData> edges = new List<EdgeExportData>();
}

[Serializable]
public class NodeExportData
{
    public string nodeId;

    public string definitionId;
    public string title;
    public string symbol;
    public string category;
    public string nodeKind;

    public PositionExportData position;

    public List<NodeParam> parameters = new List<NodeParam>();
    public List<NodePortData> inputPorts = new List<NodePortData>();
    public List<NodePortData> outputPorts = new List<NodePortData>();

    public GraphExportData innerGraph;
}

[Serializable]
public class EdgeExportData
{
    public string edgeId;

    public string fromNodeId;
    public string fromPortName;

    public string toNodeId;
    public string toPortName;
}

[Serializable]
public class PositionExportData
{
    public float x;
    public float y;

    public PositionExportData(float x, float y)
    {
        this.x = x;
        this.y = y;
    }
}

[Serializable]
public class GraphValidationResponse
{
    public bool success;
    public List<string> errors = new List<string>();
    public List<string> warnings = new List<string>();
    public List<ExecutionOrderItem> executionOrder = new List<ExecutionOrderItem>();
}

[Serializable]
public class ExecutionOrderItem
{
    public string nodeId;
    public string title;
    public string nodeKind;
    public string symbol;
}

[Serializable]
public class GraphDryRunResponse
{
    public bool success;
    public List<string> errors = new List<string>();
    public List<string> warnings = new List<string>();

    public string modelSummary;

    public List<int> inputShape = new List<int>();
    public List<int> outputShape = new List<int>();

    public List<ExecutionOrderItem> executionOrder = new List<ExecutionOrderItem>();
}

[Serializable]
public class GraphTrainSettings
{
    public string dataset = "MNIST";
    public int epochs = 1;
    public int batchSize = 64;
    public float learningRate = 0.001f;
    public string optimizer = "Adam";
    public string loss = "CrossEntropyLoss";
    public int maxTrainSamples = 2000;
    public string device = "auto";

    public string modelName = "UnnamedModel";
    public string weightName = "";
    public string checkpointId = "";
}

[Serializable]
public class GraphTrainRequest
{
    public string projectName;
    public GraphExportData graph;
    public GraphTrainSettings training;
}

[Serializable]
public class GraphTrainHistory
{
    public List<float> trainLoss = new List<float>();
    public List<float> trainAcc = new List<float>();
}

[Serializable]
public class GraphTrainResponse
{
    public bool success;
    public List<string> errors = new List<string>();
    public List<string> warnings = new List<string>();

    public GraphTrainHistory history = new GraphTrainHistory();

    public List<ResultNodeResponse> resultNodes = new List<ResultNodeResponse>();

    public string checkpointId;
    public string checkpointPath;
    public CheckpointMetadata checkpointMetadata;

    public int numClasses;

    public string modelSummary;
    public string device;
    public string dataset;
    public int epochs;
    public bool queued;
    public string jobId;
    public string jobStatus;
    public string executionMode;
    public int activeWorkers;
}

[Serializable]
public class ResultNodeResponse
{
    public string nodeId;
    public string title;
    public string resultType;
    public string text;
}

[Serializable]
public class GraphFinalEvaluateRequest
{
    public string projectName;
    public GraphExportData graph;
    public GraphTrainSettings training;
    public string checkpointId;
    public bool submitToLeaderboard = true;
}

[Serializable]
public class FinalMetrics
{
    public float accuracy;
    public float f1_macro;
    public int total_samples;
    public List<List<int>> confusion_matrix = new List<List<int>>();
}

[Serializable]
public class LeaderboardScore
{
    public string evaluationId;
    public string challengeId;
    public string playerId;
    public string season;
    public string dataset;
    public string checkpointId;
    public string modelName;
    public float f1Score;
    public string recordedAt;
    public bool isPersonalBest;
    public float personalBestF1Score;
}

[Serializable]
public class GraphFinalEvaluateResponse
{
    public bool success;
    public List<string> errors = new List<string>();
    public List<string> warnings = new List<string>();

    public string checkpointPath;
    public string dataset;
    public int numClasses;
    public string checkpointId;
    public CheckpointMetadata checkpointMetadata;

    public FinalMetrics finalMetrics;
    public LeaderboardScore leaderboardScore;
    public List<ResultNodeResponse> finalResultNodes = new List<ResultNodeResponse>();
}

[Serializable]
public class CheckpointMetadata
{
    public string checkpointId;
    public string modelName;
    public string weightName;
    public string datasetName;
    public string savedAt;
    public string checkpointPath;
    public int numClasses;
}

[Serializable]
public class CheckpointListResponse
{
    public bool success;
    public List<CheckpointMetadata> checkpoints = new List<CheckpointMetadata>();
    public List<string> errors = new List<string>();
}

[Serializable]
public class AccountRegistrationRequest
{
    public string email;
    public string displayName;
    public string password;
    public string confirmPassword;
}

[Serializable]
public class AccountLoginRequest
{
    public string email;
    public string password;
}

[Serializable]
public class ServerConnectionConfig
{
    public string serverUrl;
}

[Serializable]
public class PlayerIdentity
{
    public string playerId;
    public string email;
    // Retained only for sessions created by an older username-based server.
    public string username;
    public string displayName;
    public string token;
    public string createdAt;
    public string sessionExpiresAt;
}

[Serializable]
public class AccountResponse
{
    public bool success;
    public PlayerIdentity player;
    public List<AccountApiError> errors = new List<AccountApiError>();
}

[Serializable]
public class AccountApiError
{
    public string code;
    public string message;
}

[Serializable]
public class AccountErrorResponse
{
    public bool success;
    public List<AccountApiError> errors = new List<AccountApiError>();
}

[Serializable]
public class TrainingJob
{
    public string jobId;
    public string status;
    public int attempts;
    public GraphTrainResponse result;
    public string error;
    public string createdAt;
    public string updatedAt;
}

[Serializable]
public class TrainingJobResponse
{
    public bool success;
    public TrainingJob job;
    public List<string> errors = new List<string>();
}

[Serializable]
public class LeaderboardEntry
{
    public int rank;
    public string playerId;
    public string displayName;
    public float f1Score;
    public string checkpointId;
    public string modelName;
    public string achievedAt;
}

[Serializable]
public class LeaderboardData
{
    public string challengeId;
    public string season;
    public string dataset;
    public string metric;
    public int totalPlayers;
    public int? callerRank;
    public List<LeaderboardEntry> entries = new List<LeaderboardEntry>();
}

[Serializable]
public class LeaderboardResponse
{
    public bool success;
    public LeaderboardData leaderboard;
    public List<string> errors = new List<string>();
}
