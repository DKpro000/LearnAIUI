using System;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
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

    [Header("Player / Distributed Compute")]
    public string playerDisplayName = "";
    public bool submitFinalScoreToLeaderboard = true;
    public float trainingJobPollSeconds = 2f;
    public bool automaticallyContributeCompute = true;
    public float workerRestartDelaySeconds = 10f;
    public string bundledWorkerRelativePath = "ComputeWorker/NNBuilderWorker.exe";

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
    private string playerToken = "";
    private string playerId = "";
    private Process workerProcess;
    private bool applicationQuitting;
    private bool missingWorkerWasLogged;

    private const string PlayerTokenKey = "NNBuilder.PlayerToken";
    private const string PlayerIdKey = "NNBuilder.PlayerId";
    private const string PlayerNameKey = "NNBuilder.PlayerName";
    private const string PlayerServerKey = "NNBuilder.PlayerServer";

    private void Awake()
    {
        LoadServerConfiguration();
        if (PlayerPrefs.GetString(PlayerServerKey, "") == backendUrl)
        {
            playerToken = PlayerPrefs.GetString(PlayerTokenKey, "");
            playerId = PlayerPrefs.GetString(PlayerIdKey, "");
            playerDisplayName = PlayerPrefs.GetString(PlayerNameKey, playerDisplayName);
            if (!string.IsNullOrWhiteSpace(playerToken))
            {
                SaveWorkerConfiguration();
            }
        }
    }

    private void LoadServerConfiguration()
    {
        string configuredUrl = GetCommandLineServerUrl();
        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            string configPath = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName,
                "server-config.json"
            );
            if (File.Exists(configPath))
            {
                try
                {
                    ServerConnectionConfig config =
                        JsonConvert.DeserializeObject<ServerConnectionConfig>(
                            File.ReadAllText(configPath)
                        );
                    configuredUrl = config == null ? "" : config.serverUrl;
                }
                catch (Exception error)
                {
                    Debug.LogError(
                        "Could not read server-config.json: " + error.Message
                    );
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            Uri parsedUrl;
            if (
                Uri.TryCreate(configuredUrl.Trim(), UriKind.Absolute, out parsedUrl) &&
                (parsedUrl.Scheme == Uri.UriSchemeHttp ||
                 parsedUrl.Scheme == Uri.UriSchemeHttps)
            )
            {
                backendUrl = configuredUrl.Trim().TrimEnd('/');
            }
            else
            {
                Debug.LogError("Invalid configured server URL: " + configuredUrl);
            }
        }
    }

    private string GetCommandLineServerUrl()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (argument.StartsWith("--server-url="))
            {
                return argument.Substring("--server-url=".Length);
            }
            if (argument == "--server-url" && index + 1 < arguments.Length)
            {
                return arguments[index + 1];
            }
        }
        return "";
    }

    private IEnumerator Start()
    {
        while (!applicationQuitting && automaticallyContributeCompute)
        {
            if (string.IsNullOrWhiteSpace(playerToken))
            {
                yield return EnsurePlayerIdentity();
            }

            if (!string.IsNullOrWhiteSpace(playerToken) && !IsWorkerRunning())
            {
                StartBundledWorker();
            }

            yield return new WaitForSecondsRealtime(
                Mathf.Max(2f, workerRestartDelaySeconds)
            );
        }
    }

    private bool IsWorkerRunning()
    {
        if (workerProcess == null)
        {
            return false;
        }
        try
        {
            return !workerProcess.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void StartBundledWorker()
    {
        if (
            Application.platform != RuntimePlatform.WindowsPlayer &&
            Application.platform != RuntimePlatform.WindowsEditor
        )
        {
            return;
        }

        string workerPath = Path.Combine(
            Application.streamingAssetsPath,
            bundledWorkerRelativePath
        );
        if (!File.Exists(workerPath))
        {
            if (!missingWorkerWasLogged)
            {
                Debug.LogWarning(
                    "Bundled compute worker was not found: " + workerPath
                );
                missingWorkerWasLogged = true;
            }
            return;
        }

        string workerDirectory = Path.GetDirectoryName(workerPath);
        string torchCpuPath = Path.Combine(
            workerDirectory,
            "_internal",
            "torch",
            "lib",
            "torch_cpu.dll"
        );
        const long MinimumTorchCpuDllBytes = 100L * 1024L * 1024L;
        if (
            !File.Exists(torchCpuPath) ||
            new FileInfo(torchCpuPath).Length < MinimumTorchCpuDllBytes
        )
        {
            if (!missingWorkerWasLogged)
            {
                Debug.LogWarning(
                    "Bundled compute worker is incomplete; torch_cpu.dll is missing " +
                    "or truncated. " +
                    "Download and extract the complete worker release into " +
                    "Assets/StreamingAssets/ComputeWorker. Unity will continue " +
                    "without contributing compute. Missing file: " + torchCpuPath
                );
                missingWorkerWasLogged = true;
            }
            return;
        }

        missingWorkerWasLogged = false;
        string runtimeDirectory = Path.Combine(
            Application.persistentDataPath,
            "compute-worker-runtime"
        );
        Directory.CreateDirectory(runtimeDirectory);
        string configPath = Path.Combine(
            Application.persistentDataPath,
            "compute-worker.json"
        );
        string logPath = Path.Combine(runtimeDirectory, "compute-worker.log");

        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = workerPath;
        startInfo.Arguments =
            "--config \"" + configPath + "\" " +
            "--log-file \"" + logPath + "\"";
        startInfo.WorkingDirectory = runtimeDirectory;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
        startInfo.EnvironmentVariables["NN_BUILDER_RUNTIME_DIR"] = runtimeDirectory;
        startInfo.EnvironmentVariables["NN_BUILDER_DATA_DIR"] =
            Path.Combine(runtimeDirectory, "data");
        startInfo.EnvironmentVariables["NN_BUILDER_LOCAL_DATASET_DIR"] =
            Path.Combine(runtimeDirectory, "dataset");

        try
        {
            workerProcess = Process.Start(startInfo);
            Debug.Log(
                "Bundled compute worker started. Log: " + logPath
            );
        }
        catch (Exception error)
        {
            Debug.LogError("Could not start bundled compute worker: " + error.Message);
            workerProcess = null;
        }
    }

    private void StopBundledWorker()
    {
        if (workerProcess == null)
        {
            return;
        }
        try
        {
            if (!workerProcess.HasExited)
            {
                workerProcess.Kill();
            }
            workerProcess.Dispose();
        }
        catch
        {
            // The process may already have exited.
        }
        workerProcess = null;
    }

    private void OnApplicationQuit()
    {
        applicationQuitting = true;
        StopBundledWorker();
    }

    private void OnDestroy()
    {
        StopBundledWorker();
    }

    [ContextMenu("Reset Player Identity")]
    public void ResetPlayerIdentity()
    {
        StopBundledWorker();
        playerToken = "";
        playerId = "";
        PlayerPrefs.DeleteKey(PlayerTokenKey);
        PlayerPrefs.DeleteKey(PlayerIdKey);
        PlayerPrefs.DeleteKey(PlayerNameKey);
        PlayerPrefs.DeleteKey(PlayerServerKey);
        PlayerPrefs.Save();
        string configPath = Path.Combine(
            Application.persistentDataPath,
            "compute-worker.json"
        );
        if (File.Exists(configPath))
        {
            File.Delete(configPath);
        }
    }

    private IEnumerator EnsurePlayerIdentity()
    {
        if (!string.IsNullOrWhiteSpace(playerToken))
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(playerDisplayName))
        {
            playerDisplayName = "Player-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        for (int registrationAttempt = 0; registrationAttempt < 2; registrationAttempt++)
        {
            PlayerRegistrationRequest registration = new PlayerRegistrationRequest
            {
                displayName = playerDisplayName.Trim()
            };
            string json = JsonConvert.SerializeObject(registration);

            using (UnityWebRequest request = new UnityWebRequest(
                backendUrl + "/players/register",
                "POST"
            ))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                yield return request.SendWebRequest();

                if (request.responseCode == 409 && registrationAttempt == 0)
                {
                    string suffix = "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
                    string baseName = playerDisplayName.Trim();
                    int maximumBaseLength = 32 - suffix.Length;
                    if (baseName.Length > maximumBaseLength)
                    {
                        baseName = baseName.Substring(0, maximumBaseLength);
                    }
                    playerDisplayName = baseName + suffix;
                    Debug.LogWarning(
                        "Player name is already registered; retrying as " +
                        playerDisplayName + "."
                    );
                    continue;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    SetResultText(
                        "Player registration failed.\n" +
                        request.error + "\n" + request.downloadHandler.text
                    );
                    yield break;
                }

                PlayerRegistrationResponse response =
                    JsonConvert.DeserializeObject<PlayerRegistrationResponse>(
                        request.downloadHandler.text
                    );
                if (response == null || !response.success || response.player == null)
                {
                    SetResultText("Player registration returned an invalid response.");
                    yield break;
                }

                playerToken = response.player.token;
                playerId = response.player.playerId;
                playerDisplayName = response.player.displayName;
                PlayerPrefs.SetString(PlayerTokenKey, playerToken);
                PlayerPrefs.SetString(PlayerIdKey, playerId);
                PlayerPrefs.SetString(PlayerNameKey, playerDisplayName);
                PlayerPrefs.SetString(PlayerServerKey, backendUrl);
                PlayerPrefs.Save();
                SaveWorkerConfiguration();
                yield break;
            }
        }
    }

    private void SaveWorkerConfiguration()
    {
        try
        {
            string path = Path.Combine(
                Application.persistentDataPath,
                "compute-worker.json"
            );
            string json = JsonConvert.SerializeObject(
                new
                {
                    serverUrl = backendUrl,
                    playerToken = playerToken,
                    name = playerDisplayName + " computer"
                },
                Formatting.Indented
            );
            File.WriteAllText(path, json);
            Debug.Log("Compute worker configuration: " + path);
        }
        catch (Exception error)
        {
            Debug.LogWarning("Could not save compute worker configuration: " + error.Message);
        }
    }

    private void AddPlayerAuthorization(UnityWebRequest request)
    {
        if (!string.IsNullOrWhiteSpace(playerToken))
        {
            request.SetRequestHeader("Authorization", "Bearer " + playerToken);
        }
    }

    public IEnumerator TrainGraph(GraphData graph, GraphTrainSettings settings)
    {
        if (graph == null)
        {
            Debug.LogError("Cannot train null graph.");
            yield break;
        }

        yield return EnsurePlayerIdentity();
        if (string.IsNullOrWhiteSpace(playerToken))
        {
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

        for (int authenticationAttempt = 0; authenticationAttempt < 2; authenticationAttempt++)
        {
            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                AddPlayerAuthorization(request);

                yield return request.SendWebRequest();

                if (request.responseCode == 401 && authenticationAttempt == 0)
                {
                    Debug.LogWarning(
                        "The saved player token is no longer valid. " +
                        "Registering a new session and retrying once."
                    );
                    SetResultText("Player session expired. Registering again...");
                    ResetPlayerIdentity();
                    yield return EnsurePlayerIdentity();
                    if (string.IsNullOrWhiteSpace(playerToken))
                    {
                        yield break;
                    }
                    continue;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string responseBody = request.downloadHandler == null
                        ? ""
                        : request.downloadHandler.text;
                    string error =
                        "Train graph request failed: " + request.error +
                        (string.IsNullOrWhiteSpace(responseBody)
                            ? ""
                            : "\n" + responseBody);
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

                if (response != null && response.queued)
                {
                    SetResultText(
                        "Training queued.\n" +
                        "Active worker computers: " + response.activeWorkers
                    );
                    yield return PollTrainingJob(response.jobId);
                }
                else
                {
                    ShowTrainResult(response);
                }
                yield break;
            }
        }
    }

    private IEnumerator PollTrainingJob(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            SetResultText("Training queue returned no job ID.");
            yield break;
        }

        while (true)
        {
            yield return new WaitForSecondsRealtime(
                Mathf.Max(0.5f, trainingJobPollSeconds)
            );
            using (UnityWebRequest request = UnityWebRequest.Get(
                backendUrl + "/training_jobs/" + UnityWebRequest.EscapeURL(jobId)
            ))
            {
                AddPlayerAuthorization(request);
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    SetResultText("Training status request failed:\n" + request.error);
                    continue;
                }

                TrainingJobResponse response =
                    JsonConvert.DeserializeObject<TrainingJobResponse>(
                        request.downloadHandler.text
                    );
                if (response == null || response.job == null)
                {
                    SetResultText("Training status response is invalid.");
                    continue;
                }

                if (response.job.status == "completed")
                {
                    ShowTrainResult(response.job.result);
                    yield break;
                }
                if (response.job.status == "failed")
                {
                    SetResultText("Training failed.\n" + response.job.error);
                    yield break;
                }

                SetResultText(
                    "Training " + response.job.status +
                    " (attempt " + response.job.attempts + ")"
                );
            }
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

        yield return EnsurePlayerIdentity();
        if (string.IsNullOrWhiteSpace(playerToken))
        {
            yield break;
        }

        GraphFinalEvaluateRequest request = new GraphFinalEvaluateRequest();
        request.projectName = "UnityNodeGraph";
        request.graph = GraphExportUtility.ExportGraph(graph);
        request.training = settings == null ? new GraphTrainSettings() : settings;
        request.checkpointId = request.training.checkpointId;
        request.submitToLeaderboard = submitFinalScoreToLeaderboard;

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
            AddPlayerAuthorization(webRequest);

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
            string errorMessage = "Final evaluate error.\n";

            if (response.errors != null)
            {
                foreach (string error in response.errors)
                {
                    errorMessage += "- " + error + "\n";
                }
            }

            SetResultText(errorMessage);
            return;
        }

        string message =
            "Final evaluate success.\n" +
            "Dataset: " + response.dataset;

        if (response.finalMetrics != null)
        {
            message +=
                "\nAccuracy: " + response.finalMetrics.accuracy.ToString("0.0000") +
                "\nMacro F1: " + response.finalMetrics.f1_macro.ToString("0.0000");
        }
        if (response.leaderboardScore != null)
        {
            message +=
                "\nLeaderboard best: " +
                response.leaderboardScore.personalBestF1Score.ToString("0.0000");
            if (response.leaderboardScore.isPersonalBest)
            {
                message += " (new personal best)";
            }
        }
        SetResultText(message);

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
        yield return EnsurePlayerIdentity();
        if (string.IsNullOrWhiteSpace(playerToken))
        {
            onLoaded?.Invoke(new List<CheckpointMetadata>());
            yield break;
        }

        string url = backendUrl + "/checkpoints";

        if (!string.IsNullOrWhiteSpace(datasetName))
        {
            url += "?dataset_name=" + UnityWebRequest.EscapeURL(datasetName);
        }

        using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
        {
            AddPlayerAuthorization(webRequest);
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

    public void ShowLeaderboard()
    {
        StartCoroutine(LoadLeaderboard(dataset, ShowLeaderboardResult));
    }

    public void ShowLeaderboard(string datasetName)
    {
        StartCoroutine(LoadLeaderboard(datasetName, ShowLeaderboardResult));
    }

    public IEnumerator LoadLeaderboard(
        string datasetName,
        Action<LeaderboardData> onLoaded
    )
    {
        yield return EnsurePlayerIdentity();
        if (string.IsNullOrWhiteSpace(playerToken))
        {
            onLoaded?.Invoke(null);
            yield break;
        }

        string selectedDataset = string.IsNullOrWhiteSpace(datasetName)
            ? "MNIST"
            : datasetName;
        string url =
            backendUrl + "/leaderboard?dataset=" +
            UnityWebRequest.EscapeURL(selectedDataset) + "&limit=50";

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            AddPlayerAuthorization(request);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                SetResultText("Leaderboard request failed:\n" + request.error);
                onLoaded?.Invoke(null);
                yield break;
            }

            LeaderboardResponse response =
                JsonConvert.DeserializeObject<LeaderboardResponse>(
                    request.downloadHandler.text
                );
            if (response == null || !response.success)
            {
                SetResultText("Leaderboard response is invalid.");
                onLoaded?.Invoke(null);
                yield break;
            }
            onLoaded?.Invoke(response.leaderboard);
        }
    }

    private void ShowLeaderboardResult(LeaderboardData leaderboard)
    {
        if (leaderboard == null)
        {
            return;
        }
        StringBuilder message = new StringBuilder();
        message.AppendLine("Leaderboard — " + leaderboard.dataset);
        if (leaderboard.callerRank.HasValue)
        {
            message.AppendLine("Your rank: #" + leaderboard.callerRank.Value);
        }
        if (leaderboard.entries == null || leaderboard.entries.Count == 0)
        {
            message.AppendLine("No verified scores yet.");
        }
        else
        {
            foreach (LeaderboardEntry entry in leaderboard.entries)
            {
                message.AppendLine(
                    "#" + entry.rank + "  " + entry.displayName +
                    "  F1 " + entry.f1Score.ToString("0.0000")
                );
            }
        }
        SetResultText(message.ToString());
    }
}
