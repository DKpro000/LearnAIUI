param(
    [Parameter(Mandatory = $true)]
    [string]$ServerUrl,
    [string]$UnityProjectPath = "D:\folders\Unity\Unity_Prj\My project",
    [string]$OutputPath = "",
    [string]$WorkerManifestUrl = (
        "https://github.com/DKpro000/LearnAIUI/releases/download/" +
        "worker-v2026.07.19/" +
        "NNBuilderWorker-manifest.json"
    )
)

$ErrorActionPreference = "Stop"
$BackendPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.5.3f1\Editor\Unity.exe"
$UnityProjectPath = [IO.Path]::GetFullPath($UnityProjectPath)

$parsedUrl = $null
if (-not [Uri]::TryCreate($ServerUrl, [UriKind]::Absolute, [ref]$parsedUrl)) {
    throw "ServerUrl must be an absolute HTTP or HTTPS URL."
}
if ($parsedUrl.Scheme -notin @("http", "https")) {
    throw "ServerUrl must use HTTP or HTTPS."
}
$parsedManifestUrl = $null
if (-not [Uri]::TryCreate(
    $WorkerManifestUrl,
    [UriKind]::Absolute,
    [ref]$parsedManifestUrl
)) {
    throw "WorkerManifestUrl must be an absolute HTTPS URL."
}
if (
    $parsedManifestUrl.Scheme -ne "https" -and
    $parsedManifestUrl.Host -notin @("127.0.0.1", "localhost")
) {
    throw "WorkerManifestUrl must use HTTPS except during a localhost test."
}
if (-not (Test-Path -LiteralPath $UnityExe)) {
    throw "Unity editor was not found: $UnityExe"
}
if (Get-Process -Name Unity -ErrorAction SilentlyContinue) {
    throw "Close the Unity Editor before running the automated Windows build."
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $UnityProjectPath "Builds\NNBuilder\NNBuilder.exe"
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$OutputDirectory = Split-Path -Parent $OutputPath
$LogPath = Join-Path $BackendPath "unity-windows-build.log"
if (
    (Test-Path -LiteralPath $OutputDirectory) -and
    (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)
) {
    throw (
        "Output directory is not empty. Choose a new OutputPath or move the " +
        "old build first so a previously bundled worker cannot remain: " +
        $OutputDirectory
    )
}
$WorkerAssetsPath = [IO.Path]::GetFullPath((
    Join-Path $UnityProjectPath "Assets\StreamingAssets\ComputeWorker"
))
$WorkerMetaPath = $WorkerAssetsPath + ".meta"
$WorkerStashPath = [IO.Path]::GetFullPath((
    Join-Path $UnityProjectPath ".ComputeWorker-build-stash"
))
$WorkerMetaStashPath = $WorkerStashPath + ".meta"
if (
    -not $WorkerAssetsPath.StartsWith(
        $UnityProjectPath,
        [StringComparison]::OrdinalIgnoreCase
    ) -or
    -not $WorkerStashPath.StartsWith(
        $UnityProjectPath,
        [StringComparison]::OrdinalIgnoreCase
    )
) {
    throw "Refusing to move worker files outside the Unity project."
}
if (
    (Test-Path -LiteralPath $WorkerStashPath) -or
    (Test-Path -LiteralPath $WorkerMetaStashPath)
) {
    throw "Remove the stale worker build stash first: $WorkerStashPath"
}

$WorkerWasStashed = $false
$WorkerMetaWasStashed = $false
try {
    # A local developer worker may remain available in the Editor, but it must
    # not inflate the compiled game. Move it outside Assets only for the build.
    if (Test-Path -LiteralPath $WorkerAssetsPath) {
        Move-Item -LiteralPath $WorkerAssetsPath -Destination $WorkerStashPath
        $WorkerWasStashed = $true
    }
    if (Test-Path -LiteralPath $WorkerMetaPath) {
        Move-Item -LiteralPath $WorkerMetaPath -Destination $WorkerMetaStashPath
        $WorkerMetaWasStashed = $true
    }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $arguments = @(
        "-batchmode",
        "-quit",
        "-projectPath",
        ('"' + $UnityProjectPath + '"'),
        "-buildWindows64Player",
        ('"' + $OutputPath + '"'),
        "-logFile",
        ('"' + $LogPath + '"')
    )
    $process = Start-Process `
        -FilePath $UnityExe `
        -ArgumentList $arguments `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
}
finally {
    if ($WorkerWasStashed -and (Test-Path -LiteralPath $WorkerStashPath)) {
        Move-Item -LiteralPath $WorkerStashPath -Destination $WorkerAssetsPath
    }
    if ($WorkerMetaWasStashed -and (Test-Path -LiteralPath $WorkerMetaStashPath)) {
        Move-Item -LiteralPath $WorkerMetaStashPath -Destination $WorkerMetaPath
    }
}

if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $OutputPath)) {
    throw "Unity build failed. See $LogPath"
}

[ordered]@{
    serverUrl = $ServerUrl.TrimEnd('/')
    workerManifestUrl = $WorkerManifestUrl
} |
    ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $OutputDirectory "server-config.json") `
        -Encoding UTF8

Write-Host "Windows player created: $OutputPath"
Write-Host "Distribute the entire folder: $OutputDirectory"
