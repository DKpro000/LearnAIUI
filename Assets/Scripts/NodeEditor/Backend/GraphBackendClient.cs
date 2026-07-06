using System;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

public class GraphBackendClient : MonoBehaviour
{
    [Header("Backend")]
    public string backendUrl = "http://127.0.0.1:8000";

    [Header("Optional UI")]
    public TMP_Text resultText;

    [Header("Training Settings")]
    public string dataset = "MNIST";
    public int epochs = 1;
    public int batchSize = 64;
    public float learningRate = 0.001f;
    public string optimizer = "Adam";
    public string loss = "CrossEntropyLoss";
    public int maxTrainSamples = 2000;
    public string device = "auto";

    [Header("Graph Editor")]
    public GraphEditorController graphEditorController;

    public void SetSelectedCheckpointId(string checkpointId)
    {
        selectedCheckpointId = checkpointId;
    }

    private string selectedCheckpointId = "";
    private List<CheckpointMetadata> cachedCheckpoints = new List<CheckpointMetadata>();

    public IEnumerator TrainGraph(GraphData graph, GraphTrainSettings settings)
    {
        if (graph == null)
        {
            Debug.LogError("Cannot train null graph.");
            yield break;
        }

        GraphExportData exportGraph = GraphExportUtility.ExportGraph(graph);

        GraphTrainRequest requestData = new GraphTrainRequest
        {
            projectName = "UnityNodeProject",
            graph = exportGraph,
            training = settings
        };

        string json = JsonConvert.SerializeObject(
            requestData,
            Formatting.Indented
        );

        Debug.Log("Sending train graph JSON:\n" + json);

        string url = backendUrl + "/train_graph";

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                string error = "Train graph request failed: " + request.error;
                Debug.LogError(error);
                SetResultText(error);
                yield break;
            }

            string responseJson = request.downloadHandler.text;
            Debug.Log("Train graph response:\n" + responseJson);

            GraphTrainResponse response = null;

            try
            {
                response = JsonConvert.DeserializeObject<GraphTrainResponse>(responseJson);
            }
            catch (Exception e)
            {
                string error = "Failed to parse train response: " + e.Message;
                Debug.LogError(error);
                SetResultText(error);
                yield break;
            }

            ShowTrainResult(response);
        }
    }

    public IEnumerator ValidateGraph(GraphData graph)
    {
        if (graph == null)
        {
            Debug.LogError("Cannot validate null graph.");
            yield break;
        }

        GraphExportData exportGraph = GraphExportUtility.ExportGraph(graph);

        GraphValidationRequest requestData = new GraphValidationRequest
        {
            projectName = "UnityNodeProject",
            graph = exportGraph
        };

        string json = JsonConvert.SerializeObject(
            requestData,
            Formatting.Indented
        );

        Debug.Log("Sending graph JSON:\n" + json);

        string url = backendUrl + "/validate_graph";

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                string error = "Validate graph request failed: " + request.error;
                Debug.LogError(error);
                SetResultText(error);
                yield break;
            }

            string responseJson = request.downloadHandler.text;
            Debug.Log("Validate graph response:\n" + responseJson);

            GraphValidationResponse response = null;

            try
            {
                response = JsonConvert.DeserializeObject<GraphValidationResponse>(responseJson);
            }
            catch (Exception e)
            {
                string error = "Failed to parse validation response: " + e.Message;
                Debug.LogError(error);
                SetResultText(error);
                yield break;
            }

            ShowValidationResult(response);
        }
    }

    private void ShowValidationResult(GraphValidationResponse response)
    {
        if (response == null)
        {
            SetResultText("Validation response is null.");
            return;
        }

        string message = "";

        if (response.success)
        {
            message += "Graph validation passed.\n";
        }
        else
        {
            message += "Graph validation failed.\n";
        }

        if (response.errors != null && response.errors.Count > 0)
        {
            message += "\nErrors:\n";

            foreach (string error in response.errors)
            {
                message += "- " + error + "\n";
            }
        }

        if (response.warnings != null && response.warnings.Count > 0)
        {
            message += "\nWarnings:\n";

            foreach (string warning in response.warnings)
            {
                message += "- " + warning + "\n";
            }
        }

        if (response.executionOrder != null && response.executionOrder.Count > 0)
        {
            message += "\nExecution order:\n";

            for (int i = 0; i < response.executionOrder.Count; i++)
            {
                ExecutionOrderItem item = response.executionOrder[i];
                message += $"{i + 1}. {item.title} ({item.nodeKind})\n";
            }
        }

        if (response.success)
        {
            Debug.Log(message);
        }
        else
        {
            Debug.LogError(message);
        }

        SetResultText(message);
    }

    private void SetResultText(string text)
    {
        if (resultText != null)
        {
            resultText.text = text;
        }
    }

    public IEnumerator DryRunGraph(GraphData graph)
    {
        if (graph == null)
        {
            Debug.LogError("Cannot dry run null graph.");
            yield break;
        }

        GraphExportData exportGraph = GraphExportUtility.ExportGraph(graph);

        GraphValidationRequest requestData = new GraphValidationRequest
        {
            projectName = "UnityNodeProject",
            graph = exportGraph
        };

        string json = JsonConvert.SerializeObject(
            requestData,
            Formatting.Indented
        );

        Debug.Log("Sending dry run graph JSON:\n" + json);

        string url = backendUrl + "/dry_run_graph";

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                string error = "Dry run graph request failed: " + request.error;
                Debug.LogError(error);
                SetResultText(error);
                yield break;
            }

            string responseJson = request.downloadHandler.text;
            Debug.Log("Dry run graph response:\n" + responseJson);

            GraphDryRunResponse response = null;

            try
            {
                response = JsonConvert.DeserializeObject<GraphDryRunResponse>(responseJson);
            }
            catch (Exception e)
            {
                string error = "Failed to parse dry run response: " + e.Message;
                Debug.LogError(error);
                SetResultText(error);
                yield break;
            }

            ShowDryRunResult(response);
        }
    }

    private void ShowDryRunResult(GraphDryRunResponse response)
    {
        if (response == null)
        {
            SetResultText("Dry run response is null.");
            return;
        }

        string message = "";

        if (response.success)
        {
            message += "Dry run passed.\n";
        }
        else
        {
            message += "Dry run failed.\n";
        }

        if (response.inputShape != null && response.inputShape.Count > 0)
        {
            message += "\nInput shape: [" + string.Join(", ", response.inputShape) + "]\n";
        }

        if (response.outputShape != null && response.outputShape.Count > 0)
        {
            message += "Output shape: [" + string.Join(", ", response.outputShape) + "]\n";
        }

        if (response.errors != null && response.errors.Count > 0)
        {
            message += "\nErrors:\n";

            foreach (string error in response.errors)
            {
                message += "- " + error + "\n";
            }
        }

        if (response.warnings != null && response.warnings.Count > 0)
        {
            message += "\nWarnings:\n";

            foreach (string warning in response.warnings)
            {
                message += "- " + warning + "\n";
            }
        }

        if (response.executionOrder != null && response.executionOrder.Count > 0)
        {
            message += "\nExecution order:\n";

            for (int i = 0; i < response.executionOrder.Count; i++)
            {
                ExecutionOrderItem item = response.executionOrder[i];
                message += $"{i + 1}. {item.title} ({item.nodeKind})\n";
            }
        }

        if (!string.IsNullOrEmpty(response.modelSummary))
        {
            message += "\nModel summary:\n";
            message += response.modelSummary + "\n";
        }

        if (response.success)
        {
            Debug.Log(message);
        }
        else
        {
            Debug.LogError(message);
        }

        SetResultText(message);
    }

    private void ShowTrainResult(GraphTrainResponse response)
    {
        if (response == null)
        {
            SetResultText("Train error: response is null.");
            return;
        }

        if (!response.success)
        {
            string message = "Train error.\n";

            if (response.errors != null)
            {
                foreach (string error in response.errors)
                {
                    message += "- " + error + "\n";
                }
            }

            SetResultText(message);
            return;
        }

        if (response.success && !string.IsNullOrWhiteSpace(response.checkpointId))
        {
            selectedCheckpointId = response.checkpointId;
        }

        SetResultText(
            "Train success.\n" +
            "Dataset: " + response.dataset + "\n" +
            "Saved weight: " +
            (response.checkpointMetadata == null
                ? response.checkpointId
                : response.checkpointMetadata.weightName)
        );

        if (graphEditorController != null &&
            response.resultNodes != null &&
            response.resultNodes.Count > 0)
        {
            graphEditorController.ApplyResultNodeOutputs(response.resultNodes);
        }
    }

    public IEnumerator FinalEvaluateGraph(GraphData graph, GraphTrainSettings settings)
    {
        if (graph == null)
        {
            Debug.LogError("Cannot final evaluate null graph.");
            yield break;
        }

        GraphFinalEvaluateRequest request = new GraphFinalEvaluateRequest();
        request.projectName = "UnityNodeGraph";
        request.graph = GraphExportUtility.ExportGraph(graph);
        request.training = settings == null ? new GraphTrainSettings() : settings;
        request.checkpointId = request.training.checkpointId;

        if (string.IsNullOrWhiteSpace(request.checkpointId))
        {
            request.checkpointId = selectedCheckpointId;
        }

        if (string.IsNullOrWhiteSpace(request.checkpointId))
        {
            SetResultText("Final evaluate error.\nNo saved weight selected.");
            yield break;
        }

        string json = JsonConvert.SerializeObject(request);

        string url = backendUrl + "/final_evaluate_graph";

        using (UnityWebRequest webRequest = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

            webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
            webRequest.downloadHandler = new DownloadHandlerBuffer();
            webRequest.SetRequestHeader("Content-Type", "application/json");

            yield return webRequest.SendWebRequest();

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                SetResultText("Final evaluate request failed:\n" + webRequest.error);
                yield break;
            }

            string responseText = webRequest.downloadHandler.text;

            GraphFinalEvaluateResponse response =
                JsonConvert.DeserializeObject<GraphFinalEvaluateResponse>(responseText);

            ShowFinalEvaluateResult(response);
        }
    }

    private void ShowFinalEvaluateResult(GraphFinalEvaluateResponse response)
    {
        if (response == null)
        {
            SetResultText("Final evaluate error: response is null.");
            return;
        }

        if (!response.success)
        {
            string message = "Final evaluate error.\n";

            if (response.errors != null)
            {
                foreach (string error in response.errors)
                {
                    message += "- " + error + "\n";
                }
            }

            SetResultText(message);
            return;
        }

        SetResultText(
            "Final evaluate success.\n" +
            "Dataset: " + response.dataset
        );

        if (graphEditorController != null &&
            response.finalResultNodes != null &&
            response.finalResultNodes.Count > 0)
        {
            graphEditorController.ApplyResultNodeOutputs(response.finalResultNodes);
        }
    }

    public IEnumerator LoadCheckpoints(
        string datasetName,
        Action<List<CheckpointMetadata>> onLoaded
    )
    {
        string url = backendUrl + "/checkpoints";

        if (!string.IsNullOrWhiteSpace(datasetName))
        {
            url += "?dataset_name=" + UnityWebRequest.EscapeURL(datasetName);
        }

        using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
        {
            yield return webRequest.SendWebRequest();

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                SetResultText("Load checkpoints failed:\n" + webRequest.error);
                onLoaded?.Invoke(new List<CheckpointMetadata>());
                yield break;
            }

            CheckpointListResponse response =
                JsonConvert.DeserializeObject<CheckpointListResponse>(
                    webRequest.downloadHandler.text
                );

            if (response == null || !response.success)
            {
                SetResultText("Load checkpoints failed.");
                onLoaded?.Invoke(new List<CheckpointMetadata>());
                yield break;
            }

            cachedCheckpoints = response.checkpoints;
            onLoaded?.Invoke(cachedCheckpoints);
        }
    }
}