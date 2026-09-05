import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { promisify } from "node:util";
import { join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const execFileAsync = promisify(execFile);
const repositoryRoot = fileURLToPath(new URL("../", import.meta.url));
const installerPath = join(repositoryRoot, "scripts", "install-browser-host.ps1");
const extensionManifestPath = join(repositoryRoot, "browser-extension", "manifest.json");

const powershellWrapper = String.raw`
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,
    [Parameter(Mandatory)]
    [string]$InstallRoot,
    [Parameter(Mandatory)]
    [string]$ExtensionDirectory,
    [Parameter(Mandatory)]
    [string]$ManifestDirectory,
    [ValidateSet("install", "whatif")]
    [string]$Mode = "install"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$script:registryCalls = @()

function Add-RegistryCall {
    param(
        [Parameter(Mandatory)]
        [string]$Operation,
        [Parameter(Mandatory)]
        [string]$Path,
        [AllowNull()]
        [object]$Value
    )

    $script:registryCalls += [pscustomobject]@{
        Operation = $Operation
        Path = $Path
        Value = $Value
    }
}

function New-Item {
    [CmdletBinding()]
    param(
        [string]$Path,
        [string]$LiteralPath,
        [string]$ItemType,
        [switch]$Force
    )

    $target = if ($PSBoundParameters.ContainsKey("LiteralPath")) { $LiteralPath } else { $Path }
    if ($target -like "HKCU:\*") {
        Add-RegistryCall -Operation "New-Item" -Path $target -Value $null
        return
    }

    $forward = @{}
    foreach ($key in $PSBoundParameters.Keys) {
        $forward[$key] = $PSBoundParameters[$key]
    }
    Microsoft.PowerShell.Management\New-Item @forward
}

function Set-Item {
    [CmdletBinding()]
    param(
        [string]$Path,
        [string]$LiteralPath,
        [object]$Value
    )

    $target = if ($PSBoundParameters.ContainsKey("LiteralPath")) { $LiteralPath } else { $Path }
    if ($target -like "HKCU:\*") {
        Add-RegistryCall -Operation "Set-Item" -Path $target -Value $Value
        return
    }

    $forward = @{}
    foreach ($key in $PSBoundParameters.Keys) {
        $forward[$key] = $PSBoundParameters[$key]
    }
    Microsoft.PowerShell.Management\Set-Item @forward
}

$parameters = @{
    InstallRoot = $InstallRoot
    ExtensionDirectory = $ExtensionDirectory
    ManifestDirectory = $ManifestDirectory
    Browser = "Both"
}
if ($Mode -eq "whatif") {
    $parameters.WhatIf = $true
}

$success = $false
$errorMessage = $null
$scriptOutput = @()
try {
    $scriptOutput = @(. $InstallerPath @parameters)
    $success = $true
} catch {
    $errorMessage = $_.Exception.Message
}
$result = if ($scriptOutput.Count -gt 0) { $scriptOutput[-1] } else { $null }
[pscustomobject]@{
    Success = $success
    Error = $errorMessage
    Result = $result
    RegistryCalls = @($script:registryCalls)
} | ConvertTo-Json -Depth 10 -Compress
`;

async function runInstallerWrapper(wrapperPath, paths, mode = "install") {
  const { stdout } = await execFileAsync("powershell.exe", [
    "-NoProfile",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    wrapperPath,
    "-InstallerPath",
    installerPath,
    "-InstallRoot",
    paths.installRoot,
    "-ExtensionDirectory",
    paths.extensionDirectory,
    "-ManifestDirectory",
    paths.manifestDirectory,
    "-Mode",
    mode,
  ], { maxBuffer: 1024 * 1024, windowsHide: true });
  const lines = stdout.trim().split(/\r?\n/).filter(Boolean);
  return JSON.parse(lines.at(-1));
}

function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}

const windowsTest = process.platform === "win32" ? test : test.skip;

windowsTest("installer creates and reuses its manifest directory without live registry writes", async (t) => {
  const testRoot = await mkdtemp(join(tmpdir(), "rot-install-browser-host-"));
  t.after(async () => rm(testRoot, { recursive: true, force: true }));

  const wrapperPath = join(testRoot, "installer-wrapper.ps1");
  await writeFile(wrapperPath, powershellWrapper, "utf8");
  const installRoot = join(testRoot, "install");
  const extensionDirectory = join(testRoot, "browser-extension");
  const manifestDirectory = join(testRoot, "manifest");
  await mkdir(installRoot, { recursive: true });
  await mkdir(extensionDirectory, { recursive: true });
  await writeFile(join(installRoot, "Rot.BrowserHost.exe"), "test host", "utf8");
  await writeFile(join(extensionDirectory, "manifest.json"), await readFile(extensionManifestPath));

  const paths = { installRoot, extensionDirectory, manifestDirectory };
  const manifestPath = join(manifestDirectory, "com.rot.send_to_rot.json");
  const expectedChromePath = "HKCU:\\Software\\Google\\Chrome\\NativeMessagingHosts\\com.rot.send_to_rot";
  const expectedEdgePath = "HKCU:\\Software\\Microsoft\\Edge\\NativeMessagingHosts\\com.rot.send_to_rot";

  const first = await runInstallerWrapper(wrapperPath, paths);
  assert.equal(first.Success, true, first.Error || "clean install failed");
  assert.deepEqual(first.RegistryCalls.map(({ Operation, Path, Value }) => ({ Operation, Path, Value })), [
    { Operation: "New-Item", Path: expectedChromePath, Value: null },
    { Operation: "Set-Item", Path: expectedChromePath, Value: resolve(manifestPath) },
    { Operation: "New-Item", Path: expectedEdgePath, Value: null },
    { Operation: "Set-Item", Path: expectedEdgePath, Value: resolve(manifestPath) },
  ]);
  assert.equal((await stat(manifestDirectory)).isDirectory(), true);
  const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
  assert.equal(manifest.name, "com.rot.send_to_rot");
  assert.equal(manifest.type, "stdio");
  assert.equal(manifest.path.toLowerCase(), resolve(join(installRoot, "Rot.BrowserHost.exe")).toLowerCase());
  assert.deepEqual(manifest.allowed_origins, ["chrome-extension://ajakpkcchbjafafhjbobkaobhgpdikjd/"]);

  const firstHash = sha256(await readFile(manifestPath));
  const second = await runInstallerWrapper(wrapperPath, paths);
  assert.equal(second.Success, true, second.Error || "idempotent install failed");
  assert.equal(sha256(await readFile(manifestPath)), firstHash);
  assert.equal(second.RegistryCalls.length, 4);

  const whatIfDirectory = join(testRoot, "what-if-manifest");
  const whatIf = await runInstallerWrapper(wrapperPath, { ...paths, manifestDirectory: whatIfDirectory }, "whatif");
  assert.equal(whatIf.Success, true, whatIf.Error || "WhatIf invocation failed");
  assert.deepEqual(whatIf.RegistryCalls, []);
  await assert.rejects(stat(whatIfDirectory), { code: "ENOENT" });

  const existingFilePath = join(testRoot, "manifest-path-file");
  await writeFile(existingFilePath, "preserve this file", "utf8");
  const existingFile = await runInstallerWrapper(wrapperPath, { ...paths, manifestDirectory: existingFilePath });
  assert.equal(existingFile.Success, false);
  assert.match(existingFile.Error, /existing file/i);
  assert.deepEqual(existingFile.RegistryCalls, []);
  assert.equal(await readFile(existingFilePath, "utf8"), "preserve this file");
});
