[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "",
    [string]$DotNet = ""
)

$ErrorActionPreference = "Stop"
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $repository "src\Rot.App\Rot.App.csproj"
$helperProjects = @(
    (Join-Path $repository "src\Rot.BrowserHost\Rot.BrowserHost.csproj"),
    (Join-Path $repository "src\Rot.Updater\Rot.Updater.csproj")
)
$Output = if ([string]::IsNullOrWhiteSpace($Output)) { Join-Path $repository "dist\Rot-win-x64" } else { $Output }
$resolvedOutput = [IO.Path]::GetFullPath($Output)
$publishRoot = [IO.Path]::GetFullPath((Join-Path $repository "dist")).TrimEnd([IO.Path]::DirectorySeparatorChar)

function Assert-SafeDirectoryTarget {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Label
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $rootPath = [IO.Path]::GetPathRoot($fullPath).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $publishPrefix = $publishRoot + [IO.Path]::DirectorySeparatorChar
    if ([string]::IsNullOrWhiteSpace($fullPath) -or
        [string]::Equals($fullPath, $rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($fullPath, $repository.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($fullPath, $publishRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not $fullPath.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label is not a safe publish directory: $fullPath"
    }

    if ([string]::IsNullOrWhiteSpace([IO.Path]::GetFileName($fullPath))) {
        throw "$Label must name a concrete directory: $fullPath"
    }
}

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Rot project not found: $project"
}

Assert-SafeDirectoryTarget -Path $resolvedOutput -Label "Output"
if (Test-Path -LiteralPath $resolvedOutput -PathType Leaf) {
    throw "Publish output is a file, not a directory: $resolvedOutput"
}

if ([string]::IsNullOrWhiteSpace($DotNet)) {
    $bundledDotNet = Join-Path $repository "artifacts\tools\dotnet10\dotnet.exe"
    $DotNet = if (Test-Path -LiteralPath $bundledDotNet -PathType Leaf) { $bundledDotNet } else { "dotnet" }
}

$outputParent = [IO.Path]::GetDirectoryName($resolvedOutput)
if ([string]::IsNullOrWhiteSpace($outputParent)) {
    throw "Publish output has no parent directory: $resolvedOutput"
}

New-Item -ItemType Directory -Force -Path $outputParent | Out-Null
$outputName = [IO.Path]::GetFileName($resolvedOutput.TrimEnd([IO.Path]::DirectorySeparatorChar))
$stagingOutput = [IO.Path]::GetFullPath((Join-Path $outputParent ".$outputName.stage-$([Guid]::NewGuid().ToString('N'))"))
Assert-SafeDirectoryTarget -Path $stagingOutput -Label "Staging output"

$required = @(
    "Rot.exe",
    "Rot.dll",
    "Rot.BrowserHost.exe",
    "Rot.BrowserHost.dll",
    "Rot.Updater.exe",
    "Rot.Updater.dll",
    "LICENSE",
    "THIRD-PARTY-NOTICES.md",
    "browser-extension\manifest.json",
    "scripts\install-browser-host.ps1",
    "scripts\uninstall-browser-host.ps1",
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

try {
    & $DotNet publish $project `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false `
        --output $stagingOutput

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    foreach ($helperProject in $helperProjects) {
        if (-not (Test-Path -LiteralPath $helperProject -PathType Leaf)) {
            throw "Missing helper project: $helperProject"
        }
        & $DotNet publish $helperProject --configuration $Configuration --runtime $Runtime `
            --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
            --output $stagingOutput
        if ($LASTEXITCODE -ne 0) {
            throw "Helper publish failed with exit code $LASTEXITCODE"
        }
    }

    foreach ($document in @("LICENSE", "README.md", "THIRD-PARTY-NOTICES.md", "CONTRIBUTING.md", "VALIDATION.md", "DECISIONS.md")) {
        Copy-Item -LiteralPath (Join-Path $repository $document) -Destination $stagingOutput
    }
    foreach ($directory in @("licenses", "browser-extension", "docs", "assets")) {
        Copy-Item -LiteralPath (Join-Path $repository $directory) -Destination $stagingOutput -Recurse
    }
    $scriptOutput = Join-Path $stagingOutput "scripts"
    New-Item -ItemType Directory -Path $scriptOutput -Force | Out-Null
    foreach ($script in @("install-browser-host.ps1", "uninstall-browser-host.ps1")) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $scriptOutput
    }

    foreach ($item in $required) {
        $path = Join-Path $stagingOutput $item
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Published output is incomplete; missing $item"
        }
    }

    $obsoleteSearch = Join-Path $stagingOutput "Web\search"
    if (Test-Path -LiteralPath $obsoleteSearch) {
        throw "Published output contains the retired Web\search surface."
    }

    if (Test-Path -LiteralPath $resolvedOutput) {
        Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
    }

    Move-Item -LiteralPath $stagingOutput -Destination $resolvedOutput
}
finally {
    if (Test-Path -LiteralPath $stagingOutput) {
        Assert-SafeDirectoryTarget -Path $stagingOutput -Label "Staging cleanup target"
        Remove-Item -LiteralPath $stagingOutput -Recurse -Force
    }
}

Write-Host "[rot] Portable build ready: $resolvedOutput"
