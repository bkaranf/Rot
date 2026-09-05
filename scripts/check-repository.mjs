import assert from "node:assert/strict";
import { access, readFile, readdir, stat } from "node:fs/promises";
import { dirname, extname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const sourceRoot = join(root, "src", "Rot.App");

async function exists(path) {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

async function filesUnder(directory) {
  if (!await exists(directory)) return [];
  const output = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if ([".git", ".vs", "artifacts", "bin", "obj", "node_modules", "dist", "TestResults"].includes(entry.name)) continue;
    const path = join(directory, entry.name);
    if (entry.isDirectory()) output.push(...await filesUnder(path));
    else output.push(path);
  }
  return output;
}

function pngDimensions(buffer) {
  const signature = "89504e470d0a1a0a";
  assert.equal(buffer.subarray(0, 8).toString("hex"), signature, "asset is not a PNG");
  return { width: buffer.readUInt32BE(16), height: buffer.readUInt32BE(20) };
}

const requiredFiles = [
  "Rot.sln",
  "src/Rot.App/Rot.App.csproj",
  "scripts/package-handoff.ps1",
  "src/Rot.App/App.xaml",
  "src/Rot.App/App.xaml.cs",
  "src/Rot.App/Views/PlayerWindow.xaml",
  "src/Rot.App/Views/PlayerWindow.xaml.cs",
  "src/Rot.App/Views/BrowseWindow.xaml",
  "src/Rot.App/Views/BrowseWindow.xaml.cs",
  "src/Rot.App/Views/SettingsWindow.xaml",
  "src/Rot.App/Views/SettingsWindow.xaml.cs",
  "src/Rot.App/Services/UserNotificationService.cs",
  "src/Rot.App/Services/ValidationSessionLogger.cs",
  "src/Rot.App/Services/RocketLeagueForegroundMonitor.cs",
  "src/Rot.App/Web/player/index.html",
  "src/Rot.App/Web/player/player.css",
  "src/Rot.App/Web/player/player.js",
  "src/Rot.App/Web/player/pass-through.js",
  "src/Rot.App/Web/settings/index.html",
  "src/Rot.App/Web/settings/settings.css",
  "src/Rot.App/Web/settings/settings.js",
  "src/Rot.App/Web/common/youtube.js",
  "tests/Rot.App.Tests/Rot.App.Tests.csproj",
  "tests/Rot.App.Tests/RocketLeagueForegroundMonitorTests.cs",
  "tests/pass-through-ui.test.js",
  "tests/player-ui.test.js",
  "tests/settings-ui.test.js",
  "tests/youtube.test.js",
  "README.md",
  "HANDOFF.md",
  "DECISIONS.md",
  "VALIDATION.md",
];
for (const required of requiredFiles) {
  assert.equal(await exists(join(root, ...required.split("/"))), true, `missing ${required}`);
}

assert.equal(await exists(join(root, "manifest.json")), false, "the abandoned Overwolf manifest must be deleted");
for (const legacy of ["background", "common", "desktop", "overlay", "search"]) {
  assert.equal(await exists(join(root, "src", legacy)), false, `legacy Overwolf tree remains: src/${legacy}`);
}
for (const retired of [
  "src/Rot.App/Views/SearchWindow.xaml",
  "src/Rot.App/Views/SearchWindow.xaml.cs",
  "src/Rot.App/Web/search",
]) {
  assert.equal(await exists(join(root, ...retired.split("/"))), false, `retired surface remains: ${retired}`);
}

const sourceFiles = [
  ...await filesUnder(sourceRoot),
  ...await filesUnder(join(root, "tests")),
].filter((path) => [".cs", ".csproj", ".xaml", ".js", ".mjs", ".html", ".css", ".json", ".xml"].includes(extname(path).toLowerCase()));
const texts = new Map(await Promise.all(sourceFiles.map(async (path) => [path, await readFile(path, "utf8")])));
const joined = [...texts.entries()].map(([path, text]) => `${relative(root, path)}\n${text}`).join("\n");
const lower = joined.toLowerCase();

const documentationFiles = [
  "README.md",
  "HANDOFF.md",
  "DECISIONS.md",
  "VALIDATION.md",
  "src/Rot.App/Web/BRIDGE.md",
];
const documentation = (await Promise.all(documentationFiles.map(async (path) =>
  `${path}\n${await readFile(join(root, ...path.split("/")), "utf8")}`))).join("\n");
const productAndDocumentation = `${joined}\n${documentation}`;

for (const forbidden of [
  "overwolf.",
  "game_ids",
  "rocket league tracker",
  "setplaybackquality",
  "getplaybackquality",
  "webview2compositioncontrol",
  "readprocessmemory",
  "writeprocessmemory",
  "createremotethread",
  "virtualallocex",
  "setwindowshookex",
  "setwineventhook",
  "registershellhookwindow",
  "unhookwinevent",
  "windows.graphics.capture",
  "graphicscapturesession",
  "bitblt(",
  "printwindow(",
  "copyfromscreen",
  "sendinput(",
  "keybd_event",
  "mouse_event",
  "loadlibrary(",
  "setmatchpaused",
  "sethudvisibility",
]) {
  assert.equal(lower.includes(forbidden), false, `forbidden source reference: ${forbidden}`);
}

assert.equal(/AllowsTransparency\s*=\s*["']True["']/i.test(joined), false, "layered WPF windows are forbidden");
assert.equal(/<iframe[^>]+src=["']https:\/\/(?:www\.)?youtube\.com\/?["']/i.test(joined), false, "YouTube's homepage must never be framed");

assert.equal(/\bSearchWindow\b/.test(productAndDocumentation), false, "the retired SearchWindow remains");
assert.equal(
  /\bToggleSearch\b|\btoggleSearch\b|WebViewKind\.Search|["']show-search["']/i.test(productAndDocumentation),
  false,
  "the retired Search window bridge/hotkey contract remains",
);
assert.equal(/\bapi[\s_-]*key\b/i.test(productAndDocumentation), false, "the retired search credential remains");
assert.equal(
  /youtube\/v3\/search|googleapis\.com\/youtube|YouTubeSearchClient|buildSearchUrl|user-supplied\s+key/i
    .test(productAndDocumentation),
  false,
  "the retired Data API search integration remains",
);
assert.equal(
  /quotaExceeded|SearchCallsUsed|SearchCallsLocalDate|searchQuota|search\.record|estimateSearchesRemaining/i
    .test(productAndDocumentation),
  false,
  "the retired search quota integration remains",
);
assert.equal(/console\.cloud\.google\.com/i.test(productAndDocumentation), false, "the retired Cloud Console link remains");
assert.equal(
  /\bQueueItem\b|MAX_QUEUE_LENGTH|advanceQueue|queue-(?:count|badge)|["']queue\.(?:add|remove|move|clear|play|dequeue)["']|["']media\.select["']|data-(?:tab|panel)=["']queue["']|\bstate\.queue\b/i
    .test(productAndDocumentation),
  false,
  "the retired custom video collection remains",
);

const windowClasses = [...texts.entries()]
  .filter(([path]) => extname(path).toLowerCase() === ".xaml" && /[\\/]Views[\\/]/.test(path))
  .flatMap(([, text]) => [...text.matchAll(/<Window\s+[^>]*x:Class=["']([^"']+)["']/g)].map((match) => match[1]))
  .sort();
assert.deepEqual(windowClasses, [
  "Rot.App.Views.BrowseWindow",
  "Rot.App.Views.PlayerWindow",
  "Rot.App.Views.SettingsWindow",
], "Rot must have exactly Player, Browse, and Settings windows");

const browseWindowPath = join(root, "src", "Rot.App", "Views", "BrowseWindow.xaml.cs");
const browseWindow = await readFile(browseWindowPath, "utf8");
for (const eventName of [
  "IsMutedChanged",
  "SourceChanged",
  "HistoryChanged",
  "NavigationStarting",
  "NewWindowRequested",
  "DownloadStarting",
  "PermissionRequested",
]) {
  assert.match(browseWindow, new RegExp(`\\.${eventName}\\s*\\+=`), `BrowseWindow must handle ${eventName}`);
}
assert.match(browseWindow, /\.IsMuted\s*=\s*true/, "Browse WebView must be muted during initialization");
assert.doesNotMatch(browseWindow, /\.IsMuted\s*=\s*false/, "Browse WebView must never be unmuted");
assert.match(browseWindow, /(?:_core|core|CoreWebView2)\??\.Source\b/, "Browse picks must be read from CoreWebView2.Source");
assert.match(browseWindow, /CoreWebView2PermissionState\.Deny/, "Browse permissions must be denied");
assert.match(browseWindow, /\.Cancel\s*=\s*true/, "Browse navigation and downloads must be cancellable");
assert.match(browseWindow, /\.Handled\s*=\s*true/, "Browse popups and permission requests must be handled");
assert.match(browseWindow, /ObserveSuspensionCompletionAsync/, "late Browse suspension must be observed");
assert.match(browseWindow, /_shouldBeVisible[\s\S]*?\.Resume\(\)/, "late Browse suspension must recover a reopened view");

const applicationController = await readFile(
  join(root, "src", "Rot.App", "Services", "ApplicationController.cs"),
  "utf8",
);
const appXaml = await readFile(join(root, "src", "Rot.App", "App.xaml"), "utf8");
assert.match(
  appXaml,
  /ShutdownMode\s*=\s*["']OnExplicitShutdown["']/,
  "the windowless Rot host must remain alive to detect a later game launch",
);
assert.doesNotMatch(
  joined,
  /CurrentVersion\\Run(?:Once)?|RegistryKey\.(?:CreateSubKey|OpenBaseKey)|\bschtasks\b|\bTaskService\b|SpecialFolder\.Startup|IWshShortcut/i,
  "Rot must not silently install an autostart or launcher mutation",
);

const foregroundChangeStart = applicationController.indexOf("private void ApplyRocketLeagueForegroundChange");
const foregroundChangeEnd = applicationController.indexOf("private void OnStatsEnvelopeReceived", foregroundChangeStart);
assert.ok(foregroundChangeStart >= 0 && foregroundChangeEnd > foregroundChangeStart);
const foregroundChangeHandler = applicationController.slice(foregroundChangeStart, foregroundChangeEnd);
assert.doesNotMatch(
  foregroundChangeHandler,
  /Application\.Current\.Shutdown/,
  "game exit must leave the windowless monitor host dormant rather than terminating Rot",
);
assert.match(
  foregroundChangeHandler,
  /if\s*\(change\.ProcessChanged\)[\s\S]*?_detection\.SetConnected\(false\)[\s\S]*?_verifiedLocalProcessEpoch\s*=\s*-1[\s\S]*?QueueDetectionEffect/,
  "a game process edge must reset stale detection evidence and queue presentation teardown",
);

const detectionEffectStart = applicationController.indexOf("private async Task ApplyDetectionEffectAsync");
const detectionEffectEnd = applicationController.indexOf("private bool IsPlayerEffectCurrent", detectionEffectStart);
assert.ok(detectionEffectStart >= 0 && detectionEffectEnd > detectionEffectStart);
const detectionEffect = applicationController.slice(detectionEffectStart, detectionEffectEnd);
assert.match(
  detectionEffect,
  /SetWebMuted\(true\)[\s\S]*?SendPlayerCommandAsync\([\s\S]*?"pause"[\s\S]*?_playerWindow\.Hide\(\)/,
  "lifecycle teardown must core-mute, dispatch pause, and hide the Player in that order",
);
assert.match(
  applicationController,
  /ShowOneLine\(message\)/,
  "blocked Browse must provide a nonactivating visible reason",
);
assert.match(
  applicationController,
  /TryCaptureCurrentProcessInteraction[\s\S]*?TryGetForegroundInteractionGrant\(out grant\)[\s\S]*?_detectionProcessEpoch\s*==\s*grant\.ProcessEpoch/,
  "interaction must synchronously verify foreground ownership and the current game process epoch",
);
assert.match(
  applicationController,
  /ShouldShowLocalPlayer\(\)[\s\S]*?StatsDetectionState\.Local[\s\S]*?TryCaptureCurrentProcessInteraction[\s\S]*?_verifiedLocalProcessEpoch\s*==\s*interactionGrant\.ProcessEpoch/,
  "focus or process regain must restore the Player only from current-process Local evidence",
);
assert.match(
  applicationController,
  /IsPlayerEffectCurrent[\s\S]*?detectionEpoch\s*==\s*_detection\.Epoch[\s\S]*?focusLeaseEpoch\s*==\s*_foregroundMonitor\.LeaseEpoch[\s\S]*?processEpoch\s*==\s*_foregroundMonitor\.ProcessEpoch/,
  "presentation effects must be guarded by detection, focus-lease, and process epochs",
);
assert.match(
  applicationController,
  /SetWebMuted\(true\)[\s\S]*?SendPlayerCommandAsync\([\s\S]*?"pause"[\s\S]*?_playerWindow\.Hide\(\)/,
  "focus teardown must core-mute, dispatch pause, and hide in that order",
);
assert.match(
  applicationController,
  /HideAuxiliaryWindowsForExternalFocusAsync[\s\S]*?restoreFocus:\s*false/,
  "external focus loss must close Rot surfaces without stealing focus back",
);
assert.match(
  applicationController,
  /CanRestoreFocusToRocketLeague\(focusReturnWindow\)/,
  "delayed auxiliary teardown must re-check foreground and target ownership before restoring focus",
);
assert.match(
  applicationController,
  /if\s*\(!change\.LeaseChanged\)[\s\S]*?return;/,
  "owner-only Game/Rot changes must not replay Player presentation effects",
);
assert.match(
  applicationController,
  /change\.Owner\s*==\s*ForegroundOwner\.External[\s\S]*?FocusLeaseVersioning\.PredatesRevocation[\s\S]*?var changeIsCurrent/,
  "lease loss must invalidate older manual/auxiliary resources even when its UI effect is stale",
);
assert.match(
  applicationController,
  /TryAcceptStatsSignalForCurrentProcess[\s\S]*?TryGetProcessEpochForEvidence\([\s\S]*?triggeredAt,[\s\S]*?out evidenceProcessEpoch\)/,
  "Stats callbacks must atomically capture the process epoch that accepted their evidence",
);
assert.match(
  applicationController,
  /transition\.Current\s*==\s*StatsDetectionState\.Local[\s\S]*?_verifiedLocalProcessEpoch\s*=\s*evidenceProcessEpoch/,
  "only a Local transition may stamp the atomically captured process epoch as verified",
);
assert.match(
  applicationController,
  /HandleBrowseInputAsync\([\s\S]*?long focusLeaseEpoch,[\s\S]*?long processEpoch[\s\S]*?IsBrowseOperationCurrent/,
  "Browse asynchronous paths must carry detection, focus-lease, and process epochs",
);
assert.match(
  applicationController,
  /OnHotKeyPressed[\s\S]*?if\s*\(!TryCaptureCurrentProcessInteraction\(out var interactionGrant\)\)[\s\S]*?RecordInteractionIgnoredOutsideRocketLeague[\s\S]*?return;/,
  "global Rot hotkeys must remain inert while Rocket League is absent or external",
);
assert.match(
  applicationController,
  /change\.Owner\s*==\s*ForegroundOwner\.External\s*\|\|[\s\S]*?!change\.IsProcessRunning\s*\|\|[\s\S]*?change\.ProcessChanged[\s\S]*?HideAuxiliaryWindowsForExternalFocusAsync/,
  "foreground loss, game exit, or process replacement must close stale auxiliary surfaces",
);

const auxiliaryHideStart = applicationController.indexOf("private Task HideAuxiliaryWindowsForExternalFocusAsync");
const auxiliaryHideEnd = applicationController.indexOf("private static bool ShouldInvalidateResource", auxiliaryHideStart);
assert.ok(auxiliaryHideStart >= 0 && auxiliaryHideEnd > auxiliaryHideStart);
const auxiliaryHide = applicationController.slice(auxiliaryHideStart, auxiliaryHideEnd);
assert.match(
  auxiliaryHide,
  /HideBrowseAndRestoreFocusAsync\([\s\S]*?restoreFocus:\s*false\)[\s\S]*?_browseWindow\.Hide\(\)/,
  "external/process teardown must safely hide Browse without restoring focus",
);
assert.match(
  auxiliaryHide,
  /HideSettingsAndRestoreFocusAsync\([\s\S]*?restoreFocus:\s*false\)[\s\S]*?_settingsWindow\.Hide\(\)/,
  "external/process teardown must safely hide Settings without restoring focus",
);
assert.match(
  applicationController,
  /ShowForInteraction\(focusInput:\s*false,\s*activateOnShow:\s*false\)/,
  "Browse initialization must be nonactivating until its captured interaction grant is revalidated",
);
assert.match(
  applicationController,
  /ShowForInteraction\(focusBrowser:\s*false,\s*activateOnShow:\s*false\)/,
  "Settings initialization must be nonactivating until its captured interaction grant is revalidated",
);
assert.match(
  applicationController,
  /RefusePlayerInteraction[\s\S]*?if\s*\(!AllowsCurrentProcessInteractionNow\(\)\)[\s\S]*?RecordInteractionIgnoredOutsideRocketLeague[\s\S]*?return;[\s\S]*?ShowOneLine\(message\)/,
  "a delayed refusal must revalidate the game interaction grant before showing a notification",
);

const foregroundMonitor = await readFile(
  join(root, "src", "Rot.App", "Services", "RocketLeagueForegroundMonitor.cs"),
  "utf8",
);
assert.match(
  foregroundMonitor,
  /DefaultPollInterval\s*=\s*TimeSpan\.FromMilliseconds\(100\)/,
  "foreground ownership must be sampled every 100 ms",
);
assert.match(
  foregroundMonitor,
  /_pollInterval\s*=\s*pollInterval\s*\?\?\s*DefaultPollInterval/,
  "production monitor construction must use the asserted 100 ms default interval",
);
assert.match(
  foregroundMonitor,
  /_timer\s*\?\?=\s*new Timer\([\s\S]*?PollNow\(\)[\s\S]*?_pollInterval,[\s\S]*?_pollInterval\)/,
  "the running monitor must schedule the combined process and foreground sample at its 100 ms interval",
);
assert.match(
  foregroundMonitor,
  /leaseBeforeOwnerReconciliation\s*=\s*processChanged[\s\S]*?false[\s\S]*?HasRocketLeagueFocusLease[\s\S]*?ForegroundOwner\.Rot\s*=>\s*leaseBeforeOwnerReconciliation/,
  "Rot-owned Browse and Settings must preserve, but never grant, the game focus lease",
);
assert.match(
  foregroundMonitor,
  /focusLease\s*!=\s*previousFocusLease[\s\S]*?LeaseEpoch\+\+/,
  "the presentation guard epoch must advance only when the focus lease changes",
);
assert.match(
  foregroundMonitor,
  /TryGetForegroundInteractionGrant[\s\S]*?lock\s*\(_sync\)[\s\S]*?ReadProcessSession\(\)[\s\S]*?ReadOwner\(\)[\s\S]*?_policy\.Observe/,
  "synchronous authorization must serialize the HWND sample with policy reconciliation",
);
assert.match(
  foregroundMonitor,
  /GetWindowThreadProcessId/,
  "foreground classification must resolve the owning process from its HWND",
);
const exactProcessNames = foregroundMonitor.match(
  /ExactProcessNames[\s\S]*?Array\.AsReadOnly\(\[([^\]]+)]\)/,
);
assert.ok(exactProcessNames, "exact Rocket League process-name list is missing");
assert.deepEqual(
  [...exactProcessNames[1].matchAll(/["']([^"']+)["']/g)].map((match) => match[1]),
  ["RocketLeague", "RocketLeague_EAC"],
  "process presence must query exactly main Rocket League and EAC, with no extra names",
);
assert.match(foregroundMonitor, /GetProcessesByName\(processName\)/);
assert.match(
  foregroundMonitor,
  /processChanged\s*=\s*!Equals\(processSession,\s*ProcessSession\)[\s\S]*?ProcessEpoch\+\+[\s\S]*?CurrentProcessStartedAt\s*=\s*isProcessRunning/,
  "each name/PID/start-identity change must advance a timestamped process epoch",
);
assert.match(
  foregroundMonitor,
  /TryGetProcessEpochForEvidence\(long observedAt,\s*out long processEpoch\)[\s\S]*?_policy\.IsProcessRunning[\s\S]*?observedAt\s*>=\s*startedAt[\s\S]*?processEpoch\s*=\s*_policy\.ProcessEpoch/,
  "current-process evidence must atomically return a running epoch no older than its observed start",
);
assert.match(
  foregroundMonitor,
  /CanRestoreFocusToRocketLeague\(nint targetWindow\)[\s\S]*?ReadOwner\(targetWindow\)\s*==\s*ForegroundOwner\.RocketLeague/,
  "focus restoration must verify that the saved target HWND still belongs to Rocket League",
);

const browseSources = [...texts.entries()]
  .filter(([path]) => path.startsWith(sourceRoot) && /browse/i.test(relative(root, path)))
  .map(([, text]) => text)
  .join("\n");
for (const host of [
  "youtube.com",
  "www.youtube.com",
  "m.youtube.com",
  "consent.youtube.com",
  "accounts.google.com",
  "ytimg.com",
  "ggpht.com",
  "gstatic.com",
  "googlevideo.com",
]) {
  const escapedHost = host.replaceAll(".", "\\.");
  assert.match(browseSources, new RegExp(`["']${escapedHost}["']`), `Browse allowlist is missing ${host}`);
}
for (const suffix of [".ytimg.com", ".ggpht.com", ".gstatic.com", ".googlevideo.com"]) {
  const escapedSuffix = suffix.replaceAll(".", "\\.");
  assert.match(browseSources, new RegExp(`["']${escapedSuffix}["']`), `Browse wildcard allowlist is missing ${suffix}`);
}
for (const forbiddenBrowseApi of [
  "AddHostObjectToScript",
  "AddScriptToExecuteOnDocumentCreatedAsync",
  "ExecuteScriptAsync",
  "CallDevToolsProtocolMethodAsync",
  "NavigateToString",
  "WebResourceRequested",
  "UserAgent",
]) {
  assert.equal(
    browseSources.includes(forbiddenBrowseApi),
    false,
    `Browse must not inject into or modify YouTube: ${forbiddenBrowseApi}`,
  );
}

assert.match(joined, /ClientWebSocket/, "Stats API must use ClientWebSocket");
assert.match(joined, /ReceiveAsync/, "Stats API client must receive WebSocket data");

const statsSources = [...texts.entries()]
  .filter(([path]) => /Stats|MatchLifecycle|RocketLeague/i.test(relative(root, path)))
  .map(([, text]) => text)
  .join("\n");
assert.equal(/\bSendAsync\s*\(/.test(statsSources), false, "Stats API code must never send application messages");
assert.match(joined, /ws:\/\/127\.0\.0\.1:49124\/?/, "Stats API must use loopback WebSocket port 49124");
assert.match(joined, /PacketSendRate/, "Stats config repair is missing");
assert.match(joined, /WebPort/, "Stats config repair is missing WebPort");
assert.match(joined, /SetVirtualHostNameToFolderMapping/, "WebView2 virtual HTTPS host mapping is missing");
assert.match(joined, /https:\/\/rot\.local/, "rot.local HTTPS origin is missing");
assert.match(joined, /origin/, "YouTube embed origin is missing");
const playerScript = await readFile(join(root, "src", "Rot.App", "Web", "player", "player.js"), "utf8");
assert.match(playerScript, /nextVideo\s*\(/, "the next hotkey must advance the active YouTube playlist");
assert.match(joined, /RegisterHotKey/, "global hotkeys must use RegisterHotKey");
assert.match(joined, /WsExNoActivate|WS_EX_NOACTIVATE/i, "permanent no-activate style is missing");
assert.match(joined, /WsExTransparent|WS_EX_TRANSPARENT/i, "pass-through style is missing");

for (const [path, text] of texts) {
  if (extname(path).toLowerCase() !== ".js") continue;
  for (const match of text.matchAll(/setInterval\([\s\S]*?,\s*(\d+)\s*\)/g)) {
    assert.ok(Number(match[1]) >= 1000, `interval below 1000ms in ${relative(root, path)}`);
  }
}

const nativeImports = [...joined.matchAll(/DllImport\(["']([^"']+)["']/gi)].map((match) => match[1].toLowerCase());
for (const library of nativeImports) {
  // user32 provides ordinary window/hotkey behavior; shcore is read-only monitor DPI discovery.
  assert.ok(["user32.dll", "shcore.dll"].includes(library), `unexpected native import: ${library}`);
}

for (const name of ["icon-color.png", "icon-gray.png", "window-icon.png", "splash.png"]) {
  const path = join(root, "assets", name);
  const buffer = await readFile(path);
  assert.deepEqual(pngDimensions(buffer), { width: 256, height: 256 }, `${name} must be 256x256`);
  if (["icon-color.png", "icon-gray.png"].includes(name)) {
    assert.ok((await stat(path)).size < 30_000, `${name} must remain below 30KB`);
  }
}
assert.equal(await exists(join(root, "assets", "launcher.ico")), true, "launcher.ico is missing");

const installerNames = (await filesUnder(root)).filter((path) => {
  const name = path.toLowerCase();
  return name.endsWith(".msi") || name.endsWith(".msix") || name.endsWith("\\setup.exe");
});
assert.deepEqual(installerNames, [], "Rot must ship as a portable folder without an installer");

console.log(`[rot-check] ${sourceFiles.length} source/test files checked; standalone architecture, safety boundary, assets, and required integration points passed.`);
