# Neural Network Builder server

The project supports email/password accounts, server training, opt-in
player-computer workers, and a server-verified macro-F1 leaderboard.

## Security architecture

The FastAPI process is split into two code-level planes while remaining simple
to deploy as one server process:

- The **control plane** owns all databases, authenticates Unity account
  sessions, validates graph requests, records validation receipts, assigns
  jobs, accepts verified results, and updates the leaderboard.
- The **worker plane** is a database-free HTTP transport. It can register and
  authenticate workers and relay heartbeat, claim, complete, and fail messages,
  but every state change is delegated to the control plane.
- A training job cannot be claimed unless its stored payload still matches the
  control plane's validation hash. Worker uploads are checked again before the
  control plane accepts them.
- Final F1 evaluation and every leaderboard write occur only in the control
  plane on the server.

The two planes intentionally share one Uvicorn process and port. This gives a
clear authority boundary without requiring the project owner to deploy a
second service. Canonical worker endpoints use `/worker-plane`; hidden
`/compute` aliases temporarily keep older packaged workers compatible.

The email address is currently the unique login identifier. The server does
not yet send a verification email or password-reset email; those require a
configured mail provider and verification-token workflow.

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

`PLAYER_TOKEN_PEPPER` must be at least 32 characters. Keep it and
`SERVER_DATA_DIR` unchanged across server restarts. Account session tokens are
hashed with the pepper and stored in the database; changing either setting
makes previously saved Unity sessions invalid. Restore the original pepper to
preserve account access and leaderboard ownership.
Setting an environment variable with `$env:` affects only the current
PowerShell process, so set it again in every new server terminal or configure a
persistent user/system environment variable.

Use one Uvicorn worker. The process contains a single local GPU fallback queue,
and multiple Uvicorn processes would create competing fallback workers.

Existing SQLite files are migrated in place on startup. Back up
`SERVER_DATA_DIR` before the first start after this update. Legacy anonymous
tokens are not formal account sessions, so users must register an email,
display name, password, and matching confirmation password. Existing anonymous
scores stay visible but are not automatically
reassigned to a new account.

Accounts created by the short-lived username-based version have no email value
that the server can safely guess. An already saved session remains usable, but
after logout that player must register a new email-based account unless an
administrator performs a deliberate account migration.

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

Do not add that generated folder to ordinary Git tracking. In particular,
`_internal/torch/lib/torch_cpu.dll` is roughly 305 MB, which exceeds GitHub's
normal 100 MB per-file limit. A clone containing the `.meta` file but not the
DLL fails with PyTorch `WinError 126`.

For friends who need to open the Unity source project, create a complete worker
archive and upload it as a GitHub Release asset:

```powershell
.\package_worker_release.ps1 -UnityProjectPath "D:\folders\Unity\Unity_Prj\My project"
```

After cloning the Unity project, they download the release ZIP and extract it
into `Assets/StreamingAssets`. The final required path is
`Assets/StreamingAssets/ComputeWorker/_internal/torch/lib/torch_cpu.dll`.
They do not need Python. If they do not install the optional worker bundle,
Unity still works and the server handles training, but their computer does not
contribute compute.

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

1. shows separate login and registration views when there is no valid saved
   session;
2. remembers only the returned session token, never the password;
3. writes `compute-worker.json` under `Application.persistentDataPath` after
   login;
4. launches `StreamingAssets/ComputeWorker/NNBuilderWorker.exe` invisibly;
5. restarts the worker if it exits unexpectedly; and
6. stops it on logout or when Unity closes.

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

- creates a runtime login/register screen without scene or prefab setup;
- validates and remembers an account session, and supports logout;
- loads the server URL from an external file or command line;
- starts and monitors the bundled worker only after login;
- authenticates training, checkpoint, evaluation, and leaderboard requests;
- polls queued training jobs until completion;
- submits final server-evaluated macro-F1 scores;
- displays final accuracy/F1 and personal-best information; and
- exposes `ShowLeaderboard()` for a Unity button.

To add a leaderboard button, connect its `On Click` event to
`GraphEditorController.ShowLeaderboard`. The current result text is used to
display the ranked list.

## API summary

Registration requires all four fields:

```json
{
  "email": "player@example.com",
  "displayName": "Player One",
  "password": "a-long-private-password",
  "confirmPassword": "a-long-private-password"
}
```

Login accepts only the email and password fields:

```json
{
  "email": "player@example.com",
  "password": "a-long-private-password"
}
```

- `POST /auth/register`
- `POST /auth/login`
- `POST /auth/logout`
- `GET /auth/me`
- `POST /train_graph`
- `GET /training_jobs/{job_id}`
- `POST /worker-plane/workers/register`
- `POST /worker-plane/workers/heartbeat`
- `POST /worker-plane/jobs/claim`
- `POST /worker-plane/jobs/{job_id}/heartbeat`
- `POST /worker-plane/jobs/{job_id}/complete`
- `POST /worker-plane/jobs/{job_id}/fail`
- `POST /final_evaluate_graph`
- `GET /leaderboard?dataset=MNIST`
- `GET /worker-plane/status`

Passwords are salted and hashed with PBKDF2-HMAC-SHA256. Account and worker
tokens are secrets. Do not commit `compute-worker.json`, copy Unity's
`PlayerPrefs`, or send tokens to another person. Use HTTPS or a trusted private
VPN outside a private LAN so passwords and session tokens are encrypted in
transit.

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
