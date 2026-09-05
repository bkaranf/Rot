import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const repositoryRoot = fileURLToPath(new URL("../", import.meta.url));
const [settingsHtml, settingsCss, settingsScript, themeCss] = await Promise.all([
  readFile(`${repositoryRoot}src/Rot.App/Web/settings/index.html`, "utf8"),
  readFile(`${repositoryRoot}src/Rot.App/Web/settings/settings.css`, "utf8"),
  readFile(`${repositoryRoot}src/Rot.App/Web/settings/settings.js`, "utf8"),
  readFile(`${repositoryRoot}src/Rot.App/Web/common/theme.css`, "utf8"),
]);

class FakeClassList {
  #values = new Set();

  add(...names) {
    for (const name of names) this.#values.add(name);
  }

  remove(...names) {
    for (const name of names) this.#values.delete(name);
  }

  toggle(name, force) {
    const enabled = force === undefined ? !this.#values.has(name) : Boolean(force);
    if (enabled) this.#values.add(name);
    else this.#values.delete(name);
    return enabled;
  }

  contains(name) {
    return this.#values.has(name);
  }
}

class FakeElement extends EventTarget {
  constructor(tagName = "div") {
    super();
    this.tagName = tagName.toUpperCase();
    this.classList = new FakeClassList();
    this.children = [];
    this.parentNode = null;
    this.ownerDocument = null;
    this.attributes = new Map();
    this.dataset = {};
    this.hidden = false;
    this.open = false;
    this.checked = false;
    this.disabled = false;
    this.value = "";
    this.textContent = "";
    this.focus = () => {
      this.ownerDocument?.setActiveElement(this);
    };
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  getAttribute(name) {
    return this.attributes.get(name) || null;
  }

  append(...children) {
    for (const child of children) {
      if (!child || typeof child !== "object") continue;
      child.parentNode?.removeChild(child);
      child.parentNode = this;
      this.children.push(child);
    }
  }

  prepend(...children) {
    for (const child of children.reverse()) {
      if (!child || typeof child !== "object") continue;
      child.parentNode?.removeChild(child);
      child.parentNode = this;
      this.children.unshift(child);
    }
  }

  insertBefore(child, next) {
    child.parentNode?.removeChild(child);
    const index = next ? this.children.indexOf(next) : this.children.length;
    child.parentNode = this;
    this.children.splice(index, 0, child);
  }

  replaceChildren(...children) {
    for (const child of this.children) child.parentNode = null;
    this.children = [];
    this.append(...children);
  }

  removeChild(child) {
    const index = this.children.indexOf(child);
    if (index < 0) return child;
    const containsActive = (element) => element === this.ownerDocument?.activeElement
      || element.children.some(containsActive);
    if (containsActive(child)) this.ownerDocument?.setActiveElement(null);
    this.children.splice(index, 1);
    child.parentNode = null;
    return child;
  }

  closest() {
    return null;
  }
}

class FakeDocument extends EventTarget {
  constructor() {
    super();
    this.byId = new Map();
    this.sizeButtons = [];
    this.opacityButtons = [];
    this.activeElement = null;
  }

  add(id, tagName = "div") {
    const element = new FakeElement(tagName);
    element.ownerDocument = this;
    this.byId.set(id, element);
    return element;
  }

  querySelector(selector) {
    if (selector.startsWith("#")) return this.byId.get(selector.slice(1)) || null;
    if (selector === "[data-size]") return this.sizeButtons[0] || null;
    if (selector === "[data-opacity]") return this.opacityButtons[0] || null;
    return null;
  }

  querySelectorAll(selector) {
    if (selector === "[data-size]") return this.sizeButtons;
    if (selector === "[data-opacity]") return this.opacityButtons;
    return [];
  }

  createElement(tagName) {
    const element = new FakeElement(tagName);
    element.ownerDocument = this;
    return element;
  }

  setActiveElement(element) {
    this.activeElement = element;
  }
}

function makeSettingsDocument() {
  const document = new FakeDocument();
  for (const id of [
    "drag-header", "close-button", "detection-message", "detection-status", "detection-dot",
    "detection-label", "detection-action-message", "repair-stats-button", "restart-warning",
    "web-recovery-notice", "web-recovery-message", "web-recovery-button",
    "borderless-warning", "auto-restore-toggle", "volume-slider", "volume-label", "muted-toggle",
    "reset-layout-button", "player-message", "appearance-message", "pass-through-group", "pass-through-toggle",
    "pass-through-description", "pass-through-recovery", "pass-through-hotkey", "pass-through-message",
    "hotkey-failure-notice", "hotkey-failure-heading", "hotkey-failure-list", "hotkey-details", "hotkey-list",
    "hotkey-editor", "hotkey-capture-hint", "hotkey-apply-button", "hotkey-cancel-button",
    "hotkey-defaults-button", "hotkey-edit-message",
    "about-version", "about-revision", "check-updates-button", "install-update-button",
    "update-message", "project-repository-button", "project-releases-button", "project-help-button",
    "help-details", "player-capabilities-details", "player-capabilities-status", "player-capabilities-reason",
  ]) document.add(id);
  for (const [id, value] of [["compact", "compact"], ["medium", "medium"], ["large", "large"]]) {
    const button = document.add(`size-${id}`, "button");
    button.dataset.size = value;
    document.sizeButtons.push(button);
  }
  for (const value of ["1", "0.85", "0.7", "0.55"]) {
    const button = document.add(`opacity-${value}`, "button");
    button.dataset.opacity = value;
    document.opacityButtons.push(button);
  }
  return document;
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

class FakeWebview extends EventTarget {
  constructor(state) {
    super();
    this.state = state;
    this.requests = [];
  }

  postMessage(message) {
    if (!message.requestId) return;
    this.requests.push(message);
    if (message.type === "state.get") {
      queueMicrotask(() => this.respond(message, { state: clone(this.state) }));
    } else if (message.type === "hotkeys.capture") {
      queueMicrotask(() => this.respond(message, { state: clone(this.state) }));
    }
  }

  respond(request, payload = {}, { ok = true, error = "" } = {}) {
    this.dispatchEvent(new MessageEvent("message", {
      data: { type: "response", requestId: request.requestId, ok, error, payload },
    }));
  }
}

async function flush() {
  await new Promise((resolve) => setImmediate(resolve));
  await new Promise((resolve) => setImmediate(resolve));
}

function keyEvent(key, options = {}) {
  const event = new Event("keydown");
  for (const [name, value] of Object.entries({
    key,
    code: options.code || key,
    altKey: false,
    ctrlKey: false,
    shiftKey: false,
    metaKey: false,
    repeat: false,
    ...options,
  })) {
    Object.defineProperty(event, name, { configurable: true, value });
  }
  return event;
}

function makeShortcutState() {
  return {
    schemaVersion: 2,
    settings: {
      volume: 75,
      muted: false,
      opacity: 1,
      sizePreset: "medium",
      passThrough: false,
      autoRestoreAfterMatch: true,
    },
    runtime: {
      version: "2.1.0",
      revision: "fixture",
      detectionState: "disconnected",
      detectionAvailable: false,
      detectionMessage: "Waiting for Stats API.",
      restartRequired: false,
      borderlessWarning: false,
      playerCapabilities: { ready: false, appControls: false, reason: "Player is starting." },
      hotkeyFailures: [],
      hotkeys: {
        togglePlayer: "Ctrl+Shift+Y",
        toggleBrowse: "Ctrl+Shift+F",
        playPause: "Ctrl+Shift+K",
        mute: "Ctrl+Shift+M",
        next: "Ctrl+Shift+N",
        opacity: "Ctrl+Shift+O",
        interactivity: "Ctrl+Shift+P",
      },
      hotkeyBindings: {
        "toggle-overlay": { modifiers: 6, virtualKey: 89 },
        "toggle-browse": { modifiers: 6, virtualKey: 70 },
        "toggle-playback": { modifiers: 6, virtualKey: 75 },
        "toggle-mute": { modifiers: 6, virtualKey: 77 },
        next: { modifiers: 6, virtualKey: 78 },
        "cycle-opacity": { modifiers: 6, virtualKey: 79 },
        "toggle-interactivity": { modifiers: 6, virtualKey: 80 },
      },
      hotkeyDefaults: {
        "toggle-overlay": { modifiers: 6, virtualKey: 89 },
        "toggle-browse": { modifiers: 6, virtualKey: 70 },
        "toggle-playback": { modifiers: 6, virtualKey: 75 },
        "toggle-mute": { modifiers: 6, virtualKey: 77 },
        next: { modifiers: 6, virtualKey: 78 },
        "cycle-opacity": { modifiers: 6, virtualKey: 79 },
        "toggle-interactivity": { modifiers: 6, virtualKey: 80 },
      },
      update: {
        currentVersion: "2.1.0",
        latestVersion: "2.1.0",
        isUpdateAvailable: false,
        message: "",
        busy: false,
        notice: "",
      },
    },
  };
}

test("Settings follows the playback-first four-section order without tabs", () => {
  const headings = [...settingsHtml.matchAll(/class="section-label">([^<]+)</g)]
    .map((match) => match[1]);
  assert.deepEqual(headings, ["Playback", "Appearance", "Mouse controls", "Help"]);
  assert.doesNotMatch(settingsHtml, /role="tab"|class="tab/);
  assert.match(settingsHtml, /settings\.css/);
  assert.match(settingsHtml, /settings\.js/);
  assert.match(settingsHtml, /src="\.\.\/assets\/icon-color\.png"/);
  assert.doesNotMatch(settingsHtml, /Auto-save on|Changes save automatically/);
  assert.doesNotMatch(settingsHtml, /settings-message|settings-header-status|save-status/);
  assert.match(settingsHtml, /settings-brand[\s\S]*<h1>Settings<\/h1>[\s\S]*id="close-button"/);
  for (const id of ["player-message", "appearance-message", "pass-through-message", "detection-action-message"]) {
    assert.match(settingsHtml, new RegExp(`id="${id}"[^>]*aria-live="polite"`));
  }
  assert.doesNotMatch(settingsHtml, /<details id="help-details"[^>]*\bopen\b/);
});

test("Settings states the embedded Browse limitations exactly once", () => {
  assert.equal((settingsHtml.match(/Browse is signed out\./g) || []).length, 1);
  assert.equal((settingsHtml.match(/YouTube Premium does not apply/g) || []).length, 1);
  assert.match(settingsHtml, /general recommendations/);
  assert.match(settingsHtml, /subscriptions, history, or Watch Later/);
  assert.match(settingsHtml, /ads will play/);
  assert.match(settingsHtml, /Player opacity/);
  assert.match(settingsHtml, /aria-label="Player opacity"/);
  assert.match(settingsHtml, /Lower values let more of the game show through/);
  assert.match(settingsHtml, /Keep videos quiet/);
  assert.match(settingsCss, /container:\s*settings-header\s*\/\s*inline-size/);
  assert.match(settingsCss, /@container settings-header[\s\S]*?\.settings-logo\s*\{[\s\S]*?display:\s*none/);
});

test("Settings explains the Rocket League foreground gate", () => {
  assert.match(settingsHtml, /resumes only after Rocket League verifies Training again/);
  assert.match(settingsHtml, /stays hidden and paused while the game is closed, in the background/);
});

test("Settings exposes selected presets and native recovery status accessibly", () => {
  assert.equal((settingsHtml.match(/aria-pressed="false"/g) || []).length, 7);
  assert.match(settingsHtml, /id="hotkey-failure-notice"/);
  assert.match(settingsHtml, /id="hotkey-details" class="settings-details">/);
  assert.match(settingsHtml, /id="player-capabilities-details" class="settings-details settings-details--nested">/);
  assert.match(settingsScript, /setAttribute\("aria-pressed", String\(active\)\)/);
  assert.match(settingsScript, /hotkeyFailures/);
  assert.match(settingsScript, /playerCapabilities/);
  assert.match(settingsScript, /recovery shortcut is unavailable/);
  assert.doesNotMatch(settingsScript, /settingsMessage|setSaveStatus/);
  assert.match(settingsScript, /setMessage\(messageElement, "Saving…"\)/);
  assert.match(settingsScript, /appearanceMessage/);
  assert.match(settingsScript, /detectionActionMessage, "Checking configuration…"/);
});

test("Settings exposes editable keyboard shortcuts without adding header chrome", () => {
  assert.match(settingsHtml, /id="hotkey-editor"/);
  assert.match(settingsHtml, /id="hotkey-apply-button"/);
  assert.match(settingsHtml, /id="hotkey-cancel-button"/);
  assert.match(settingsHtml, /id="hotkey-defaults-button"/);
  assert.match(settingsHtml, /id="hotkey-edit-message"[^>]*aria-live="polite"/);
  assert.match(settingsScript, /bridge\.request\("hotkeys\.set", \{ bindings: requestBindings \}\)/);
  assert.match(settingsScript, /hotkeyDefaults/);
  assert.match(settingsScript, /That shortcut is already assigned/);
  assert.match(settingsScript, /reserved by Windows/);
  assert.match(settingsScript, /Escape cancels/);
  assert.match(settingsScript, /F\$\{virtualKey - 111\}/);
  assert.match(settingsScript, /Space/);
  assert.match(settingsScript, /hotkeys\.capture/);
  assert.match(settingsScript, /hotkeys\.captured/);
  assert.match(settingsHtml, /id="about-version"/);
  assert.match(settingsHtml, /id="about-revision"/);
  assert.match(settingsHtml, /id="check-updates-button"/);
  assert.match(settingsHtml, /id="install-update-button"/);
  assert.match(settingsScript, /bridge\.request\("updates\.check", \{\}, \{ timeoutMs: 35000 \}\)/);
  assert.match(settingsScript, /bridge\.request\("updates\.install", \{\}, \{ timeoutMs: 360000 \}\)/);
  assert.match(settingsScript, /project\.open/);
});

test("Settings resynchronizes controls when the desktop host rejects a patch", () => {
  assert.match(settingsScript, /const generation = \+\+settingsPatchGeneration/);
  assert.match(settingsScript, /const feedbackGeneration = \+\+feedback\.generation/);
  assert.match(settingsScript, /const isCurrentFeedback = \(\) => feedbackGeneration === feedback\.generation/);
  assert.match(settingsScript, /if \(isCurrentState\(\) && result\?\.state\) applyState/);
  assert.match(settingsScript, /if \(!isCurrentFeedback\(\)\) return null/);
  assert.match(settingsScript, /const state = await getInitialState\(bridge\)/);
  assert.match(settingsScript, /settings state resync failed after a rejected patch/);
  assert.doesNotMatch(settingsCss, /min-width:\s*420px/);
  assert.match(settingsCss, /width: min\(100%, 640px\)/);
  assert.match(settingsCss, /\.settings-row small,[\s\S]*?font-size: 13px/);
  assert.match(themeCss, /\.message:empty\s*\{[\s\S]*?display: none/);
  assert.match(settingsScript, /Choose a video in Browse to enable playback controls\./);
});

test("Settings renders native failures and preserves a newer successful update", async () => {
  const document = makeSettingsDocument();
  const state = {
    schemaVersion: 2,
    settings: {
      volume: 75,
      muted: false,
      opacity: 1,
      sizePreset: "medium",
      passThrough: false,
      autoRestoreAfterMatch: true,
    },
    runtime: {
      detectionState: "disconnected",
      detectionAvailable: false,
      detectionMessage: "Waiting for Stats API.",
      restartRequired: false,
      borderlessWarning: false,
      playerCapabilities: { ready: false, appControls: false, reason: "Player is starting." },
      hotkeyFailures: [{
        action: "toggle-interactivity",
        chord: "Ctrl+Shift+P",
        message: "The shortcut is already registered.",
      }],
      hotkeys: {
        togglePlayer: "Ctrl+Shift+Y",
        toggleBrowse: "Ctrl+Shift+F",
        playPause: "Ctrl+Shift+K",
        mute: "Ctrl+Shift+M",
        next: "Ctrl+Shift+N",
        opacity: "Ctrl+Shift+O",
        interactivity: "Unavailable",
      },
    },
  };
  const webview = new FakeWebview(clone(state));
  const fakeWindow = new EventTarget();
  const priorGlobals = new Map([
    ["document", globalThis.document],
    ["window", globalThis.window],
    ["chrome", globalThis.chrome],
    ["location", globalThis.location],
  ]);
  Object.assign(globalThis, {
    document,
    window: fakeWindow,
    chrome: { webview },
    location: { href: "https://rot.local/settings/" },
  });

  try {
    await import(`${pathToFileURL(`${repositoryRoot}src/Rot.App/Web/settings/settings.js`).href}?behavior=${Date.now()}`);
    await flush();

    const medium = document.byId.get("size-medium");
    const failureNotice = document.byId.get("hotkey-failure-notice");
    const failureItem = document.byId.get("hotkey-failure-list").children[0];
    assert.equal(medium.getAttribute("aria-pressed"), "true");
    assert.equal(failureNotice.hidden, false);
    assert.equal(document.byId.get("hotkey-details").open, true);
    assert.equal(document.byId.get("help-details").open, true);
    assert.match(failureItem.textContent, /Toggle player pass-through.*Ctrl\+Shift\+P/);
    assert.equal(document.byId.get("pass-through-hotkey").textContent, "Unavailable: use Settings");
    assert.equal(document.byId.get("pass-through-recovery").hidden, false);
    assert.equal(document.byId.get("player-capabilities-reason").textContent, "Player is starting.");
    assert.equal(document.byId.get("player-capabilities-details").open, false);

    document.byId.get("hotkey-details").open = false;
    webview.dispatchEvent(new MessageEvent("message", {
      data: { type: "state.changed", payload: { state: clone(state) } },
    }));
    await flush();
    assert.equal(document.byId.get("hotkey-details").open, false);

    const volume = document.byId.get("volume-slider");
    volume.value = "42";
    volume.dispatchEvent(new Event("change"));
    await flush();
    const rejected = webview.requests.filter(({ type }) => type === "settings.patch").at(-1);
    webview.respond(rejected, {}, { ok: false, error: "The settings file could not be written." });
    await flush();
    assert.equal(volume.value, "75");
    const playerMessage = document.byId.get("player-message");
    assert.equal(playerMessage.textContent, "The settings file could not be written.");
    assert.equal(playerMessage.classList.contains("is-error"), true);
    assert.equal(document.querySelector("#settings-message"), null);

    volume.value = "33";
    volume.dispatchEvent(new Event("change"));
    volume.value = "44";
    volume.dispatchEvent(new Event("change"));
    await flush();
    const patches = webview.requests.filter(({ type }) => type === "settings.patch").slice(-2);
    assert.equal(patches.length, 2);
    state.settings.volume = 44;
    webview.state = clone(state);
    webview.respond(patches[1], { state: clone(state) });
    webview.respond(patches[0], {}, { ok: false, error: "The older settings write failed." });
    await flush();
    assert.equal(volume.value, "44");

    volume.value = "55";
    volume.dispatchEvent(new Event("change"));
    volume.value = "66";
    volume.dispatchEvent(new Event("change"));
    await flush();
    const reversed = webview.requests.filter(({ type }) => type === "settings.patch").slice(-2);
    assert.equal(reversed.length, 2);
    const staleState = clone(state);
    staleState.settings.volume = 55;
    state.settings.volume = 66;
    webview.state = clone(state);
    webview.respond(reversed[0], { state: staleState });
    webview.respond(reversed[1], { state: clone(state) });
    await flush();
    assert.equal(volume.value, "66");
    assert.equal(playerMessage.textContent, "Saved.");
    assert.equal(playerMessage.classList.contains("is-error"), false);
    assert.equal(document.querySelector("#settings-message"), null);

    const large = document.byId.get("size-large");
    large.dispatchEvent(new Event("click"));
    await flush();
    const appearancePatch = webview.requests.filter(({ type }) => type === "settings.patch").at(-1);
    assert.equal(document.byId.get("appearance-message").textContent, "Saving…");
    state.settings.sizePreset = "large";
    webview.state = clone(state);
    webview.respond(appearancePatch, { state: clone(state) });
    await flush();
    assert.equal(document.byId.get("appearance-message").textContent, "Saved.");
    assert.equal(document.querySelector("#settings-message"), null);

    const crossSectionStart = webview.requests.length;
    volume.value = "27";
    volume.dispatchEvent(new Event("change"));
    document.byId.get("size-compact").dispatchEvent(new Event("click"));
    await flush();
    const crossSectionPatches = webview.requests
      .slice(crossSectionStart)
      .filter(({ type }) => type === "settings.patch");
    assert.equal(crossSectionPatches.length, 2);
    assert.equal(document.byId.get("player-message").textContent, "Saving…");
    assert.equal(document.byId.get("appearance-message").textContent, "Saving…");
    state.settings.volume = 27;
    state.settings.sizePreset = "compact";
    webview.state = clone(state);
    webview.respond(crossSectionPatches[1], { state: clone(state) });
    webview.respond(crossSectionPatches[0], { state: clone(state) });
    await flush();
    assert.equal(volume.value, "27");
    assert.equal(document.byId.get("size-compact").getAttribute("aria-pressed"), "true");
    assert.equal(document.byId.get("player-message").textContent, "Saved.");
    assert.equal(document.byId.get("appearance-message").textContent, "Saved.");

    const crossSectionFailureStart = webview.requests.length;
    volume.value = "31";
    volume.dispatchEvent(new Event("change"));
    document.byId.get("size-large").dispatchEvent(new Event("click"));
    await flush();
    const crossSectionFailurePatches = webview.requests
      .slice(crossSectionFailureStart)
      .filter(({ type }) => type === "settings.patch");
    assert.equal(crossSectionFailurePatches.length, 2);
    state.settings.sizePreset = "large";
    webview.state = clone(state);
    webview.respond(crossSectionFailurePatches[0], {}, { ok: false, error: "Playback settings failed." });
    await flush();
    assert.equal(document.byId.get("player-message").textContent, "Playback settings failed.");
    assert.equal(document.byId.get("appearance-message").textContent, "Saving…");
    webview.respond(crossSectionFailurePatches[1], { state: clone(state) });
    await flush();
    assert.equal(volume.value, "27");
    assert.equal(document.byId.get("size-large").getAttribute("aria-pressed"), "true");
    assert.equal(document.byId.get("player-message").textContent, "Playback settings failed.");
    assert.equal(document.byId.get("appearance-message").textContent, "Saved.");
    assert.doesNotMatch(document.byId.get("player-message").textContent, /Saving/);
    assert.doesNotMatch(document.byId.get("appearance-message").textContent, /Saving/);

    const sameSectionStart = webview.requests.length;
    volume.value = "41";
    volume.dispatchEvent(new Event("change"));
    volume.value = "52";
    volume.dispatchEvent(new Event("change"));
    await flush();
    const sameSectionPatches = webview.requests
      .slice(sameSectionStart)
      .filter(({ type }) => type === "settings.patch");
    assert.equal(sameSectionPatches.length, 2);
    state.settings.volume = 52;
    webview.state = clone(state);
    webview.respond(sameSectionPatches[1], { state: clone(state) });
    const staleSameSectionState = clone(state);
    staleSameSectionState.settings.volume = 41;
    webview.respond(sameSectionPatches[0], { state: staleSameSectionState });
    await flush();
    assert.equal(volume.value, "52");
    assert.equal(document.byId.get("player-message").textContent, "Saved.");
    assert.doesNotMatch(document.byId.get("player-message").textContent, /Saving/);

    const passThroughToggle = document.byId.get("pass-through-toggle");
    passThroughToggle.checked = true;
    passThroughToggle.dispatchEvent(new Event("change"));
    await flush();
    const passThroughPatch = webview.requests.filter(({ type }) => type === "settings.patch").at(-1);
    state.settings.passThrough = true;
    webview.state = clone(state);
    webview.respond(passThroughPatch, { state: clone(state) });
    await flush();
    assert.match(document.byId.get("pass-through-message").textContent, /^Pass-through enabled\./);
    assert.doesNotMatch(document.byId.get("pass-through-message").textContent, /Saving/);

    const repair = document.byId.get("repair-stats-button");
    repair.dispatchEvent(new Event("click"));
    await flush();
    assert.equal(document.byId.get("detection-action-message").textContent, "Checking configuration…");
    const repairRequest = webview.requests.filter(({ type }) => type === "stats.repair").at(-1);
    webview.respond(repairRequest, { message: "Configuration checked." });
    await flush();
    assert.equal(document.byId.get("detection-action-message").textContent, "Configuration checked.");
    repair.dispatchEvent(new Event("click"));
    await flush();
    const failedRepair = webview.requests.filter(({ type }) => type === "stats.repair").at(-1);
    webview.respond(failedRepair, {}, { ok: false, error: "Stats settings unavailable." });
    await flush();
    const detectionActionMessage = document.byId.get("detection-action-message");
    assert.equal(detectionActionMessage.textContent, "Stats settings unavailable.");
    assert.equal(detectionActionMessage.classList.contains("is-error"), true);
    assert.equal(document.querySelector("#settings-message"), null);

    const healthy = clone(state);
    healthy.settings.passThrough = false;
    healthy.runtime.hotkeyFailures = [];
    webview.state = clone(healthy);
    document.byId.get("help-details").open = false;
    webview.dispatchEvent(new MessageEvent("message", {
      data: { type: "state.changed", payload: { state: clone(healthy) } },
    }));
    await flush();
    assert.equal(document.byId.get("pass-through-recovery").hidden, true);
    assert.equal(document.byId.get("hotkey-failure-notice").hidden, true);

    healthy.settings.passThrough = true;
    webview.state = clone(healthy);
    webview.dispatchEvent(new MessageEvent("message", {
      data: { type: "state.changed", payload: { state: clone(healthy) } },
    }));
    await flush();
    assert.equal(document.byId.get("pass-through-recovery").hidden, false);

    const warning = clone(healthy);
    warning.runtime.hotkeyFailures = [{
      action: "toggle-interactivity",
      chord: "Ctrl+Shift+P",
      message: "The shortcut is already registered.",
    }];
    webview.state = clone(warning);
    document.byId.get("help-details").open = false;
    webview.dispatchEvent(new MessageEvent("message", {
      data: { type: "state.changed", payload: { state: clone(warning) } },
    }));
    await flush();
    assert.equal(document.byId.get("help-details").open, true);
    assert.equal(document.byId.get("pass-through-recovery").hidden, false);
    assert.equal(document.byId.get("hotkey-failure-heading").textContent, "Pass-through recovery unavailable");
  } finally {
    for (const [key, value] of priorGlobals) {
      if (value === undefined) delete globalThis[key];
      else globalThis[key] = value;
    }
  }
});

test("Settings captures, validates, saves, reverts, and restores keyboard shortcuts", async () => {
  const document = makeSettingsDocument();
  const state = makeShortcutState();
  const webview = new FakeWebview(clone(state));
  const fakeWindow = new EventTarget();
  const priorGlobals = new Map([
    ["document", globalThis.document],
    ["window", globalThis.window],
    ["chrome", globalThis.chrome],
    ["location", globalThis.location],
  ]);
  Object.assign(globalThis, {
    document,
    window: fakeWindow,
    chrome: { webview },
    location: { href: "https://rot.local/settings/" },
  });

  try {
    await import(`${pathToFileURL(`${repositoryRoot}src/Rot.App/Web/settings/settings.js`).href}?shortcuts=${Date.now()}-${Math.random()}`);
    await flush();

    const list = document.byId.get("hotkey-list");
    assert.equal(list.children.length, 7);
    const firstButton = () => list.children[0].children[1];
    const secondButton = () => list.children[1].children[1];
    assert.equal(firstButton().textContent, "Ctrl+Shift+Y");

    firstButton().dispatchEvent(new Event("click"));
    assert.equal(document.byId.get("hotkey-editor").hidden, false);
    assert.match(document.byId.get("hotkey-capture-hint").textContent, /Press a new shortcut/);
    document.dispatchEvent(keyEvent("Escape"));
    assert.equal(document.byId.get("hotkey-editor").hidden, true);
    assert.equal(document.byId.get("hotkey-edit-message").textContent, "Shortcut change canceled.");
    assert.equal(firstButton().textContent, "Ctrl+Shift+Y");

    firstButton().dispatchEvent(new Event("click"));
    document.dispatchEvent(keyEvent("F", { code: "KeyF", ctrlKey: true, shiftKey: true }));
    assert.equal(document.byId.get("hotkey-edit-message").textContent, "That shortcut is already assigned to Open or close Browse.");
    assert.equal(document.byId.get("hotkey-edit-message").classList.contains("is-error"), true);
    assert.equal(firstButton().textContent, "Ctrl+Shift+Y");
    document.dispatchEvent(keyEvent("A", { code: "KeyA", shiftKey: true }));
    assert.equal(document.byId.get("hotkey-edit-message").textContent, "Use Ctrl, Alt, or Win with that key.");
    document.dispatchEvent(keyEvent("F8", { code: "F8", ctrlKey: true }));
    assert.equal(firstButton().textContent, "Ctrl+F8");
    assert.equal(document.byId.get("hotkey-apply-button").disabled, false);

    document.byId.get("hotkey-apply-button").dispatchEvent(new Event("click"));
    await flush();
    const saveRequest = webview.requests.filter(({ type }) => type === "hotkeys.set").at(-1);
    assert.equal(Object.keys(saveRequest.payload.bindings).length, 7);
    assert.deepEqual(saveRequest.payload.bindings["toggle-overlay"], { modifiers: 2, virtualKey: 119 });
    state.runtime.hotkeyBindings["toggle-overlay"] = { modifiers: 2, virtualKey: 119 };
    state.runtime.hotkeys.togglePlayer = "Ctrl+F8";
    webview.state = clone(state);
    webview.respond(saveRequest, { state: clone(state) });
    await flush();
    assert.equal(document.byId.get("hotkey-edit-message").textContent, "Shortcuts saved.");
    assert.equal(document.byId.get("hotkey-editor").hidden, true);
    assert.equal(firstButton().textContent, "Ctrl+F8");

    secondButton().dispatchEvent(new Event("click"));
    document.dispatchEvent(keyEvent("ArrowUp", { code: "ArrowUp", altKey: true }));
    document.byId.get("hotkey-apply-button").dispatchEvent(new Event("click"));
    await flush();
    const rejectedRequest = webview.requests.filter(({ type }) => type === "hotkeys.set").at(-1);
    assert.deepEqual(rejectedRequest.payload.bindings["toggle-browse"], { modifiers: 1, virtualKey: 38 });
    webview.respond(rejectedRequest, {}, { ok: false, error: "Shortcut is already registered." });
    await flush();
    assert.equal(secondButton().textContent, "Ctrl+Shift+F");
    assert.equal(document.byId.get("hotkey-edit-message").textContent, "Shortcut is already registered.");
    assert.equal(document.byId.get("hotkey-edit-message").classList.contains("is-error"), true);
    assert.equal(document.byId.get("hotkey-editor").hidden, true);

    document.byId.get("hotkey-defaults-button").dispatchEvent(new Event("click"));
    await flush();
    const defaultsRequest = webview.requests.filter(({ type }) => type === "hotkeys.set").at(-1);
    assert.equal(Object.keys(defaultsRequest.payload.bindings).length, 7);
    assert.deepEqual(defaultsRequest.payload.bindings["toggle-overlay"], { modifiers: 6, virtualKey: 89 });
    assert.deepEqual(defaultsRequest.payload.bindings["toggle-interactivity"], { modifiers: 6, virtualKey: 80 });
    const defaultsState = makeShortcutState();
    webview.state = clone(defaultsState);
    webview.respond(defaultsRequest, { state: clone(defaultsState) });
    await flush();
    assert.equal(firstButton().textContent, "Ctrl+Shift+Y");
    assert.equal(document.byId.get("hotkey-edit-message").textContent, "Shortcuts saved.");
    assert.doesNotMatch(document.byId.get("hotkey-edit-message").textContent, /Saving/);
  } finally {
    for (const [key, value] of priorGlobals) {
      if (value === undefined) delete globalThis[key];
      else globalThis[key] = value;
    }
  }
});

test("Settings keeps shortcut focus stable and accepts native capture events", async () => {
  const document = makeSettingsDocument();
  const state = makeShortcutState();
  const webview = new FakeWebview(clone(state));
  const fakeWindow = new EventTarget();
  const priorGlobals = new Map([
    ["document", globalThis.document],
    ["window", globalThis.window],
    ["chrome", globalThis.chrome],
    ["location", globalThis.location],
  ]);
  Object.assign(globalThis, {
    document,
    window: fakeWindow,
    chrome: { webview },
    location: { href: "https://rot.local/settings/" },
  });

  try {
    await import(`${pathToFileURL(`${repositoryRoot}src/Rot.App/Web/settings/settings.js`).href}?native-capture=${Date.now()}-${Math.random()}`);
    await flush();
    const list = document.byId.get("hotkey-list");
    const firstButton = list.children[0].children[1];
    firstButton.dispatchEvent(new Event("click"));
    await flush();
    assert.equal(document.activeElement, firstButton);
    const captureStart = webview.requests.filter(({ type }) => type === "hotkeys.capture").at(-1);
    assert.deepEqual(captureStart.payload, { active: true });

    webview.dispatchEvent(new MessageEvent("message", {
      data: { type: "state.changed", payload: { state: clone(state) } },
    }));
    await flush();
    assert.equal(list.children[0].children[1], firstButton);
    assert.equal(document.activeElement, firstButton);

    webview.dispatchEvent(new MessageEvent("message", {
      data: { type: "hotkeys.captured", payload: { modifiers: 2, virtualKey: 119 } },
    }));
    await flush();
    assert.equal(firstButton.textContent, "Ctrl+F8");
    const captureStop = webview.requests.filter(({ type }) => type === "hotkeys.capture").at(-1);
    assert.deepEqual(captureStop.payload, { active: false });
    assert.equal(document.byId.get("hotkey-apply-button").disabled, false);

    document.byId.get("hotkey-cancel-button").dispatchEvent(new Event("click"));
    assert.equal(firstButton.textContent, "Ctrl+Shift+Y");
    assert.equal(document.byId.get("hotkey-editor").hidden, true);
  } finally {
    for (const [key, value] of priorGlobals) {
      if (value === undefined) delete globalThis[key];
      else globalThis[key] = value;
    }
  }
});

test("Settings checks updates explicitly and guards install actions", async () => {
  const document = makeSettingsDocument();
  const state = makeShortcutState();
  const webview = new FakeWebview(clone(state));
  const fakeWindow = new EventTarget();
  const priorGlobals = new Map([
    ["document", globalThis.document],
    ["window", globalThis.window],
    ["chrome", globalThis.chrome],
    ["location", globalThis.location],
  ]);
  Object.assign(globalThis, {
    document,
    window: fakeWindow,
    chrome: { webview },
    location: { href: "https://rot.local/settings/" },
  });

  try {
    await import(`${pathToFileURL(`${repositoryRoot}src/Rot.App/Web/settings/settings.js`).href}?updates=${Date.now()}-${Math.random()}`);
    await flush();
    const check = document.byId.get("check-updates-button");
    const install = document.byId.get("install-update-button");
    const message = document.byId.get("update-message");
    assert.equal(document.byId.get("about-version").textContent, "Version 2.1.0");
    assert.equal(document.byId.get("about-revision").textContent, "Revision fixture");
    assert.equal(install.disabled, true);

    install.dispatchEvent(new Event("click"));
    assert.equal(webview.requests.some(({ type }) => type === "updates.install"), false);

    check.dispatchEvent(new Event("click"));
    check.dispatchEvent(new Event("click"));
    await flush();
    const checks = webview.requests.filter(({ type }) => type === "updates.check");
    assert.equal(checks.length, 1);
    assert.equal(check.disabled, true);
    assert.equal(install.disabled, true);

    const available = clone(state);
    available.runtime.update = {
      currentVersion: "2.1.0",
      latestVersion: "2.2.0",
      isUpdateAvailable: true,
      message: "Version 2.2.0 is ready to install.",
      busy: false,
      notice: "",
    };
    webview.state = clone(available);
    webview.respond(checks[0], {
      state: clone(available),
      update: clone(available.runtime.update),
    });
    await flush();
    assert.equal(install.disabled, false);
    assert.equal(message.textContent, "Version 2.2.0 is ready to install.");

    install.dispatchEvent(new Event("click"));
    install.dispatchEvent(new Event("click"));
    await flush();
    const installs = webview.requests.filter(({ type }) => type === "updates.install");
    assert.equal(installs.length, 1);
    assert.equal(install.disabled, true);
    assert.equal(message.textContent, "Downloading and preparing the update...");
    webview.respond(installs[0], {
      state: clone(available),
      update: { ...available.runtime.update, message: "Restarting Rot..." },
    });
    await flush();
    assert.equal(message.textContent, "Restarting Rot...");
    assert.equal(message.classList.contains("is-error"), false);

    check.dispatchEvent(new Event("click"));
    await flush();
    const failedCheck = webview.requests.filter(({ type }) => type === "updates.check").at(-1);
    webview.respond(failedCheck, {}, { ok: false, error: "Update service unavailable." });
    await flush();
    assert.equal(message.textContent, "Update service unavailable.");
    assert.equal(message.classList.contains("is-error"), true);
    const failedRecovery = clone(state);
    failedRecovery.runtime.recoveryMessage = "The player could not restart. Choose Retry player.";
    failedRecovery.runtime.recoveryCanRetry = true;
    webview.dispatchEvent(new MessageEvent("message", {
      data: { type: "state.changed", payload: { state: failedRecovery } },
    }));
    const retry = document.byId.get("web-recovery-button");
    assert.equal(document.byId.get("web-recovery-notice").hidden, false);
    assert.equal(document.byId.get("help-details").open, true);
    assert.equal(retry.hidden, false);
    retry.dispatchEvent(new Event("click"));
    assert.equal(retry.disabled, true);
    const retryRequest = webview.requests.find(({ type }) => type === "player.recover");
    assert.ok(retryRequest);
    webview.respond(retryRequest, {}, { ok: false, error: "Close and reopen Rot to retry." });
    await flush();
    assert.equal(document.byId.get("web-recovery-message").textContent, "Close and reopen Rot to retry.");
    assert.equal(retry.disabled, false);
  } finally {
    for (const [key, value] of priorGlobals) {
      if (value === undefined) delete globalThis[key];
      else globalThis[key] = value;
    }
  }
});

test("the retired combined surface and its configuration artifacts are absent", async () => {
  await assert.rejects(access(`${repositoryRoot}src/Rot.App/Web/search/index.html`));
  const paths = [
    "src/Rot.App/Web/common/constants.js",
    "src/Rot.App/Web/common/bridge.js",
    "src/Rot.App/Web/common/youtube.js",
    "src/Rot.App/Web/settings/index.html",
    "src/Rot.App/Web/settings/settings.js",
    "src/Rot.App/Web/player/player.js",
  ];
  const joined = (await Promise.all(paths.map((path) => readFile(`${repositoryRoot}${path}`, "utf8")))).join("\n");
  const qword = String.fromCharCode(113, 117, 101, 117, 101);
  for (const retired of [
    ["api", "Key"].join(""),
    ["search", "Quota"].join(""),
    ["www.", "googleapis.com", "/youtube/v3"].join(""),
    `${qword}.add`,
    `${qword}.remove`,
    `${qword}.de${qword}`,
  ]) {
    assert.equal(joined.includes(retired), false, retired);
  }
});
