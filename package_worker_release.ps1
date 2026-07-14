param(
    [string]$UnityProjectPath = "D:\folders\Unity\Unity_Prj\My project",
    [string]$OutputPath = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$BackendPath = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $BackendPath "release\NNBuilderWorker-Windows-x64.zip"
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$OutputDirectory = Split-Path -Parent $OutputPath
$WorkerPath = Join-Path $UnityProjectPath "Assets\StreamingAssets\ComputeWorker"

if (-not $SkipBuild) {
    & (Join-Path $BackendPath "build_worker.ps1") -UnityProjectPath $UnityProjectPath
    if ($LASTEXITCODE -ne 0) {
        throw "Worker build failed with exit code $LASTEXITCODE."
    }
}

$RequiredFiles = @(
    (Join-Path $WorkerPath "NNBuilderWorker.exe"),
    (Join-Path $WorkerPath "_internal\torch\lib\torch_cpu.dll")
)
foreach ($RequiredFile in $RequiredFiles) {
    if (-not (Test-Path -LiteralPath $RequiredFile)) {
        throw "Worker release is incomplete: $RequiredFile is missing."
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $OutputPath) {
    Remove-Item -LiteralPath $OutputPath -Force
}
Compress-Archive -LiteralPath $WorkerPath -DestinationPath $OutputPath -CompressionLevel Optimal

$Hash = Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256
$HashPath = $OutputPath + ".sha256.txt"
Set-Content -LiteralPath $HashPath -Value ($Hash.Hash + "  " + (Split-Path -Leaf $OutputPath))

$SizeMb = [Math]::Round((Get-Item -LiteralPath $OutputPath).Length / 1MB, 1)
Write-Host "GitHub Release asset created: $OutputPath"
Write-Host "Archive size: $SizeMb MB"
Write-Host "SHA-256: $($Hash.Hash)"
Write-Host "Upload both the ZIP and .sha256.txt file to a GitHub Release."
