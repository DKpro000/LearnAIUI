param(
    [ValidateSet("Cpu", "Cuda")]
    [string]$TorchVariant = "Cuda",
    [ValidateRange(100, 1950)]
    [int]$PartSizeMB = 1400,
    [string]$ReleasePath = ""
)

$ErrorActionPreference = "Stop"
$BackendPath = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ReleasePath)) {
    $ReleasePath = Join-Path $BackendPath "release"
}
$ReleasePath = [IO.Path]::GetFullPath($ReleasePath)
$VariantName = $TorchVariant.ToLowerInvariant()
$DescriptorPath = Join-Path $ReleasePath "worker-package-$VariantName.json"
$ManifestPath = Join-Path $ReleasePath "NNBuilderWorker-manifest.json"

if (-not (Test-Path -LiteralPath $DescriptorPath)) {
    throw "Package descriptor not found: $DescriptorPath"
}

$Package = Get-Content -LiteralPath $DescriptorPath -Raw | ConvertFrom-Json
if (-not $Package.parts -or $Package.parts.Count -lt 1) {
    throw "Package descriptor contains no archive parts."
}

$FirstName = [string]$Package.parts[0].fileName
$ArchiveName = $FirstName -replace '\.part\d{3}$', ''
$TemporaryArchivePath = Join-Path $ReleasePath ($ArchiveName + ".repartitioning")
$UrlPrefix = ([string]$Package.parts[0].url)
$UrlPrefix = $UrlPrefix.Substring(0, $UrlPrefix.LastIndexOf('/') + 1)

if (Test-Path -LiteralPath $TemporaryArchivePath) {
    Remove-Item -LiteralPath $TemporaryArchivePath -Force
}

Write-Host "Verifying and joining the existing $VariantName archive parts."
$OutputStream = [IO.File]::Create($TemporaryArchivePath)
try {
    foreach ($Part in $Package.parts) {
        $PartPath = Join-Path $ReleasePath ([string]$Part.fileName)
        if (-not (Test-Path -LiteralPath $PartPath)) {
            throw "Archive part not found: $PartPath"
        }
        $ActualPartHash = (Get-FileHash -LiteralPath $PartPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($ActualPartHash -ne ([string]$Part.sha256).ToLowerInvariant()) {
            throw "Checksum mismatch for $PartPath"
        }
        $InputStream = [IO.File]::OpenRead($PartPath)
        try {
            $InputStream.CopyTo($OutputStream)
        }
        finally {
            $InputStream.Dispose()
        }
    }
}
finally {
    $OutputStream.Dispose()
}

$CombinedItem = Get-Item -LiteralPath $TemporaryArchivePath
$CombinedHash = (Get-FileHash -LiteralPath $TemporaryArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($CombinedItem.Length -ne [int64]$Package.archiveSizeBytes) {
    throw "Combined archive size does not match the descriptor."
}
if ($CombinedHash -ne ([string]$Package.archiveSha256).ToLowerInvariant()) {
    throw "Combined archive checksum does not match the descriptor."
}

foreach ($Part in $Package.parts) {
    $OldPartPath = Join-Path $ReleasePath ([string]$Part.fileName)
    Remove-Item -LiteralPath $OldPartPath -Force
    $OldHashPath = $OldPartPath + ".sha256.txt"
    if (Test-Path -LiteralPath $OldHashPath) {
        Remove-Item -LiteralPath $OldHashPath -Force
    }
}

$MaximumPartBytes = [int64]$PartSizeMB * 1MB
$Buffer = New-Object byte[] (8MB)
$InputStream = [IO.File]::OpenRead($TemporaryArchivePath)
$NewParts = @()
try {
    $PartNumber = 1
    while ($InputStream.Position -lt $InputStream.Length) {
        $PartName = $ArchiveName + ".part" + $PartNumber.ToString("000")
        $PartPath = Join-Path $ReleasePath $PartName
        $PartStream = [IO.File]::Create($PartPath)
        try {
            $Written = [int64]0
            while ($Written -lt $MaximumPartBytes) {
                $Remaining = [Math]::Min([int64]$Buffer.Length, $MaximumPartBytes - $Written)
                $Read = $InputStream.Read($Buffer, 0, [int]$Remaining)
                if ($Read -le 0) {
                    break
                }
                $PartStream.Write($Buffer, 0, $Read)
                $Written += $Read
            }
        }
        finally {
            $PartStream.Dispose()
        }

        $PartItem = Get-Item -LiteralPath $PartPath
        $PartHash = (Get-FileHash -LiteralPath $PartPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content `
            -LiteralPath ($PartPath + ".sha256.txt") `
            -Value ($PartHash + "  " + $PartName) `
            -Encoding ASCII
        $NewParts += [ordered]@{
            fileName = $PartName
            url = $UrlPrefix + [Uri]::EscapeDataString($PartName)
            sha256 = $PartHash
            sizeBytes = $PartItem.Length
        }
        $PartNumber += 1
    }
}
finally {
    $InputStream.Dispose()
    Remove-Item -LiteralPath $TemporaryArchivePath -Force
}

$Package.parts = @($NewParts)
$Package |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $DescriptorPath -Encoding UTF8

$Manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$Manifest.packages.$VariantName = $Package
$Manifest.createdAt = [DateTime]::UtcNow.ToString("o")
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
foreach ($ManifestPackage in @($Manifest.packages.cpu, $Manifest.packages.cuda)) {
    foreach ($Part in $ManifestPackage.parts) {
        $UploadNames.Add([string]$Part.fileName)
    }
}
$UploadListPath = Join-Path $ReleasePath "worker-release-assets.txt"
$UploadNames | Set-Content -LiteralPath $UploadListPath -Encoding ASCII

Write-Host "Repartitioned $VariantName package into $($NewParts.Count) part(s) of at most $PartSizeMB MB."
Write-Host "Updated descriptor, manifest, checksums, and upload list in $ReleasePath"
