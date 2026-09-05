[CmdletBinding()]
param(
    [string]$Branch = "codex/rot-standalone-handoff",
    [string]$PublishDirectory = ""
)

$ErrorActionPreference = "Stop"
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repository "artifacts"))
$distRoot = [IO.Path]::GetFullPath((Join-Path $repository "dist"))
$PublishDirectory = if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    Join-Path $distRoot "Rot-win-x64"
} else {
    $PublishDirectory
}
$resolvedPublish = [IO.Path]::GetFullPath($PublishDirectory)
$requiredPublishFiles = @(
    "Rot.exe",
    "Rot.dll",
    "Web\BRIDGE.md",
    "Web\common\bridge.js",
    "Web\common\constants.js",
    "Web\common\theme.css",
    "Web\common\youtube.js",
    "Web\player\index.html",
    "Web\player\player.css",
    "Web\player\player.js",
    "Web\player\pass-through.js",
    "Web\settings\index.html",
    "Web\settings\settings.css",
    "Web\settings\settings.js",
    "Web\assets\icon-color.png",
    "Web\assets\icon-gray.png",
    "Web\assets\launcher.ico",
    "Web\assets\splash.png",
    "Web\assets\window-icon.png",
    "runtimes\win-x64\native\WebView2Loader.dll"
)

function Assert-SafeChild {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string]$Label
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix = $fullRoot + [IO.Path]::DirectorySeparatorChar
    if ([string]::IsNullOrWhiteSpace($fullPath) -or
        [string]::Equals($fullPath, $fullRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label is not a concrete child of $fullRoot`: $fullPath"
    }

    return $fullPath
}

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)

    & git -C $repository @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Assert-PublishContents {
    param(
        [Parameter(Mandatory)]
        [string]$Directory
    )

    foreach ($item in $requiredPublishFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $item) -PathType Leaf)) {
            throw "Portable publish is incomplete; missing $item"
        }
    }
    if (Test-Path -LiteralPath (Join-Path $Directory "Web\search")) {
        throw "The portable publish still contains the retired Web\search surface."
    }
}

$publishPrefix = $distRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedPublish.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $resolvedPublish -PathType Container)) {
    throw "A verified portable publish beneath dist is required: $resolvedPublish"
}
Assert-PublishContents -Directory $resolvedPublish

$sourceWeb = [IO.Path]::GetFullPath((Join-Path $repository "src\Rot.App\Web"))
foreach ($source in Get-ChildItem -LiteralPath $sourceWeb -Recurse -File) {
    $relative = $source.FullName.Substring($sourceWeb.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
    $publishedAsset = Join-Path (Join-Path $resolvedPublish "Web") $relative
    if (-not (Test-Path -LiteralPath $publishedAsset -PathType Leaf) -or
        (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $publishedAsset -Algorithm SHA256).Hash) {
        throw "Portable Web asset does not match current source: $relative"
    }
}

$sourceAssets = [IO.Path]::GetFullPath((Join-Path $repository "assets"))
foreach ($source in Get-ChildItem -LiteralPath $sourceAssets -File) {
    $publishedAsset = Join-Path (Join-Path $resolvedPublish "Web\assets") $source.Name
    if (-not (Test-Path -LiteralPath $publishedAsset -PathType Leaf) -or
        (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $publishedAsset -Algorithm SHA256).Hash) {
        throw "Portable app asset does not match current source: $($source.Name)"
    }
}

$projectSources = @(Get-ChildItem -LiteralPath (Join-Path $repository "src\Rot.App") -Recurse -File |
    Where-Object { $_.FullName -notmatch "[\\/](?:bin|obj)[\\/]" }) +
    @(Get-ChildItem -LiteralPath $sourceAssets -File)
$latestProjectSource = $projectSources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$publishedAssembly = Get-Item -LiteralPath (Join-Path $resolvedPublish "Rot.dll")
if ($null -eq $latestProjectSource -or $publishedAssembly.LastWriteTimeUtc -lt $latestProjectSource.LastWriteTimeUtc) {
    throw "Portable publish predates the current project source; run scripts\publish.ps1 again."
}

$status = & git -C $repository status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0) {
    throw "git status failed with exit code $LASTEXITCODE"
}
if ($status) {
    throw "Commit all tracked changes before generating the portable handoff."
}

$currentBranch = (& git -C $repository branch --show-current).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "git branch --show-current failed with exit code $LASTEXITCODE"
}
if (-not [string]::Equals($currentBranch, $Branch, [StringComparison]::Ordinal)) {
    throw "The checked-out branch must be '$Branch'; current branch is '$currentBranch'."
}

$branchCommit = (& git -C $repository rev-parse --verify "$Branch^{commit}").Trim()
if ($LASTEXITCODE -ne 0) {
    throw "git rev-parse --verify $Branch^{commit} failed with exit code $LASTEXITCODE"
}
$headCommit = (& git -C $repository rev-parse --verify "HEAD^{commit}").Trim()
if ($LASTEXITCODE -ne 0) {
    throw "git rev-parse --verify HEAD^{commit} failed with exit code $LASTEXITCODE"
}
if (-not [string]::Equals($branchCommit, $headCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The checked-out HEAD must match the tip of '$Branch'."
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$stage = Assert-SafeChild `
    -Path (Join-Path $artifactRoot ".rot-v2-stage-$([Guid]::NewGuid().ToString('N'))") `
    -Root $artifactRoot `
    -Label "Artifact staging directory"
New-Item -ItemType Directory -Path $stage | Out-Null

$finalPortable = Assert-SafeChild -Path (Join-Path $artifactRoot "Rot-win-x64") -Root $artifactRoot -Label "Portable artifact"
$finalPatches = Assert-SafeChild -Path (Join-Path $artifactRoot "patches") -Root $artifactRoot -Label "Patch artifact"
$finalBundle = Assert-SafeChild -Path (Join-Path $artifactRoot "rot-standalone-handoff.bundle") -Root $artifactRoot -Label "Bundle artifact"
$finalZip = Assert-SafeChild -Path (Join-Path $artifactRoot "Rot-win-x64.zip") -Root $artifactRoot -Label "Zip artifact"
$finalHashes = Assert-SafeChild -Path (Join-Path $artifactRoot "SHA256SUMS.txt") -Root $artifactRoot -Label "Hash manifest"
$stalePreFinal = Assert-SafeChild -Path (Join-Path $distRoot "Rot-win-x64-pre-final") -Root $distRoot -Label "Stale pre-final publish"

try {
    $stagePortable = Join-Path $stage "Rot-win-x64"
    $stagePatches = Join-Path $stage "patches"
    $stageBundle = Join-Path $stage "rot-standalone-handoff.bundle"
    $stageZip = Join-Path $stage "Rot-win-x64.zip"
    $stageHashes = Join-Path $stage "SHA256SUMS.txt"

    Copy-Item -LiteralPath $resolvedPublish -Destination $stagePortable -Recurse
    Assert-PublishContents -Directory $stagePortable

    New-Item -ItemType Directory -Path $stagePatches | Out-Null
    Invoke-Git format-patch --binary --full-index --root --output-directory $stagePatches $Branch
    $patchFiles = @(Get-ChildItem -LiteralPath $stagePatches -Filter "*.patch" -File | Sort-Object Name)
    if ($patchFiles.Count -eq 0) {
        throw "git format-patch produced no portable patches."
    }

    Invoke-Git bundle create $stageBundle $Branch
    Invoke-Git bundle verify $stageBundle | Out-Null

    Compress-Archive -Path (Join-Path $stagePortable "*") -DestinationPath $stageZip -CompressionLevel Optimal

    $hashTargets = @(
        [pscustomobject]@{ Name = "Rot-win-x64/Rot.exe"; Path = (Join-Path $stagePortable "Rot.exe") },
        [pscustomobject]@{ Name = "Rot-win-x64.zip"; Path = $stageZip },
        [pscustomobject]@{ Name = "rot-standalone-handoff.bundle"; Path = $stageBundle }
    ) + @($patchFiles | ForEach-Object {
        [pscustomobject]@{ Name = "patches/$($_.Name)"; Path = $_.FullName }
    })
    $hashLines = @($hashTargets | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    })
    [IO.File]::WriteAllLines($stageHashes, $hashLines)

    foreach ($directory in @($finalPortable, $finalPatches)) {
        if (Test-Path -LiteralPath $directory) {
            Remove-Item -LiteralPath $directory -Recurse -Force
        }
    }
    foreach ($file in @($finalBundle, $finalZip, $finalHashes)) {
        if (Test-Path -LiteralPath $file) {
            Remove-Item -LiteralPath $file -Force
        }
    }

    Move-Item -LiteralPath $stagePortable -Destination $finalPortable
    Move-Item -LiteralPath $stagePatches -Destination $finalPatches
    Move-Item -LiteralPath $stageBundle -Destination $finalBundle
    Move-Item -LiteralPath $stageZip -Destination $finalZip
    Move-Item -LiteralPath $stageHashes -Destination $finalHashes

    if (Test-Path -LiteralPath $stalePreFinal) {
        Remove-Item -LiteralPath $stalePreFinal -Recurse -Force
    }
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

Write-Host "[rot] Portable folder: $finalPortable"
Write-Host "[rot] Zip: $finalZip"
Write-Host "[rot] Patch series: $finalPatches"
Write-Host "[rot] Bundle: $finalBundle"
Write-Host "[rot] Hashes: $finalHashes"
