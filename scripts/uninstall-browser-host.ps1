[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallRoot = "",
    [string]$ManifestPath = "",
    [string]$ManifestDirectory = "",
    [ValidateSet("Chrome", "Edge", "Both")]
    [string]$Browser = "Both"
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
if ([string]::IsNullOrWhiteSpace($ManifestDirectory)) {
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw "LOCALAPPDATA is required to locate the per-user native messaging manifest."
    }
    $ManifestDirectory = Join-Path $env:LOCALAPPDATA "Rot\BrowserHost"
}
$expectedHostExecutablePath = [IO.Path]::GetFullPath((Join-Path $InstallRoot "Rot.BrowserHost.exe"))
$expectedManifestPath = if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    Join-Path $ManifestDirectory "$hostName.json"
} else {
    $ManifestPath
}
$expectedManifestPath = [IO.Path]::GetFullPath($expectedManifestPath)

function Test-OwnedHostManifest {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$HostName,
        [Parameter(Mandatory)]
        [string]$ExpectedHostExecutablePath
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    try {
        $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        if ($manifest.name -ne $HostName -or $manifest.type -ne "stdio" -or
            [string]::IsNullOrWhiteSpace($manifest.path)) {
            return $false
        }
        return [IO.Path]::GetFullPath($manifest.path) -eq $ExpectedHostExecutablePath
    } catch {
        return $false
    }
}

$manifestIsOwned = Test-OwnedHostManifest -Path $expectedManifestPath -HostName $hostName -ExpectedHostExecutablePath $expectedHostExecutablePath
if (-not $manifestIsOwned) {
    Write-Warning "Skipped native host uninstall because the manifest is missing or belongs to another Rot installation: $expectedManifestPath"
    return
}

$registryBases = @{
    Chrome = "HKCU:\Software\Google\Chrome\NativeMessagingHosts"
    Edge = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts"
}
$browsers = if ($Browser -eq "Both") { @("Chrome", "Edge") } else { @($Browser) }
foreach ($browserName in $browsers) {
    $registrationPath = Join-Path $registryBases[$browserName] $hostName
    if (-not (Test-Path -LiteralPath $registrationPath)) {
        continue
    }

    $registeredManifestPath = (Get-Item -LiteralPath $registrationPath).GetValue("")
    $registeredManifestFullPath = $null
    try {
        if (-not [string]::IsNullOrWhiteSpace($registeredManifestPath)) {
            $registeredManifestFullPath = [IO.Path]::GetFullPath($registeredManifestPath)
        }
    } catch {
        $registeredManifestFullPath = $null
    }
    if ($registeredManifestFullPath -ne $expectedManifestPath) {
        Write-Warning "Skipped non-Rot or mismatched native host registration: $registrationPath"
        continue
    }

    if ($PSCmdlet.ShouldProcess($registrationPath, "Remove exact current-user native messaging registration")) {
        Remove-Item -LiteralPath $registrationPath -Recurse -Force
    }
}

$remainingReference = $false
foreach ($browserName in @("Chrome", "Edge")) {
    $registrationPath = Join-Path $registryBases[$browserName] $hostName
    if (-not (Test-Path -LiteralPath $registrationPath)) {
        continue
    }

    try {
        $registeredManifestPath = (Get-Item -LiteralPath $registrationPath).GetValue("")
        if (-not [string]::IsNullOrWhiteSpace($registeredManifestPath) -and
            [IO.Path]::GetFullPath($registeredManifestPath) -eq $expectedManifestPath) {
            $remainingReference = $true
            break
        }
    } catch {
        # A malformed foreign registration is left untouched and does not make
        # this exact manifest appear owned by another browser registration.
    }
}

if (-not $remainingReference -and $PSCmdlet.ShouldProcess($expectedManifestPath, "Remove exact current-user native messaging manifest")) {
    Remove-Item -LiteralPath $expectedManifestPath -Force
}
