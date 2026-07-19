param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Repository = "DKpro000/LearnAIUI",
    [string]$ReleaseTag = "",
    [string]$UnityProjectPath = "D:\folders\Unity\Unity_Prj\My project",
    [string]$CpuPythonPath = ".\.worker-venv\Scripts\python.exe",
    [string]$CudaPythonPath = ".\.venv\Scripts\python.exe",
    [ValidateRange(100, 1950)]
    [int]$PartSizeMB = 1400,
    [switch]$SkipCpuBuild,
    [switch]$SkipCudaBuild
)

$ErrorActionPreference = "Stop"
$BackendPath = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ReleaseTag)) {
    $ReleaseTag = "worker-v$Version"
}
$ReleasePath = Join-Path $BackendPath "release"
$CpuDescriptorPath = Join-Path $ReleasePath "worker-package-cpu.json"
$CudaDescriptorPath = Join-Path $ReleasePath "worker-package-cuda.json"

$CpuArguments = @{
    UnityProjectPath = $UnityProjectPath
    Version = $Version
    Repository = $Repository
    ReleaseTag = $ReleaseTag
    TorchVariant = "Cpu"
    WorkerPythonPath = $CpuPythonPath
    PartSizeMB = $PartSizeMB
    DescriptorPath = $CpuDescriptorPath
}
if ($SkipCpuBuild) {
    $CpuArguments.SkipBuild = $true
}
& (Join-Path $BackendPath "package_worker_release.ps1") @CpuArguments

$CudaArguments = @{
    UnityProjectPath = $UnityProjectPath
    Version = $Version
    Repository = $Repository
    ReleaseTag = $ReleaseTag
    TorchVariant = "Cuda"
    WorkerPythonPath = $CudaPythonPath
    PartSizeMB = $PartSizeMB
    DescriptorPath = $CudaDescriptorPath
}
if ($SkipCudaBuild) {
    $CudaArguments.SkipBuild = $true
}
& (Join-Path $BackendPath "package_worker_release.ps1") @CudaArguments

$CpuPackage = Get-Content -LiteralPath $CpuDescriptorPath -Raw | ConvertFrom-Json
$CudaPackage = Get-Content -LiteralPath $CudaDescriptorPath -Raw | ConvertFrom-Json
$Manifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    releaseTag = $ReleaseTag
    createdAt = [DateTime]::UtcNow.ToString("o")
    packages = [ordered]@{
        cpu = $CpuPackage
        cuda = $CudaPackage
    }
}
$ManifestPath = Join-Path $ReleasePath "NNBuilderWorker-manifest.json"
$Manifest |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $ManifestPath -Encoding UTF8
$ManifestHash = (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content `
    -LiteralPath ($ManifestPath + ".sha256.txt") `
    -Value ($ManifestHash + "  " + (Split-Path -Leaf $ManifestPath)) `
    -Encoding ASCII

$UploadNames = New-Object System.Collections.Generic.List[string]
$UploadNames.Add((Split-Path -Leaf $ManifestPath))
foreach ($Package in @($CpuPackage, $CudaPackage)) {
    foreach ($Part in $Package.parts) {
        $UploadNames.Add([string]$Part.fileName)
    }
}
$UploadListPath = Join-Path $ReleasePath "worker-release-assets.txt"
$UploadNames | Set-Content -LiteralPath $UploadListPath -Encoding ASCII

Write-Host "Worker release catalog is ready: $ReleasePath"
Write-Host "GitHub tag: $ReleaseTag"
Write-Host "Exact upload list: $UploadListPath"
