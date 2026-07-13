param(
    [Parameter(Mandatory = $true)]
    [string]$ServerUrl,
    [string]$UnityProjectPath = "D:\folders\Unity\Unity_Prj\My project",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$BackendPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.6f1\Editor\Unity.exe"
$UnityProjectPath = [IO.Path]::GetFullPath($UnityProjectPath)

$parsedUrl = $null
if (-not [Uri]::TryCreate($ServerUrl, [UriKind]::Absolute, [ref]$parsedUrl)) {
    throw "ServerUrl must be an absolute HTTP or HTTPS URL."
}
if ($parsedUrl.Scheme -notin @("http", "https")) {
    throw "ServerUrl must use HTTP or HTTPS."
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

& (Join-Path $BackendPath "build_worker.ps1") `
    -UnityProjectPath $UnityProjectPath

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

if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $OutputPath)) {
    throw "Unity build failed. See $LogPath"
}

@{ serverUrl = $ServerUrl.TrimEnd('/') } |
    ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $OutputDirectory "server-config.json") `
        -Encoding UTF8

Write-Host "Windows player created: $OutputPath"
Write-Host "Distribute the entire folder: $OutputDirectory"
