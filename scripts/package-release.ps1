[CmdletBinding()]
param(
    [string]$PublishDirectory = ""
)

$ErrorActionPreference = "Stop"
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$distRoot = [IO.Path]::GetFullPath((Join-Path $repository "dist"))
$publish = if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    Join-Path $distRoot "Rot-win-x64"
} else {
    [IO.Path]::GetFullPath($PublishDirectory)
}
$prefix = $distRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $publish.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($publish) -ne "Rot-win-x64") {
    throw "Choose a Rot-win-x64 publish folder beneath this repository's dist folder."
}
foreach ($required in @("Rot.exe", "Rot.dll", "Rot.BrowserHost.exe", "Rot.Updater.exe", "LICENSE", "Web\player\index.html")) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required) -PathType Leaf)) {
        throw "Incomplete portable build: missing $required"
    }
}
foreach ($entry in Get-ChildItem -LiteralPath $publish -Recurse -Force) {
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release contents must not contain reparse points: $($entry.FullName)"
    }
    if ($entry.Name -match '^(\.git|settings\.v1\.json|WebView2|Validation)$' -or
        $entry.Extension -in @(".log", ".jsonl", ".bundle", ".patch")) {
        throw "Release contents include local data or review evidence: $($entry.FullName)"
    }
}

$releaseRoot = Join-Path $repository "artifacts\release"
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packages = @(
    @{ Name = "Rot-win-x64.zip"; Source = $publish; IncludeRoot = $true },
    @{ Name = "Send-to-Rot.zip"; Source = (Join-Path $repository "browser-extension"); IncludeRoot = $false }
)
$hashes = @()
foreach ($package in $packages) {
    $destination = Join-Path $releaseRoot $package.Name
    $temporary = Join-Path $releaseRoot ("." + [Guid]::NewGuid().ToString("N") + ".zip")
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory(
            $package.Source, $temporary, [IO.Compression.CompressionLevel]::Optimal, $package.IncludeRoot)
        $zip = [IO.Compression.ZipFile]::OpenRead($temporary)
        try {
            if ($zip.Entries.Count -eq 0) { throw "Empty release package: $($package.Name)" }
        } finally {
            $zip.Dispose()
        }
        Move-Item -LiteralPath $temporary -Destination $destination -Force
        $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        $hashes += "$hash  $($package.Name)"
    } finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}
[IO.File]::WriteAllText((Join-Path $releaseRoot "SHA256SUMS"), ($hashes -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "[rot] Release packages and SHA256SUMS ready: $releaseRoot"
