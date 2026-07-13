# Neural Network Builder server

The project now supports server training, opt-in player-computer workers, and a
server-verified macro-F1 leaderboard.

## Compute behavior

- With zero or one active player worker, `/train_graph` runs on the server.
- With two or more active workers belonging to different players, training is
  queued and leased to an available player computer.
- A worker sends a heartbeat while training. Expired leases are retried up to
  three times.
- If the active worker count falls below two while jobs are queued, the server
  automatically processes those jobs.
- Each worker trains a complete model. The system does not split one model
  across several internet-connected computers because synchronization would
  normally cost more than it saves for this project.
- Final evaluation always runs on the server. A worker cannot directly choose
  the leaderboard score.

## Server setup

Create the Python environment and install the packages in `requirements.txt`.
For a CUDA build, install the PyTorch build that matches the server's CUDA
driver using the official PyTorch instructions.

For a local-network test in PowerShell:

```powershell
$env:SERVER_DATA_DIR = "C:\NNBuilderData"
$env:PLAYER_TOKEN_PEPPER = "replace-with-a-long-random-secret"
.\.venv\Scripts\python.exe -m uvicorn app:app --host 0.0.0.0 --port 8000 --workers 1
```

Use one Uvicorn worker. The process contains a single local GPU fallback queue,
and multiple Uvicorn processes would create competing fallback workers.

For a compiled Unity build, place `server-config.json` beside the game `.exe`:

```json
{
  "serverUrl": "http://192.168.1.20:8000"
}
```

Replace the example with the server computer's LAN or HTTPS address. You can
also launch the game with `--server-url https://server.example`. The command-line
value has priority over `server-config.json`, which has priority over the Unity
Inspector value. `127.0.0.1` only works on the server computer.

For internet play, put TLS and authentication-aware rate limiting in front of
Uvicorn or use a private VPN. Do not forward an unencrypted Uvicorn port to the
public internet.

## Build the automatic player worker

The default bundled worker uses CPU-only PyTorch so it works on all Windows
player computers without Python, CUDA, or an NVIDIA GPU. Generate and copy it
into Unity before making a Windows player build:

```powershell
.\build_worker.ps1 -UnityProjectPath "D:\folders\Unity\Unity_Prj\My project"
```

The generated folder is approximately 486 MB and is copied to
`Assets/StreamingAssets/ComputeWorker`. Unity automatically includes
`StreamingAssets` in the Windows build.

Distribute the complete Unity build folder, not only the `.exe`; Unity always
requires its `_Data` folder and bundled libraries. Players only need to launch
the Unity `.exe`. They do not install or start Python.

To package the worker, build the Windows player, and write the external server
configuration in one command, first close the Unity Editor and run:

```powershell
.\build_unity_windows.ps1 `
  -ServerUrl "http://192.168.1.20:8000" `
  -UnityProjectPath "D:\folders\Unity\Unity_Prj\My project"
```

The complete distributable is created under
`My project/Builds/NNBuilder` by default.

At runtime Unity automatically:

1. registers the player;
2. writes `compute-worker.json` under `Application.persistentDataPath`;
3. launches `StreamingAssets/ComputeWorker/NNBuilderWorker.exe` invisibly;
4. restarts the worker if it exits unexpectedly; and
5. stops it when Unity closes.

The worker stores downloaded torchvision data, temporary checkpoints, and its
log under `Application.persistentDataPath/compute-worker-runtime`. The bundled
worker advertises MNIST, FashionMNIST, and CIFAR10 automatically. It advertises
a local dataset only when that dataset folder exists under
`compute-worker-runtime/dataset`, so unsupported jobs remain queued for another
worker or fall back to the server.

Set `GraphBackendClient.automaticallyContributeCompute` to false before building
if you want an edition that does not launch the worker. Compute contribution
uses the player's CPU, memory, storage, electricity, and network connection, so
the released game should explain this behavior clearly.

## Unity behavior

`GraphBackendClient` now:

- registers and remembers the player;
- loads the server URL from an external file or command line;
- automatically starts and monitors the bundled worker;
- authenticates training, checkpoint, evaluation, and leaderboard requests;
- polls queued training jobs until completion;
- submits final server-evaluated macro-F1 scores;
- displays final accuracy/F1 and personal-best information; and
- exposes `ShowLeaderboard()` for a Unity button.

To add a leaderboard button, connect its `On Click` event to
`GraphEditorController.ShowLeaderboard`. The current result text is used to
display the ranked list.

## API summary

- `POST /players/register`
- `POST /train_graph`
- `GET /training_jobs/{job_id}`
- `POST /compute/workers/register`
- `POST /compute/workers/heartbeat`
- `POST /compute/jobs/claim`
- `POST /compute/jobs/{job_id}/heartbeat`
- `POST /compute/jobs/{job_id}/complete`
- `POST /final_evaluate_graph`
- `GET /leaderboard?dataset=MNIST`
- `GET /compute/status`

Worker and player tokens are secrets. Do not commit `compute-worker.json` or
send it to another person.

## Verification and limits

The coordinator validates node definitions, parameter sizes, epochs, batch
size, learning rate, graph size, input size, model parameter count, checkpoint
size, tensor types, tensor shapes, and finite model weights. Uploaded PyTorch
files are loaded with `weights_only=True`, then strictly matched to the submitted
graph before the server stores them.

Server evaluation proves that the submitted weights achieve the recorded score
on the server's dataset. It cannot prove that the player respected a particular
training method or did not obtain the test data. For a serious competition,
keep a separate test set only on the server and distribute only training data to
workers.

Leaderboards are separated by dataset and `LEADERBOARD_SEASON`. Change the
season whenever the held-out data, preprocessing, or metric definition changes.

## Tests

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```
