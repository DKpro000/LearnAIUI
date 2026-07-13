param(
    [string]$UnityProjectPath = "D:\folders\Unity\Unity_Prj\My project"
)

$ErrorActionPreference = "Stop"
$BackendPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$WorkerPython = Join-Path $BackendPath ".worker-venv\Scripts\python.exe"
$WorkPath = Join-Path $BackendPath "worker-build"
$DistPath = Join-Path $BackendPath "worker-dist"
$UnityAssetsPath = [IO.Path]::GetFullPath((Join-Path $UnityProjectPath "Assets"))
$StreamingAssetsPath = Join-Path $UnityAssetsPath "StreamingAssets"
$TargetPath = [IO.Path]::GetFullPath(
    (Join-Path $StreamingAssetsPath "ComputeWorker")
)

if (-not (Test-Path -LiteralPath $WorkerPython)) {
    throw "Worker environment not found. Create .worker-venv and install the worker dependencies first."
}
if (-not (Test-Path -LiteralPath $UnityAssetsPath)) {
    throw "Unity Assets folder not found: $UnityAssetsPath"
}
if (-not $TargetPath.StartsWith($UnityAssetsPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to package outside the Unity Assets folder: $TargetPath"
}

& $WorkerPython -m PyInstaller `
    --noconfirm `
    --onedir `
    --noconsole `
    --name NNBuilderWorker `
    --distpath $DistPath `
    --workpath $WorkPath `
    --specpath $BackendPath `
    --collect-data torchvision `
    --hidden-import torchvision.datasets.mnist `
    --hidden-import torchvision.datasets.cifar `
    --hidden-import torchvision.datasets.folder `
    --exclude-module fastapi `
    --exclude-module uvicorn `
    --exclude-module torchaudio `
    (Join-Path $BackendPath "compute_worker.py")

if ($LASTEXITCODE -ne 0) {
    throw "Worker packaging failed with exit code $LASTEXITCODE."
}

$SourcePath = Join-Path $DistPath "NNBuilderWorker"
if (-not (Test-Path -LiteralPath (Join-Path $SourcePath "NNBuilderWorker.exe"))) {
    throw "Packaged worker executable was not produced."
}

New-Item -ItemType Directory -Path $StreamingAssetsPath -Force | Out-Null
if (Test-Path -LiteralPath $TargetPath) {
    Remove-Item -LiteralPath $TargetPath -Recurse -Force
}
Copy-Item -LiteralPath $SourcePath -Destination $TargetPath -Recurse

$Size = (
    Get-ChildItem -LiteralPath $TargetPath -File -Recurse |
    Measure-Object -Property Length -Sum
).Sum
$SizeMb = [Math]::Round($Size / 1MB, 1)
Write-Host "Bundled worker copied to: $TargetPath"
Write-Host "Bundled worker size: $SizeMb MB"
