[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallRoot = "",
    [string]$ExtensionDirectory = "",
    [string]$ManifestDirectory = "",
    [string]$HostExecutable = "",
    [ValidateSet("Chrome", "Edge", "Both")]
    [string]$Browser = "Both",
    [string]$ExtensionId = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$hostName = "com.rot.send_to_rot"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = if (Test-Path -LiteralPath (Join-Path $repositoryRoot "Rot.BrowserHost.exe")) {
        $repositoryRoot
    } else { Join-Path $repositoryRoot "dist\Rot-win-x64" }
}
if ([string]::IsNullOrWhiteSpace($ExtensionDirectory)) {
    $ExtensionDirectory = Join-Path $repositoryRoot "browser-extension"
}
if ([string]::IsNullOrWhiteSpace($ManifestDirectory)) {
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw "LOCALAPPDATA is required to store the per-user native messaging manifest."
    }
    $ManifestDirectory = Join-Path $env:LOCALAPPDATA "Rot\BrowserHost"
}
$hostExecutablePath = if ([string]::IsNullOrWhiteSpace($HostExecutable)) {
    Join-Path $InstallRoot "Rot.BrowserHost.exe"
} else {
    $HostExecutable
}

if (-not (Test-Path -LiteralPath $hostExecutablePath -PathType Leaf)) {
    throw "Browser host executable was not found: $hostExecutablePath"
}

$manifestPath = Join-Path $ManifestDirectory "$hostName.json"
$extensionManifestPath = Join-Path $ExtensionDirectory "manifest.json"
if (-not (Test-Path -LiteralPath $extensionManifestPath -PathType Leaf)) {
    throw "Browser extension manifest was not found: $extensionManifestPath"
}

function Get-StableExtensionId {
    param([string]$ManifestPath)

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($manifest.key)) {
        throw "The extension manifest must contain its committed public key."
    }

    $publicKey = [Convert]::FromBase64String($manifest.key)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($publicKey)
    } finally {
        $sha256.Dispose()
    }
    $alphabet = "abcdefghijklmnop"
    $builder = [Text.StringBuilder]::new()
    foreach ($byte in $hash[0..15]) {
        [void]$builder.Append($alphabet[($byte -shr 4) -band 0x0f])
        [void]$builder.Append($alphabet[$byte -band 0x0f])
    }

    $builder.ToString()
}

if ([string]::IsNullOrWhiteSpace($ExtensionId)) {
    $ExtensionId = Get-StableExtensionId -ManifestPath $extensionManifestPath
}

if ($ExtensionId -notmatch "^[a-p]{32}$") {
    throw "ExtensionId must be a 32-character Chrome/Edge extension ID."
}

$allowedOrigin = "chrome-extension://$ExtensionId/"
$hostManifest = [ordered]@{
    name = $hostName
    description = "Send YouTube selections to Rot."
    path = [IO.Path]::GetFullPath($hostExecutablePath)
    type = "stdio"
    allowed_origins = @($allowedOrigin)
}

if ($PSCmdlet.ShouldProcess($manifestPath, "Write native messaging host manifest")) {
    $manifestDirectoryPath = Split-Path -Parent $manifestPath
    if (Test-Path -LiteralPath $manifestDirectoryPath -PathType Leaf) {
        throw "Native messaging manifest directory path is an existing file: $manifestDirectoryPath. Move it aside or choose another -ManifestDirectory path."
    }
    if (-not (Test-Path -LiteralPath $manifestDirectoryPath -PathType Container)) {
        New-Item -Path $manifestDirectoryPath -ItemType Directory -Force | Out-Null
    }
    $manifestJson = $hostManifest | ConvertTo-Json -Depth 3
    [IO.File]::WriteAllText($manifestPath, $manifestJson, [Text.UTF8Encoding]::new($false))
}

$registryBases = @{
    Chrome = "HKCU:\Software\Google\Chrome\NativeMessagingHosts"
    Edge = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts"
}
$browsers = if ($Browser -eq "Both") { @("Chrome", "Edge") } else { @($Browser) }
foreach ($browserName in $browsers) {
    $registrationPath = Join-Path $registryBases[$browserName] $hostName
    if ($PSCmdlet.ShouldProcess($registrationPath, "Register current-user native messaging host")) {
        New-Item -Path $registrationPath -Force | Out-Null
        Set-Item -LiteralPath $registrationPath -Value ([IO.Path]::GetFullPath($manifestPath))
    }
}

[pscustomobject]@{
    HostName = $hostName
    ExtensionId = $ExtensionId
    ManifestPath = [IO.Path]::GetFullPath($manifestPath)
    Browsers = $browsers -join ","
}
