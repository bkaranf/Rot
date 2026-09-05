const params = new URLSearchParams(globalThis.location.search);
const page = globalThis.location.pathname.includes("/player/") ? "player" : "settings";
const fixtureName = params.get("fixture") || "success";
const fixtureKey = "rot-ui-fixture-settings";

const fallbackHotkeys = {
  togglePlayer: "Ctrl+Shift+Y",
  toggleBrowse: "Ctrl+Shift+F",
  playPause: "Ctrl+Shift+K",
  mute: "Ctrl+Shift+M",
  next: "Ctrl+Shift+N",
  opacity: "Ctrl+Shift+O",
  interactivity: "Ctrl+Shift+P",
};

const fallbackHotkeyBindings = {
  "toggle-overlay": { modifiers: 6, virtualKey: 89 },
  "toggle-browse": { modifiers: 6, virtualKey: 70 },
  "toggle-playback": { modifiers: 6, virtualKey: 75 },
  "toggle-mute": { modifiers: 6, virtualKey: 77 },
  next: { modifiers: 6, virtualKey: 78 },
  "cycle-opacity": { modifiers: 6, virtualKey: 79 },
  "toggle-interactivity": { modifiers: 6, virtualKey: 80 },
};

const hotkeyActionToDisplay = {
  "toggle-overlay": "togglePlayer",
  "toggle-browse": "toggleBrowse",
  "toggle-playback": "playPause",
  "toggle-mute": "mute",
  next: "next",
  "cycle-opacity": "opacity",
  "toggle-interactivity": "interactivity",
};

function hotkeyDisplay(binding) {
  const modifiers = Number(binding?.modifiers) || 0;
  const virtualKey = Number(binding?.virtualKey) || 0;
  const parts = [];
  if (modifiers & 2) parts.push("Ctrl");
  if (modifiers & 1) parts.push("Alt");
  if (modifiers & 4) parts.push("Shift");
  if (modifiers & 8) parts.push("Win");
  if (virtualKey >= 65 && virtualKey <= 90) parts.push(String.fromCharCode(virtualKey));
  else if (virtualKey >= 48 && virtualKey <= 57) parts.push(String.fromCharCode(virtualKey));
  else if (virtualKey >= 112 && virtualKey <= 135) parts.push(`F${virtualKey - 111}`);
  else if (virtualKey === 32) parts.push("Space");
  else if (virtualKey === 37) parts.push("ArrowLeft");
  else if (virtualKey === 38) parts.push("ArrowUp");
  else if (virtualKey === 39) parts.push("ArrowRight");
  else if (virtualKey === 40) parts.push("ArrowDown");
  else if (virtualKey === 36) parts.push("Home");
  else if (virtualKey === 35) parts.push("End");
  else if (virtualKey === 33) parts.push("PageUp");
  else if (virtualKey === 34) parts.push("PageDown");
  else if (virtualKey === 45) parts.push("Insert");
  else if (virtualKey === 46) parts.push("Delete");
  return parts.join("+") || "Unavailable";
}

const baseSettings = {
  volume: 75,
  muted: false,
  opacity: 1,
  sizePreset: "medium",
  passThrough: false,
  autoRestoreAfterMatch: true,
};

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function savedSettings() {
  try {
    const parsed = JSON.parse(globalThis.sessionStorage?.getItem(fixtureKey) || "null");
    return parsed && typeof parsed === "object" ? parsed : {};
  } catch {
    return {};
  }
}

function saveSettings(settings) {
  try {
    globalThis.sessionStorage?.setItem(fixtureKey, JSON.stringify(settings));
  } catch {
    // Session storage is optional in browser automation contexts.
  }
}

function savedHotkeyBindings() {
  try {
    const parsed = JSON.parse(globalThis.sessionStorage?.getItem("rot-ui-fixture-hotkeys") || "null");
    return parsed && typeof parsed === "object" ? parsed : {};
  } catch {
    return {};
  }
}

function saveHotkeyBindings(bindings) {
  try {
    globalThis.sessionStorage?.setItem("rot-ui-fixture-hotkeys", JSON.stringify(bindings));
  } catch {
    // Session storage is optional in browser automation contexts.
  }
}

function stateFor(name) {
  const hotkeyBindings = { ...fallbackHotkeyBindings, ...savedHotkeyBindings() };
  const hotkeys = { ...fallbackHotkeys };
  for (const [action, stateKey] of Object.entries(hotkeyActionToDisplay)) {
    hotkeys[stateKey] = hotkeyDisplay(hotkeyBindings[action]);
  }
  const state = {
    schemaVersion: 2,
    settings: { ...baseSettings, ...savedSettings() },
    resume: null,
    runtime: {
      version: "2.1.0",
      revision: "fixture",
      detectionState: "disconnected",
      detectionAvailable: false,
      detectionMessage: "With Rocket League focused, manual hotkeys are available.",
      restartRequired: false,
      borderlessWarning: false,
      playerCapabilities: { ready: false, appControls: false, reason: "Player is starting." },
      hotkeyFailures: [],
      hotkeys,
      hotkeyBindings,
      hotkeyDefaults: clone(fallbackHotkeyBindings),
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

  if (name === "hotkey-unavailable") {
    state.runtime.hotkeyFailures = [{
      action: "toggle-interactivity",
      chord: "Ctrl+Shift+P",
      message: "The shortcut is already registered by another application.",
    }];
  } else if (name === "player-unavailable") {
    state.runtime.playerCapabilities = {
      ready: false,
      appControls: false,
      reason: "The YouTube player API is offline.",
    };
  } else if (name === "fallback") {
    state.runtime.playerCapabilities = {
      ready: false,
      appControls: false,
      reason: "App hotkeys cannot precisely control this fallback player.",
    };
  } else if (name === "long-text") {
    state.runtime.detectionMessage = "This intentionally long fixture message verifies that status copy wraps inside the available Settings column at narrow widths and at 200% page zoom without forcing horizontal scrolling.";
    state.runtime.hotkeyFailures = [{
      action: "toggle-interactivity",
      chord: "Ctrl+Shift+P",
      message: "This intentionally long registration failure explains that another global shortcut owns the chord and that pass-through can be recovered from Settings while this fixture is active.",
    }];
    state.runtime.playerCapabilities = {
      ready: false,
      appControls: false,
      reason: "The player is unavailable because this long-text fixture supplies a deliberately verbose capability explanation for responsive layout checks.",
    };
  } else if (name === "update-available") {
    state.runtime.update = {
      currentVersion: "2.1.0",
      latestVersion: "2.2.0",
      isUpdateAvailable: true,
      message: "Version 2.2.0 is ready to install.",
      busy: false,
      notice: "",
    };
  }
  return state;
}

let scenario = fixtureName;
let currentState = stateFor(scenario);
const listeners = new Set();
const requests = new Map();

function emit(message) {
  const event = new MessageEvent("message", { data: message });
  for (const listener of listeners) listener(event);
}

function respond(request, payload = {}, error = "") {
  emit({
    type: "response",
    requestId: request.requestId,
    ok: !error,
    error,
    payload,
  });
}

function publishState() {
  emit({ type: "state.changed", payload: { state: clone(currentState) } });
}

function setScenario(next) {
  scenario = next;
  currentState = stateFor(scenario);
  if (scenario === "reset") {
    try { globalThis.sessionStorage?.removeItem(fixtureKey); } catch { /* optional */ }
    try { globalThis.sessionStorage?.removeItem("rot-ui-fixture-hotkeys"); } catch { /* optional */ }
    scenario = "success";
    currentState = stateFor(scenario);
  }
  status.textContent = `Scenario: ${scenario}`;
  publishState();
}

const webview = {
  addEventListener(type, listener) {
    if (type === "message") listeners.add(listener);
  },
  removeEventListener(type, listener) {
    if (type === "message") listeners.delete(listener);
  },
  postMessage(message) {
    if (!message.requestId) {
      if (message.type === "player.capabilities") {
        currentState.runtime.playerCapabilities = { ...currentState.runtime.playerCapabilities, ...message.payload };
        publishState();
      } else if (message.type === "playback.save" && message.payload?.resume) {
        currentState.resume = clone(message.payload.resume);
      } else if (message.type === "window.action") {
        const windowName = String(message.payload?.window || "window");
        const action = String(message.payload?.action || "unknown");
        status.textContent = `Last action: ${windowName}/${action}`;
      }
      return;
    }

    requests.set(message.requestId, message);
    if (message.type === "state.get") {
      queueMicrotask(() => respond(message, { state: clone(currentState) }));
      return;
    }
    if (message.type === "settings.patch") {
      if (scenario === "settings-failure" || scenario === "patch-failure") {
        setTimeout(() => respond(message, {}, "Fixture rejected this settings write."), 180);
        return;
      }
      currentState.settings = { ...currentState.settings, ...(message.payload?.patch || {}) };
      saveSettings(currentState.settings);
      queueMicrotask(() => respond(message, { state: clone(currentState) }));
      publishState();
      return;
    }
    if (message.type === "hotkeys.set") {
      if (scenario === "hotkey-conflict") {
        queueMicrotask(() => respond(message, {}, "Fixture rejected this shortcut."));
        return;
      }
      const bindings = message.payload?.bindings;
      if (!bindings || typeof bindings !== "object" || Object.keys(fallbackHotkeyBindings).some((action) => !bindings[action])) {
        queueMicrotask(() => respond(message, {}, "Fixture received incomplete shortcut bindings."));
        return;
      }
      currentState.runtime.hotkeyBindings = clone(bindings);
      for (const [action, stateKey] of Object.entries(hotkeyActionToDisplay)) {
        currentState.runtime.hotkeys[stateKey] = hotkeyDisplay(bindings[action]);
      }
      saveHotkeyBindings(bindings);
      queueMicrotask(() => respond(message, { state: clone(currentState) }));
      publishState();
      return;
    }
    if (message.type === "hotkeys.capture") {
      queueMicrotask(() => respond(message, { state: clone(currentState) }));
      return;
    }
    if (message.type === "updates.check") {
      if (scenario === "update-error") {
        queueMicrotask(() => respond(message, {}, "Fixture could not check for updates."));
        return;
      }
      const update = scenario === "update-available"
        ? {
          currentVersion: "2.1.0",
          latestVersion: "2.2.0",
          isUpdateAvailable: true,
          message: "Version 2.2.0 is ready to install.",
          busy: false,
          notice: "",
        }
        : {
          ...currentState.runtime.update,
          currentVersion: "2.1.0",
          latestVersion: "2.1.0",
          isUpdateAvailable: false,
          message: "Rot is up to date.",
          busy: false,
          notice: "",
        };
      currentState.runtime.update = update;
      queueMicrotask(() => respond(message, { state: clone(currentState), update: clone(update) }));
      publishState();
      return;
    }
    if (message.type === "updates.install") {
      if (scenario === "update-install-error") {
        queueMicrotask(() => respond(message, {}, "Fixture could not install this update."));
        return;
      }
      currentState.runtime.update = {
        ...currentState.runtime.update,
        busy: false,
        message: "Restarting Rot...",
        notice: "",
      };
      queueMicrotask(() => respond(message, {
        state: clone(currentState),
        update: clone(currentState.runtime.update),
      }));
      publishState();
      return;
    }
    if (message.type === "project.open") {
      const target = String(message.payload?.target || "project");
      status.textContent = `Project link: ${target}`;
      queueMicrotask(() => respond(message, {}));
      return;
    }
    if (message.type === "stats.repair") {
      queueMicrotask(() => respond(message, { state: clone(currentState), message: "Fixture configuration is readable." }));
      return;
    }
    if (message.type === "layout.reset") {
      queueMicrotask(() => respond(message, { state: clone(currentState) }));
      return;
    }
    queueMicrotask(() => respond(message, {}));
  },
};

globalThis.chrome = { ...(globalThis.chrome || {}), webview };

const banner = document.createElement("details");
banner.id = "rot-fixture-banner";
banner.setAttribute("aria-label", "Isolated test fixture controls");
banner.style.cssText = "position:fixed;z-index:9999;right:8px;bottom:8px;max-width:calc(100% - 16px);padding:6px 8px;border:1px solid #ffb340;border-radius:6px;background:#241d10;color:#fff;font:12px Segoe UI,sans-serif";
const summary = document.createElement("summary");
summary.textContent = `Isolated test fixture (${page})${params.get("zoom") === "2" ? " · 200% page zoom test fixture" : ""}`;
const status = document.createElement("span");
status.textContent = `Scenario: ${scenario}`;
status.style.marginLeft = "8px";
const controls = document.createElement("div");
controls.style.cssText = "display:flex;flex-wrap:wrap;gap:4px;margin-top:6px";
summary.append(status);
banner.append(summary, controls);
for (const [name, text] of [
  ["success", "Success"],
  ["settings-failure", "Reject patches"],
  ["hotkey-unavailable", "Hotkey failure"],
  ["hotkey-conflict", "Reject shortcut"],
  ["player-unavailable", "Player unavailable"],
  ["long-text", "Long text"],
  ["update-available", "Update available"],
  ["update-error", "Update check error"],
  ["update-install-error", "Install error"],
  ["reset", "Reset prefs"],
]) {
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = text;
  button.addEventListener("click", () => setScenario(name));
  controls.append(button);
}
(document.body || document.documentElement).append(banner);

if (params.get("zoom") === "2") {
  document.body.style.zoom = "2";
}
