param(
    [string]$UnityProjectPath = "D:\folders\Unity\Unity_Prj\My project",
    [ValidateSet("Cpu", "Cuda")]
    [string]$TorchVariant = "Cuda",
    [string]$WorkerPythonPath = "",
    [switch]$SkipUnityCopy
)

$ErrorActionPreference = "Stop"
$BackendPath = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($WorkerPythonPath)) {
    $WorkerPythonPath = Join-Path $BackendPath ".worker-venv\Scripts\python.exe"
}
$WorkerPython = [IO.Path]::GetFullPath($WorkerPythonPath)
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

$TorchInfo = & $WorkerPython -c `
    "import json, torch; print(json.dumps({'version': torch.__version__, 'cudaBuild': torch.version.cuda or ''}))" |
    ConvertFrom-Json
if ($TorchVariant -eq "Cuda" -and [string]::IsNullOrWhiteSpace($TorchInfo.cudaBuild)) {
    throw (
        "TorchVariant Cuda requires a CUDA-enabled PyTorch worker environment. " +
        "Reinstall torch and torchvision in .worker-venv from the official cu128 index."
    )
}
if ($TorchVariant -eq "Cpu" -and -not [string]::IsNullOrWhiteSpace($TorchInfo.cudaBuild)) {
    throw (
        "TorchVariant Cpu requires a CPU-only PyTorch worker environment. " +
        "Reinstall torch and torchvision in .worker-venv from the official cpu index."
    )
}
Write-Host "Packaging PyTorch $($TorchInfo.version), CUDA build: $($TorchInfo.cudaBuild)"
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
$SourceExePath = Join-Path $SourcePath "NNBuilderWorker.exe"
$SourceTorchCpuPath = Join-Path $SourcePath "_internal\torch\lib\torch_cpu.dll"
$SourceTorchCudaPath = Join-Path $SourcePath "_internal\torch\lib\torch_cuda.dll"
if (-not (Test-Path -LiteralPath $SourceExePath)) {
    throw "Packaged worker executable was not produced."
}
if (-not (Test-Path -LiteralPath $SourceTorchCpuPath)) {
    throw "Packaged worker is incomplete: torch_cpu.dll was not produced."
}
if ($TorchVariant -eq "Cuda" -and -not (Test-Path -LiteralPath $SourceTorchCudaPath)) {
    throw "Packaged CUDA worker is incomplete: torch_cuda.dll was not produced."
}

if (-not $SkipUnityCopy) {
    New-Item -ItemType Directory -Path $StreamingAssetsPath -Force | Out-Null
    if (Test-Path -LiteralPath $TargetPath) {
        Remove-Item -LiteralPath $TargetPath -Recurse -Force
    }
    Copy-Item -LiteralPath $SourcePath -Destination $TargetPath -Recurse

    $TargetTorchCpuPath = Join-Path $TargetPath "_internal\torch\lib\torch_cpu.dll"
    if (-not (Test-Path -LiteralPath $TargetTorchCpuPath)) {
        throw "Copied worker is incomplete: $TargetTorchCpuPath is missing."
    }
    $SourceTorchCpuSize = (Get-Item -LiteralPath $SourceTorchCpuPath).Length
    $TargetTorchCpuSize = (Get-Item -LiteralPath $TargetTorchCpuPath).Length
    if ($SourceTorchCpuSize -ne $TargetTorchCpuSize) {
        throw "Copied worker is corrupt: torch_cpu.dll size does not match the packaged source."
    }
    if ($TorchVariant -eq "Cuda") {
        $TargetTorchCudaPath = Join-Path $TargetPath "_internal\torch\lib\torch_cuda.dll"
        if (-not (Test-Path -LiteralPath $TargetTorchCudaPath)) {
            throw "Copied CUDA worker is incomplete: $TargetTorchCudaPath is missing."
        }
        $SourceTorchCudaSize = (Get-Item -LiteralPath $SourceTorchCudaPath).Length
        $TargetTorchCudaSize = (Get-Item -LiteralPath $TargetTorchCudaPath).Length
        if ($SourceTorchCudaSize -ne $TargetTorchCudaSize) {
            throw "Copied worker is corrupt: torch_cuda.dll size does not match the packaged source."
        }
    }
}

$SizePath = if ($SkipUnityCopy) { $SourcePath } else { $TargetPath }
$Size = (
    Get-ChildItem -LiteralPath $SizePath -File -Recurse |
    Measure-Object -Property Length -Sum
).Sum
$SizeMb = [Math]::Round($Size / 1MB, 1)
if ($SkipUnityCopy) {
    Write-Host "Bundled worker created without Unity copy: $SourcePath"
} else {
    Write-Host "Bundled worker copied to: $TargetPath"
}
Write-Host "Bundled worker size: $SizeMb MB"
Write-Host "Bundled worker variant: $TorchVariant"
