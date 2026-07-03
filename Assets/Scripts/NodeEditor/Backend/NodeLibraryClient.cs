using System;
using System.Collections;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

public class NodeLibraryClient : MonoBehaviour
{
    [Header("Backend")]
    public string backendUrl = "http://127.0.0.1:8000";

    public IEnumerator FetchNodeLibrary(Action<NodeLibraryResponse> onSuccess)
    {
        string url = backendUrl + "/node_library";

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Failed to fetch node library: " + request.error);
                yield break;
            }

            string json = request.downloadHandler.text;

            NodeLibraryResponse response = null;

            try
            {
                response = JsonConvert.DeserializeObject<NodeLibraryResponse>(json);
            }
            catch (Exception e)
            {
                Debug.LogError("Failed to parse node library JSON: " + e.Message);
                yield break;
            }

            if (response == null || !response.success)
            {
                Debug.LogError("Invalid node library response.");
                yield break;
            }

            onSuccess?.Invoke(response);
        }
    }
}