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

The compiled game no longer contains a multi-gigabyte PyTorch folder. After a
player logs in, Unity downloads and caches only the appropriate worker release:

- if Windows exposes `nvcuda.dll`, Unity downloads the CUDA package;
- otherwise Unity downloads the smaller CPU package;
- every downloaded part and the reconstructed ZIP are verified with SHA-256;
- the archive is extracted with path-traversal protection under Unity's
  `persistentDataPath`; and
- if CUDA installation fails, Unity automatically tries the CPU package.

The CUDA worker still performs its own real PyTorch allocation probe. It trains
on `cuda:0` when usable, falls back to CPU when it is not, and retries a
CUDA-failed job once on CPU. Players run only Unity and do not install Python or
the CUDA toolkit.

Create two dedicated environments. The CPU environment uses the CPU wheel:

```powershell
py -m venv .worker-venv
.\.worker-venv\Scripts\python.exe -m pip install --upgrade pip
.\.worker-venv\Scripts\python.exe -m pip install `
  --index-url https://download.pytorch.org/whl/cpu `
  torch torchvision
.\.worker-venv\Scripts\python.exe -m pip install numpy pandas pyinstaller
```

The server `.venv` on this computer is currently also the CUDA packaging
environment. Confirm that it reports a CUDA build before packaging:

```powershell
.\.venv\Scripts\python.exe -c `
  "import torch; print(torch.__version__, torch.version.cuda)"
```

Create both packages and the manifest. Use a new version for every worker code
or dependency change, and use the same value as the GitHub Release tag suffix:

```powershell
.\package_worker_catalog.ps1 `
  -Version "2026.07.19" `
  -ReleaseTag "worker-v2026.07.19" `
  -Repository "DKpro000/LearnAIUI" `
  -UnityProjectPath "D:\folders\Unity\Unity_Prj\My project" `
  -CpuPythonPath ".\.worker-venv\Scripts\python.exe" `
  -CudaPythonPath ".\.venv\Scripts\python.exe"
```

The script builds CPU and CUDA workers separately, compresses them, splits any
archive larger than 1,400 MB into `.part001`, `.part002`, and later parts, and
creates `release/NNBuilderWorker-manifest.json`. Part URLs, sizes, and SHA-256
values are placed in that manifest automatically.

Upload these files from `release` to one GitHub Release whose tag exactly
matches `-ReleaseTag`:

- `NNBuilderWorker-manifest.json`;
- every CPU ZIP or ZIP part; and
- every CUDA ZIP or ZIP part.

The script also writes `worker-release-assets.txt`, containing the minimum exact
assets to upload. The separate checksum files remain useful for local/manual
verification, but Unity uses the hashes embedded in the manifest, so they do
not need to be Release assets. Intermediate descriptors, checksum files, and
old ZIPs are not on the upload list. Do not rename assets because their exact
names are recorded in the manifest.

Using the GitHub web interface:

1. Open the repository's **Releases** page and select **Draft a new release**.
2. Create the tag used above, such as `worker-v2026.07.19`.
3. Drag all required assets from `backend/release` into the asset box.
4. Wait until every upload finishes, then publish the release.
5. Open the tag-specific URL and confirm that JSON appears. Do not use
   `releases/latest`: publishing a later game release would change where that
   URL points.

```text
https://github.com/DKpro000/LearnAIUI/releases/download/worker-v2026.07.19/NNBuilderWorker-manifest.json
```

If GitHub's browser uploader rejects a large part, repartition the existing
CUDA archive without rebuilding PyTorch:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\repartition_worker_release.ps1 `
  -TorchVariant Cuda `
  -PartSizeMB 1400
```

This regenerates the parts, checksums, manifest, and exact upload list. Refresh
the release page and upload every file in the new list; never mix assets from
before and after repartitioning.

With GitHub CLI installed and authenticated, the equivalent upload is:

```powershell
$assets = Get-Content .\release\worker-release-assets.txt |
  ForEach-Object { Join-Path (Resolve-Path .\release) $_ }

gh release create "worker-v2026.07.19" $assets `
  --repo "DKpro000/LearnAIUI" `
  --title "NNBuilder worker 2026.07.19" `
  --notes "Automatic CPU/CUDA compute worker release."
```

To verify a packaged executable before publishing, run:

```powershell
.\worker-dist\NNBuilderWorker\NNBuilderWorker.exe `
  --diagnose-device `
  --log-file .\worker-device.log
Get-Content .\worker-device.log
```

The JSON result reports `selectedDevice` as `cuda:0` or `cpu`, plus the GPU name
and any fallback reason.

Distribute the complete Unity build folder, not only the `.exe`; Unity always
requires its `_Data` folder and bundled libraries. Players only need to launch
the Unity `.exe`. They do not install or start Python.

After the worker GitHub Release is published, build the small Windows player.
The build script temporarily moves any developer-only
`Assets/StreamingAssets/ComputeWorker` folder outside `Assets`, restores it
afterward, and writes both URLs to `server-config.json`:

```powershell
.\build_unity_windows.ps1 `
  -ServerUrl "http://192.168.1.20:8000" `
  -UnityProjectPath "D:\folders\Unity\Unity_Prj\My project" `
  -WorkerManifestUrl (
    "https://github.com/DKpro000/LearnAIUI/releases/download/" +
    "worker-v2026.07.19/" +
    "NNBuilderWorker-manifest.json"
  )
```

The complete distributable is created under
`My project/Builds/NNBuilder` by default.

At runtime Unity automatically:

1. shows separate login and registration views when there is no valid saved
   session;
2. remembers only the returned session token, never the password;
3. writes `compute-worker.json` under `Application.persistentDataPath` after
   login;
4. downloads and verifies the selected worker only when it is not cached;
5. launches the cached worker invisibly;
6. restarts the worker if it exits unexpectedly; and
7. stops it on logout or when Unity closes.

The worker stores downloaded torchvision data, temporary checkpoints, and its
log under `Application.persistentDataPath/compute-worker-runtime`. The cached
worker advertises MNIST, FashionMNIST, and CIFAR10 automatically. It advertises
a local dataset only when that dataset folder exists under
`compute-worker-runtime/dataset`, so unsupported jobs remain queued for another
worker or fall back to the server.

Set `GraphBackendClient.automaticallyContributeCompute` to false before building
if you want an edition that does not launch the worker. Compute contribution
uses the player's GPU when supported, otherwise its CPU, as well as memory,
storage, electricity, and network connection. The released game should explain
this behavior clearly.

## Unity behavior

`GraphBackendClient` now:

- creates a runtime login/register screen without scene or prefab setup;
- validates and remembers an account session, and supports logout;
- loads the server URL from an external file or command line;
- downloads, verifies, caches, starts, and monitors the correct worker only
  after login;
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
