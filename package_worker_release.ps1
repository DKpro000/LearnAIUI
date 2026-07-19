param(
    [string]$UnityProjectPath = "D:\folders\Unity\Unity_Prj\My project",
    [string]$Version = (Get-Date -Format "yyyy.MM.dd"),
    [string]$Repository = "DKpro000/LearnAIUI",
    [string]$ReleaseTag = "",
    [string]$OutputPath = "",
    [string]$DescriptorPath = "",
    [ValidateSet("Cpu", "Cuda")]
    [string]$TorchVariant = "Cuda",
    [string]$WorkerPythonPath = "",
    [ValidateRange(100, 1950)]
    [int]$PartSizeMB = 1400,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$BackendPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$VariantName = $TorchVariant.ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($ReleaseTag)) {
    $ReleaseTag = "worker-v$Version"
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Repository must use the owner/name format."
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Version cannot be empty."
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $BackendPath (
        "release\NNBuilderWorker-Windows-x64-$VariantName-$Version.zip"
    )
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$OutputDirectory = Split-Path -Parent $OutputPath
if ([string]::IsNullOrWhiteSpace($DescriptorPath)) {
    $DescriptorPath = Join-Path $OutputDirectory "worker-package-$VariantName.json"
}
$DescriptorPath = [IO.Path]::GetFullPath($DescriptorPath)
$WorkerPath = Join-Path $BackendPath "worker-dist\NNBuilderWorker"

if (-not $SkipBuild) {
    & (Join-Path $BackendPath "build_worker.ps1") `
        -UnityProjectPath $UnityProjectPath `
        -TorchVariant $TorchVariant `
        -WorkerPythonPath $WorkerPythonPath `
        -SkipUnityCopy
    if ($LASTEXITCODE -ne 0) {
        throw "Worker build failed with exit code $LASTEXITCODE."
    }
}

$RequiredFiles = @(
    (Join-Path $WorkerPath "NNBuilderWorker.exe"),
    (Join-Path $WorkerPath "_internal\torch\lib\torch_cpu.dll")
)
if ($TorchVariant -eq "Cuda") {
    $RequiredFiles += Join-Path $WorkerPath "_internal\torch\lib\torch_cuda.dll"
}
foreach ($RequiredFile in $RequiredFiles) {
    if (-not (Test-Path -LiteralPath $RequiredFile)) {
        throw "Worker release is incomplete: $RequiredFile is missing."
    }
}
if (
    $TorchVariant -eq "Cpu" -and
    (Test-Path -LiteralPath (
        Join-Path $WorkerPath "_internal\torch\lib\torch_cuda.dll"
    ))
) {
    throw "The worker source contains torch_cuda.dll and cannot be labeled as a CPU package."
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $OutputPath) {
    Remove-Item -LiteralPath $OutputPath -Force
}
$PartPrefix = (Split-Path -Leaf $OutputPath) + ".part"
Get-ChildItem -LiteralPath $OutputDirectory -File |
    Where-Object { $_.Name.StartsWith($PartPrefix, [StringComparison]::Ordinal) } |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

Write-Host "Compressing $TorchVariant worker: $WorkerPath"
Compress-Archive `
    -LiteralPath $WorkerPath `
    -DestinationPath $OutputPath `
    -CompressionLevel Optimal

$ArchiveItem = Get-Item -LiteralPath $OutputPath
$ArchiveSize = $ArchiveItem.Length
$ArchiveHash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
$MaximumPartBytes = [int64]$PartSizeMB * 1MB
$PartPaths = New-Object System.Collections.Generic.List[string]

if ($ArchiveSize -gt $MaximumPartBytes) {
    Write-Host "Splitting archive into GitHub-compatible parts of at most $PartSizeMB MB."
    $Buffer = New-Object byte[] (8MB)
    $InputStream = [IO.File]::OpenRead($OutputPath)
    try {
        $PartNumber = 1
        while ($InputStream.Position -lt $InputStream.Length) {
            $PartPath = $OutputPath + ".part" + $PartNumber.ToString("000")
            $OutputStream = [IO.File]::Create($PartPath)
            try {
                $Written = [int64]0
                while ($Written -lt $MaximumPartBytes) {
                    $Remaining = [Math]::Min(
                        [int64]$Buffer.Length,
                        $MaximumPartBytes - $Written
                    )
                    $Read = $InputStream.Read($Buffer, 0, [int]$Remaining)
                    if ($Read -le 0) {
                        break
                    }
                    $OutputStream.Write($Buffer, 0, $Read)
                    $Written += $Read
                }
            }
            finally {
                $OutputStream.Dispose()
            }
            $PartPaths.Add($PartPath)
            $PartNumber += 1
        }
    }
    finally {
        $InputStream.Dispose()
    }
    Remove-Item -LiteralPath $OutputPath -Force
}
else {
    $PartPaths.Add($OutputPath)
}

$EncodedTag = [Uri]::EscapeDataString($ReleaseTag)
$Parts = @()
foreach ($PartPath in $PartPaths) {
    $PartItem = Get-Item -LiteralPath $PartPath
    $PartHash = (Get-FileHash -LiteralPath $PartPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $PartName = $PartItem.Name
    $PartHashPath = $PartPath + ".sha256.txt"
    Set-Content `
        -LiteralPath $PartHashPath `
        -Value ($PartHash + "  " + $PartName) `
        -Encoding ASCII
    $Parts += [ordered]@{
        fileName = $PartName
        url = "https://github.com/$Repository/releases/download/$EncodedTag/" +
            [Uri]::EscapeDataString($PartName)
        sha256 = $PartHash
        sizeBytes = $PartItem.Length
    }
}

$Descriptor = [ordered]@{
    variant = $VariantName
    archiveSha256 = $ArchiveHash
    archiveSizeBytes = $ArchiveSize
    executablePath = "NNBuilderWorker/NNBuilderWorker.exe"
    parts = $Parts
}
$Descriptor |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $DescriptorPath -Encoding UTF8

$ArchiveHashPath = Join-Path $OutputDirectory (
    (Split-Path -Leaf $OutputPath) + ".sha256.txt"
)
Set-Content `
    -LiteralPath $ArchiveHashPath `
    -Value ($ArchiveHash + "  " + (Split-Path -Leaf $OutputPath)) `
    -Encoding ASCII

$SizeMb = [Math]::Round($ArchiveSize / 1MB, 1)
Write-Host "$TorchVariant worker package prepared ($SizeMb MB compressed)."
Write-Host "Package descriptor: $DescriptorPath"
foreach ($PartPath in $PartPaths) {
    Write-Host "Release asset: $PartPath"
    Write-Host "Checksum asset: $PartPath.sha256.txt"
}
