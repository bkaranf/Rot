import {
  getInitialState,
  HostBridge,
  log,
} from "../common/bridge.js";
import { withStateDefaults } from "../common/constants.js";
import { buildPassThroughPresentation } from "../player/pass-through.js";

const bridge = new HostBridge("settings");
const header = document.querySelector("#drag-header");
const closeButton = document.querySelector("#close-button");
const detectionMessage = document.querySelector("#detection-message");
const detectionStatus = document.querySelector("#detection-status");
const detectionDot = document.querySelector("#detection-dot");
const detectionLabel = document.querySelector("#detection-label");
const detectionActionMessage = document.querySelector("#detection-action-message");
const repairStatsButton = document.querySelector("#repair-stats-button");
const recoveryNotice = document.querySelector("#web-recovery-notice");
const recoveryMessage = document.querySelector("#web-recovery-message");
const recoveryButton = document.querySelector("#web-recovery-button");
const restartWarning = document.querySelector("#restart-warning");
const borderlessWarning = document.querySelector("#borderless-warning");
const autoRestoreToggle = document.querySelector("#auto-restore-toggle");
const volumeSlider = document.querySelector("#volume-slider");
const volumeLabel = document.querySelector("#volume-label");
const mutedToggle = document.querySelector("#muted-toggle");
const sizeButtons = [...document.querySelectorAll("[data-size]")];
const opacityButtons = [...document.querySelectorAll("[data-opacity]")];
const resetLayoutButton = document.querySelector("#reset-layout-button");
const playerMessage = document.querySelector("#player-message");
const appearanceMessage = document.querySelector("#appearance-message");
const passThroughGroup = document.querySelector("#pass-through-group");
const passThroughToggle = document.querySelector("#pass-through-toggle");
const passThroughDescription = document.querySelector("#pass-through-description");
const passThroughRecovery = document.querySelector("#pass-through-recovery");
const passThroughHotkey = document.querySelector("#pass-through-hotkey");
const passThroughMessage = document.querySelector("#pass-through-message");
const hotkeyFailureNotice = document.querySelector("#hotkey-failure-notice");
const hotkeyFailureHeading = document.querySelector("#hotkey-failure-heading");
const hotkeyFailureList = document.querySelector("#hotkey-failure-list");
const hotkeyDetails = document.querySelector("#hotkey-details");
const hotkeyList = document.querySelector("#hotkey-list");
const hotkeyEditor = document.querySelector("#hotkey-editor");
const hotkeyCaptureHint = document.querySelector("#hotkey-capture-hint");
const hotkeyApplyButton = document.querySelector("#hotkey-apply-button");
const hotkeyCancelButton = document.querySelector("#hotkey-cancel-button");
const hotkeyDefaultsButton = document.querySelector("#hotkey-defaults-button");
const hotkeyEditMessage = document.querySelector("#hotkey-edit-message");
const aboutVersion = document.querySelector("#about-version");
const aboutRevision = document.querySelector("#about-revision");
const checkUpdatesButton = document.querySelector("#check-updates-button");
const installUpdateButton = document.querySelector("#install-update-button");
const updateMessage = document.querySelector("#update-message");
const projectRepositoryButton = document.querySelector("#project-repository-button");
const projectReleasesButton = document.querySelector("#project-releases-button");
const projectHelpButton = document.querySelector("#project-help-button");
const helpDetails = document.querySelector("#help-details");
const playerCapabilitiesDetails = document.querySelector("#player-capabilities-details");
const playerCapabilitiesStatus = document.querySelector("#player-capabilities-status");
const playerCapabilitiesReason = document.querySelector("#player-capabilities-reason");

const unsubscribers = [];
let latestState = withStateDefaults();
let hotkeyFailuresActive = false;
let playerCapabilityErrorActive = false;
let detectionWarningActive = false;
let settingsPatchGeneration = 0;
let settingsStateGeneration = 0;
let hotkeySaveGeneration = 0;
let hotkeySaveInFlight = false;
let hotkeyEditorState = null;
let hotkeyNativeCaptureActive = false;
let updateActionBusy = false;
let updateActionMessage = "";
let updateActionError = false;
const hotkeyRows = new Map();
const settingsFeedback = new Map([
  [playerMessage, { generation: 0 }],
  [appearanceMessage, { generation: 0 }],
  [passThroughMessage, { generation: 0 }],
]);

const EMPTY_PLAYER_CAPABILITY_MESSAGE = "Choose a video in Browse to enable playback controls.";

const SHORTCUTS = Object.freeze([
  Object.freeze({ action: "toggle-overlay", stateKey: "togglePlayer", label: "Show or hide Player" }),
  Object.freeze({ action: "toggle-browse", stateKey: "toggleBrowse", label: "Open or close Browse" }),
  Object.freeze({ action: "toggle-playback", stateKey: "playPause", label: "Play or pause" }),
  Object.freeze({ action: "toggle-mute", stateKey: "mute", label: "Mute or unmute" }),
  Object.freeze({ action: "next", stateKey: "next", label: "Next video in the current playlist" }),
  Object.freeze({ action: "cycle-opacity", stateKey: "opacity", label: "Cycle player opacity" }),
  Object.freeze({ action: "toggle-interactivity", stateKey: "interactivity", label: "Toggle player pass-through" }),
]);

const HOTKEY_MODIFIERS = Object.freeze({
  alt: 1,
  ctrl: 2,
  shift: 4,
  win: 8,
});

const HOTKEY_PRIMARY_MODIFIERS = HOTKEY_MODIFIERS.alt | HOTKEY_MODIFIERS.ctrl | HOTKEY_MODIFIERS.win;
const HOTKEY_SPECIAL_KEYS = Object.freeze({
  ArrowLeft: 37,
  ArrowUp: 38,
  ArrowRight: 39,
  ArrowDown: 40,
  Home: 36,
  End: 35,
  PageUp: 33,
  PageDown: 34,
  Insert: 45,
  Delete: 46,
  Space: 32,
  " ": 32,
  Spacebar: 32,
});

const HOTKEY_RESERVED = new Set([
  "1:9", // Alt+Tab
  "1:27", // Alt+Escape
  "1:32", // Alt+Space
  "1:115", // Alt+F4
  "2:27", // Ctrl+Escape
  "6:27", // Ctrl+Shift+Escape
  "3:46", // Ctrl+Alt+Delete
  "8:9", // Win+Tab
  "8:32", // Win+Space
  "8:68", // Win+D
  "8:69", // Win+E
  "8:76", // Win+L
  "8:82", // Win+R
  "12:83", // Win+Shift+S
  "8:48", // Win+0
  "8:49", // Win+1
  "8:50", // Win+2
  "8:51", // Win+3
  "8:52", // Win+4
  "8:53", // Win+5
  "8:54", // Win+6
  "8:55", // Win+7
  "8:56", // Win+8
  "8:57", // Win+9
  "8:65", // Win+A
  "8:66", // Win+B
  "8:67", // Win+C
  "8:70", // Win+F
  "8:71", // Win+G
  "8:72", // Win+H
  "8:73", // Win+I
  "8:74", // Win+J
  "8:75", // Win+K
  "8:77", // Win+M
  "8:78", // Win+N
  "8:80", // Win+P
  "8:83", // Win+S
  "8:84", // Win+T
  "8:85", // Win+U
  "8:86", // Win+V
  "8:87", // Win+W
  "8:88", // Win+X
  "8:90", // Win+Z
]);

const HOTKEY_FAILURE_LABELS = Object.freeze({
  "toggle-overlay": "Show or hide Player",
  "toggle-browse": "Open or close Browse",
  "toggle-playback": "Play or pause",
  "toggle-mute": "Mute or unmute",
  next: "Next video in the current playlist",
  "cycle-opacity": "Cycle player opacity",
  "toggle-interactivity": "Toggle player pass-through",
});

function displayCopy(value) {
  return String(value ?? "").replace(/\u2014/g, ":").replace(/\u2013/g, "-");
}

function setMessage(element, message, isError = false) {
  element.textContent = displayCopy(message || "");
  element.classList.toggle("is-error", Boolean(isError));
}

function cloneHotkeyBinding(binding) {
  if (!binding || typeof binding !== "object") return null;
  const modifiers = Number(binding.modifiers);
  const virtualKey = Number(binding.virtualKey);
  if (!Number.isInteger(modifiers) || !Number.isInteger(virtualKey)) return null;
  return { modifiers, virtualKey };
}

function cloneHotkeyBindings(bindings) {
  const copy = {};
  for (const shortcut of SHORTCUTS) {
    const binding = cloneHotkeyBinding(bindings?.[shortcut.action]);
    if (binding) copy[shortcut.action] = binding;
  }
  return copy;
}

function hotkeyBindingsEqual(left, right) {
  return SHORTCUTS.every((shortcut) => {
    const leftBinding = left?.[shortcut.action];
    const rightBinding = right?.[shortcut.action];
    return Number(leftBinding?.modifiers) === Number(rightBinding?.modifiers)
      && Number(leftBinding?.virtualKey) === Number(rightBinding?.virtualKey);
  });
}

function currentHotkeyBindings(runtime) {
  const bindings = {};
  for (const shortcut of SHORTCUTS) {
    const binding = cloneHotkeyBinding(runtime.hotkeyBindings?.[shortcut.action])
      || cloneHotkeyBinding(runtime.hotkeyDefaults?.[shortcut.action]);
    if (binding) bindings[shortcut.action] = binding;
  }
  return bindings;
}

function defaultHotkeyBindings(runtime) {
  return currentHotkeyBindings({ hotkeyDefaults: runtime.hotkeyDefaults });
}

function hotkeyKeyName(virtualKey) {
  const special = Object.entries(HOTKEY_SPECIAL_KEYS).find(([, value]) => value === virtualKey);
  if (special) return special[0] === " " || special[0] === "Spacebar" ? "Space" : special[0];
  if (virtualKey >= 65 && virtualKey <= 90) return String.fromCharCode(virtualKey);
  if (virtualKey >= 48 && virtualKey <= 57) return String.fromCharCode(virtualKey);
  if (virtualKey >= 112 && virtualKey <= 135) return `F${virtualKey - 111}`;
  return `Key ${virtualKey}`;
}

function formatHotkeyBinding(binding) {
  const normalized = cloneHotkeyBinding(binding);
  if (!normalized) return "Unavailable";
  const parts = [];
  if (normalized.modifiers & HOTKEY_MODIFIERS.ctrl) parts.push("Ctrl");
  if (normalized.modifiers & HOTKEY_MODIFIERS.alt) parts.push("Alt");
  if (normalized.modifiers & HOTKEY_MODIFIERS.shift) parts.push("Shift");
  if (normalized.modifiers & HOTKEY_MODIFIERS.win) parts.push("Win");
  parts.push(hotkeyKeyName(normalized.virtualKey));
  return parts.join("+");
}

function keyVirtualCode(event) {
  const code = String(event.code || "");
  if (/^Key[A-Z]$/.test(code)) return code.charCodeAt(3);
  if (/^Digit[0-9]$/.test(code)) return code.charCodeAt(5);
  const key = String(event.key || "");
  if (/^[a-z]$/i.test(key)) return key.toUpperCase().charCodeAt(0);
  if (/^[0-9]$/.test(key)) return key.charCodeAt(0);
  if (Object.prototype.hasOwnProperty.call(HOTKEY_SPECIAL_KEYS, key)) return HOTKEY_SPECIAL_KEYS[key];
  const functionKey = /^F([1-9]|1[0-9]|2[0-4])$/i.exec(key);
  if (functionKey) return 111 + Number(functionKey[1]);
  return null;
}

function isSupportedHotkeyKey(virtualKey) {
  return (virtualKey >= 65 && virtualKey <= 90)
    || (virtualKey >= 48 && virtualKey <= 57)
    || (virtualKey >= 112 && virtualKey <= 135)
    || Object.values(HOTKEY_SPECIAL_KEYS).includes(virtualKey);
}

function validateHotkeyBinding(binding) {
  const modifiers = Number(binding?.modifiers);
  const virtualKey = Number(binding?.virtualKey);
  if (!Number.isInteger(modifiers) || !Number.isInteger(virtualKey)
    || modifiers < 0 || modifiers > 15 || !isSupportedHotkeyKey(virtualKey)) {
    return { error: "Add a letter, number, function, arrow, or navigation key." };
  }
  if (!(modifiers & HOTKEY_PRIMARY_MODIFIERS)) {
    return { error: "Use Ctrl, Alt, or Win with that key." };
  }
  if (HOTKEY_RESERVED.has(`${modifiers}:${virtualKey}`)) {
    return { error: "That shortcut is reserved by Windows." };
  }
  return { binding: { modifiers, virtualKey } };
}

function captureHotkeyBinding(event) {
  const modifiers = (event.altKey ? HOTKEY_MODIFIERS.alt : 0)
    | (event.ctrlKey ? HOTKEY_MODIFIERS.ctrl : 0)
    | (event.shiftKey ? HOTKEY_MODIFIERS.shift : 0)
    | (event.metaKey ? HOTKEY_MODIFIERS.win : 0);
  return validateHotkeyBinding({ modifiers, virtualKey: keyVirtualCode(event) });
}

function findDuplicateHotkey(bindings, action) {
  const candidate = bindings?.[action];
  if (!candidate) return null;
  return SHORTCUTS.find((shortcut) => shortcut.action !== action && (
    Number(bindings?.[shortcut.action]?.modifiers) === Number(candidate.modifiers)
    && Number(bindings?.[shortcut.action]?.virtualKey) === Number(candidate.virtualKey)
  )) || null;
}

function shortcutForAction(action) {
  return SHORTCUTS.find((shortcut) => shortcut.action === action) || null;
}

function displayedHotkey(runtime, shortcut) {
  const display = String(runtime.hotkeys?.[shortcut.stateKey] || "").trim();
  if (display === "Unavailable") return display;
  const binding = cloneHotkeyBinding(runtime.hotkeyBindings?.[shortcut.action]);
  return binding ? formatHotkeyBinding(binding) : displayCopy(display || "Unavailable");
}

function displayedHotkeys(runtime) {
  const hotkeys = { ...(runtime.hotkeys || {}) };
  for (const shortcut of SHORTCUTS) hotkeys[shortcut.stateKey] = displayedHotkey(runtime, shortcut);
  return hotkeys;
}

function renderDetection(runtime) {
  recoveryMessage.textContent = displayCopy(runtime.recoveryMessage || "");
  recoveryNotice.hidden = !runtime.recoveryMessage;
  recoveryButton.hidden = runtime.recoveryCanRetry !== true;
  if (runtime.recoveryCanRetry === true) helpDetails.open = true;
  const state = String(runtime.detectionState || "disconnected").toLowerCase();
  detectionDot.className = "status-dot";
  let label = "Waiting";
  if (runtime.restartRequired) {
    detectionDot.classList.add("is-warning");
    label = "Restart required";
  } else if (!runtime.detectionAvailable || state === "disconnected") {
    detectionDot.classList.add("is-disconnected");
    label = "Disconnected";
  } else if (state === "local") {
    detectionDot.classList.add("is-local");
    label = "Training";
  } else if (state === "online") {
    label = "Online match";
  } else if (state === "transition") {
    label = "Changing arenas";
  }
  detectionLabel.textContent = label;
  const message = displayCopy(runtime.detectionMessage || "Waiting for the read-only local Stats API.");
  detectionMessage.textContent = message;
  detectionStatus.title = message || label;
  restartWarning.hidden = !runtime.restartRequired;
  borderlessWarning.hidden = !runtime.borderlessWarning;
  const warningActive = Boolean(runtime.restartRequired || runtime.borderlessWarning);
  if (warningActive && !detectionWarningActive) helpDetails.open = true;
  detectionWarningActive = warningActive;
}

function hasHotkeyFailure(runtime, action) {
  return Array.isArray(runtime.hotkeyFailures) && runtime.hotkeyFailures.some(
    (failure) => String(failure?.action || "") === action,
  );
}

function createHotkeyRow(shortcut) {
  const row = document.createElement("div");
  row.className = "settings-detail-row";
  row.dataset.action = shortcut.action;
  const name = document.createElement("span");
  const binding = document.createElement("button");
  binding.type = "button";
  binding.className = "hotkey-binding-button";
  binding.dataset.action = shortcut.action;
  binding.addEventListener("click", () => startHotkeyCapture(shortcut.action));
  row.append(name, binding);
  return { row, name, binding };
}

function renderHotkeys(runtime) {
  const draftBindings = hotkeyEditorState?.bindings;
  const orderedChildren = [];
  for (const shortcut of SHORTCUTS) {
    const elements = hotkeyRows.get(shortcut.action) || createHotkeyRow(shortcut);
    hotkeyRows.set(shortcut.action, elements);
    const { row, name, binding } = elements;
    name.textContent = shortcut.label;
    const draftBinding = draftBindings?.[shortcut.action];
    const chord = draftBinding
      ? formatHotkeyBinding(draftBinding)
      : displayedHotkey(runtime, shortcut);
    binding.textContent = chord;
    binding.setAttribute("aria-label", `Change ${shortcut.label} shortcut, currently ${chord}`);
    binding.disabled = hotkeySaveInFlight;
    orderedChildren.push(row);
    if (hotkeyEditorState?.action === shortcut.action) orderedChildren.push(hotkeyEditor);
  }
  for (let index = 0; index < orderedChildren.length; index += 1) {
    const current = hotkeyList.children[index];
    if (current !== orderedChildren[index]) {
      hotkeyList.insertBefore(orderedChildren[index], current || null);
    }
  }
  if (hotkeyEditorState) {
    hotkeyEditor.append(hotkeyEditMessage);
  } else {
    hotkeyDetails.append(hotkeyEditor);
    hotkeyDetails.append(hotkeyEditMessage);
  }

  const failures = Array.isArray(runtime.hotkeyFailures) ? runtime.hotkeyFailures : [];
  const passThroughFailure = failures.some(
    (failure) => String(failure?.action || "") === "toggle-interactivity",
  );
  hotkeyFailureHeading.textContent = passThroughFailure
    ? "Pass-through recovery unavailable"
    : "Shortcut unavailable";
  hotkeyFailureList.replaceChildren();
  for (const failure of failures) {
    const item = document.createElement("li");
    const action = String(failure?.action || "shortcut");
    const label = HOTKEY_FAILURE_LABELS[action] || action;
    const chord = String(failure?.chord || "").trim();
    const message = displayCopy(String(failure?.message || "Registration failed.").trim());
    item.textContent = `${label}${chord ? ` (${chord})` : ""}: ${message}`;
    hotkeyFailureList.append(item);
  }
  hotkeyFailureNotice.hidden = failures.length === 0;
  const failuresActive = failures.length > 0;
  if (failuresActive && !hotkeyFailuresActive) {
    hotkeyDetails.open = true;
    helpDetails.open = true;
  }
  if (!failuresActive && hotkeyFailuresActive) hotkeyDetails.open = false;
  hotkeyFailuresActive = failuresActive;
}

function renderHotkeyEditor() {
  const editing = hotkeyEditorState;
  hotkeyEditor.hidden = !editing;
  hotkeyDefaultsButton.disabled = hotkeySaveInFlight;
  if (!editing) {
    hotkeyApplyButton.disabled = true;
    hotkeyCancelButton.disabled = true;
    return;
  }
  const shortcut = shortcutForAction(editing.action);
  hotkeyApplyButton.disabled = hotkeySaveInFlight || editing.capturing || !editing.dirty;
  hotkeyCancelButton.disabled = hotkeySaveInFlight;
  if (editing.capturing) {
    hotkeyCaptureHint.textContent = `Press a new shortcut for ${shortcut.label}. Escape cancels.`;
  } else if (editing.dirty) {
    hotkeyCaptureHint.textContent = `Captured ${formatHotkeyBinding(editing.bindings[editing.action])}. Choose Apply to save it, or Cancel.`;
  } else {
    hotkeyCaptureHint.textContent = `Press a new shortcut for ${shortcut.label}. Escape cancels.`;
  }
}

function renderPlayerCapabilities(runtime) {
  const capabilities = runtime.playerCapabilities || {};
  const reason = displayCopy(String(capabilities.reason || "").trim());
  const emptyPlayer = reason === EMPTY_PLAYER_CAPABILITY_MESSAGE;
  const startup = capabilities.ready !== true && (
    !reason ||
    reason === "Player is starting." ||
    reason === "YouTube player is starting." ||
    reason === EMPTY_PLAYER_CAPABILITY_MESSAGE
  );
  if (capabilities.ready === true && capabilities.appControls === true) {
    playerCapabilitiesStatus.textContent = "Ready: Rot controls are available.";
  } else if (capabilities.ready === true) {
    playerCapabilitiesStatus.textContent = "Ready: use YouTube's controls.";
  } else {
    playerCapabilitiesStatus.textContent = "Not ready for app-level control.";
  }
  playerCapabilitiesReason.textContent = emptyPlayer ? EMPTY_PLAYER_CAPABILITY_MESSAGE : reason || "Player is starting.";
  playerCapabilitiesReason.hidden = !playerCapabilitiesReason.textContent;
  const unavailable = capabilities.ready !== true || capabilities.appControls !== true;
  const capabilityErrorActive = unavailable && !startup;
  if (capabilityErrorActive && !playerCapabilityErrorActive) {
    playerCapabilitiesDetails.open = true;
    helpDetails.open = true;
  }
  if (!capabilityErrorActive && playerCapabilityErrorActive) playerCapabilitiesDetails.open = false;
  playerCapabilityErrorActive = capabilityErrorActive;
}

function renderAboutAndUpdates(runtime) {
  const update = runtime.update || {};
  const version = displayCopy(runtime.version || update.currentVersion || "Unavailable");
  const revision = displayCopy(runtime.revision || "");
  aboutVersion.textContent = `Version ${version}`;
  aboutRevision.textContent = revision ? `Revision ${revision}` : "Revision unavailable";

  const busy = updateActionBusy || update.busy === true;
  checkUpdatesButton.disabled = busy;
  installUpdateButton.disabled = busy || update.isUpdateAvailable !== true;
  const message = updateActionMessage
    || displayCopy(update.notice || update.message || "")
    || (update.isUpdateAvailable === true && update.latestVersion
      ? `Version ${displayCopy(update.latestVersion)} is available.`
      : "");
  setMessage(updateMessage, message, updateActionError);
}

function applyUpdateResult(result) {
  if (result?.state) {
    const state = withStateDefaults(result.state);
    if (result.update && typeof result.update === "object") {
      state.runtime.update = { ...state.runtime.update, ...result.update };
    }
    applyState(state);
    return;
  }
  if (result?.update && typeof result.update === "object") {
    applyState({
      ...latestState,
      runtime: { ...latestState.runtime, update: result.update },
    });
  }
}

function checkForUpdates() {
  if (updateActionBusy || latestState.runtime.update?.busy === true) return;
  updateActionBusy = true;
  updateActionError = false;
  updateActionMessage = "Checking for updates…";
  renderAboutAndUpdates(latestState.runtime);
  void bridge.request("updates.check", {}, { timeoutMs: 35000 })
    .then((result) => {
      updateActionMessage = "";
      updateActionError = false;
      applyUpdateResult(result);
    })
    .catch((error) => {
      updateActionMessage = error.message;
      updateActionError = true;
      renderAboutAndUpdates(latestState.runtime);
    })
    .finally(() => {
      updateActionBusy = false;
      renderAboutAndUpdates(latestState.runtime);
    });
}

function installUpdate() {
  const update = latestState.runtime.update || {};
  if (updateActionBusy || update.busy === true || update.isUpdateAvailable !== true) return;
  updateActionBusy = true;
  updateActionError = false;
  updateActionMessage = "Downloading and preparing the update...";
  renderAboutAndUpdates(latestState.runtime);
  void bridge.request("updates.install", {}, { timeoutMs: 360000 })
    .then((result) => {
      updateActionMessage = displayCopy(result?.update?.message || "Restarting Rot...");
      updateActionError = false;
      applyUpdateResult(result);
    })
    .catch((error) => {
      updateActionMessage = error.message;
      updateActionError = true;
      renderAboutAndUpdates(latestState.runtime);
    })
    .finally(() => {
      updateActionBusy = false;
      renderAboutAndUpdates(latestState.runtime);
    });
}

function openProject(target) {
  void bridge.request("project.open", { target }).catch((error) => {
    log("warn", `project link ${target} failed`, error);
  });
}

function setNativeHotkeyCapture(active) {
  const next = Boolean(active);
  if (hotkeyNativeCaptureActive === next) return;
  hotkeyNativeCaptureActive = next;
  void bridge.request("hotkeys.capture", { active: next }).catch((error) => {
    if (next && hotkeyEditorState?.capturing) {
      setMessage(hotkeyEditMessage, error.message, true);
    }
    log("debug", `native hotkey capture ${next ? "start" : "stop"} failed`, error);
  });
}

function startHotkeyCapture(action) {
  if (hotkeySaveInFlight) return;
  if (hotkeyEditorState && hotkeyEditorState.action !== action) {
    setMessage(hotkeyEditMessage, "Apply or cancel the current shortcut first.", true);
    return;
  }
  if (!hotkeyEditorState) {
    const bindings = currentHotkeyBindings(latestState.runtime);
    hotkeyEditorState = {
      action,
      bindings,
      originalBindings: cloneHotkeyBindings(bindings),
      capturing: true,
      dirty: false,
    };
  } else {
    hotkeyEditorState.capturing = true;
  }
  hotkeyDetails.open = true;
  setNativeHotkeyCapture(true);
  setMessage(hotkeyEditMessage, "");
  renderHotkeys(latestState.runtime);
  renderHotkeyEditor();
  hotkeyRows.get(action)?.binding.focus?.();
}

function cancelHotkeyEdit(message = "Shortcut change canceled.") {
  if (!hotkeyEditorState) return;
  const action = hotkeyEditorState.action;
  setNativeHotkeyCapture(false);
  hotkeyEditorState = null;
  renderHotkeys(latestState.runtime);
  renderHotkeyEditor();
  setMessage(hotkeyEditMessage, message);
  hotkeyRows.get(action)?.binding.focus?.();
}

function applyCapturedHotkey(binding) {
  if (!hotkeyEditorState?.capturing || hotkeySaveInFlight) return;
  const captured = validateHotkeyBinding(binding);
  if (captured.error) {
    setMessage(hotkeyEditMessage, captured.error, true);
    return;
  }
  const bindings = cloneHotkeyBindings(hotkeyEditorState.bindings);
  bindings[hotkeyEditorState.action] = captured.binding;
  const duplicate = findDuplicateHotkey(bindings, hotkeyEditorState.action);
  if (duplicate) {
    setMessage(hotkeyEditMessage, `That shortcut is already assigned to ${duplicate.label}.`, true);
    return;
  }
  hotkeyEditorState.bindings = bindings;
  hotkeyEditorState.capturing = false;
  hotkeyEditorState.dirty = !hotkeyBindingsEqual(
    hotkeyEditorState.originalBindings,
    hotkeyEditorState.bindings,
  );
  setNativeHotkeyCapture(false);
  setMessage(hotkeyEditMessage, "");
  renderHotkeys(latestState.runtime);
  renderHotkeyEditor();
}

function handleHotkeyCapture(event) {
  if (!hotkeyEditorState?.capturing || hotkeySaveInFlight) return;
  if (event.key === "Escape") {
    event.preventDefault();
    cancelHotkeyEdit();
    return;
  }
  event.preventDefault();
  const captured = captureHotkeyBinding(event);
  if (captured.error) {
    setMessage(hotkeyEditMessage, captured.error, true);
    return;
  }
  applyCapturedHotkey(captured.binding);
}

async function saveHotkeyBindings(bindings) {
  const editedAction = hotkeyEditorState?.action;
  const saveGeneration = ++hotkeySaveGeneration;
  const stateGeneration = ++settingsStateGeneration;
  const requestBindings = cloneHotkeyBindings(bindings);
  hotkeySaveInFlight = true;
  setNativeHotkeyCapture(false);
  renderHotkeys(latestState.runtime);
  renderHotkeyEditor();
  try {
    const result = await bridge.request("hotkeys.set", { bindings: requestBindings });
    if (saveGeneration !== hotkeySaveGeneration) return null;
    if (result?.state && stateGeneration === settingsStateGeneration) {
      hotkeyEditorState = null;
      applyState(result.state);
    } else {
      hotkeyEditorState = null;
      renderHotkeys(latestState.runtime);
      renderHotkeyEditor();
      if (editedAction) hotkeyRows.get(editedAction)?.binding.focus?.();
    }
    setMessage(hotkeyEditMessage, "Shortcuts saved.");
    return true;
  } catch (error) {
    if (saveGeneration !== hotkeySaveGeneration) return null;
    hotkeyEditorState = null;
    renderHotkeys(latestState.runtime);
    renderHotkeyEditor();
    setMessage(hotkeyEditMessage, error.message, true);
    return false;
  } finally {
    if (saveGeneration === hotkeySaveGeneration) {
      hotkeySaveInFlight = false;
      renderHotkeys(latestState.runtime);
      renderHotkeyEditor();
    }
  }
}

function applyHotkeyEdit() {
  if (!hotkeyEditorState || hotkeySaveInFlight) return;
  if (hotkeyEditorState.capturing) {
    setMessage(hotkeyEditMessage, "Press a complete shortcut before applying it.", true);
    return;
  }
  if (!hotkeyEditorState.dirty) {
    setMessage(hotkeyEditMessage, "Choose a new shortcut before applying it.", true);
    return;
  }
  void saveHotkeyBindings(hotkeyEditorState.bindings);
}

function restoreHotkeyDefaults() {
  if (hotkeySaveInFlight) return;
  const defaults = defaultHotkeyBindings(latestState.runtime);
  if (SHORTCUTS.some((shortcut) => !defaults[shortcut.action])) {
    setMessage(hotkeyEditMessage, "Shortcut defaults are unavailable.", true);
    return;
  }
  setNativeHotkeyCapture(false);
  hotkeyEditorState = null;
  setMessage(hotkeyEditMessage, "Saving…");
  renderHotkeys(latestState.runtime);
  renderHotkeyEditor();
  void saveHotkeyBindings(defaults);
}

function applyState(value) {
  latestState = withStateDefaults(value);
  const { settings, runtime } = latestState;
  renderDetection(runtime);

  autoRestoreToggle.checked = settings.autoRestoreAfterMatch;
  volumeSlider.value = String(settings.volume);
  volumeLabel.textContent = `${settings.volume}%`;
  mutedToggle.checked = settings.muted;
  for (const button of sizeButtons) {
    const active = button.dataset.size === settings.sizePreset;
    button.classList.toggle("is-active", active);
    button.setAttribute("aria-pressed", String(active));
  }
  for (const button of opacityButtons) {
    const active = Number(button.dataset.opacity) === Number(settings.opacity);
    button.classList.toggle("is-active", active);
    button.setAttribute("aria-pressed", String(active));
  }

  renderHotkeys(runtime);
  renderHotkeyEditor();
  renderPlayerCapabilities(runtime);
  renderAboutAndUpdates(runtime);
  const passThrough = buildPassThroughPresentation(settings.passThrough, displayedHotkeys(runtime));
  const passThroughShortcutFailed = hasHotkeyFailure(runtime, "toggle-interactivity");
  passThroughToggle.checked = passThrough.active;
  passThroughGroup.classList.toggle("is-active", passThrough.active);
  passThroughGroup.classList.toggle("has-hotkey-failure", passThroughShortcutFailed);
  passThroughRecovery.hidden = !passThrough.active && !passThroughShortcutFailed;
  passThroughDescription.textContent = passThroughShortcutFailed
    ? passThrough.active
      ? "On: clicks pass through to Rocket League. Keep Settings open because the recovery shortcut is unavailable."
      : "Off: the Player accepts mouse input. The recovery shortcut is unavailable; use this switch."
    : passThrough.active
      ? "On: clicks pass through to Rocket League."
      : "Off: the Player accepts mouse input.";
  passThroughHotkey.textContent = passThroughShortcutFailed
    ? "Unavailable: use Settings"
    : passThrough.interactivityBinding;
}

async function patchSettings(patch, messageElement = playerMessage) {
  const generation = ++settingsPatchGeneration;
  const stateGeneration = ++settingsStateGeneration;
  const feedback = settingsFeedback.get(messageElement) || settingsFeedback.get(playerMessage);
  const feedbackGeneration = ++feedback.generation;
  const isCurrentFeedback = () => feedbackGeneration === feedback.generation;
  const isCurrentState = () => generation === settingsPatchGeneration
    && stateGeneration === settingsStateGeneration;
  setMessage(messageElement, "Saving…");
  try {
    const result = await bridge.request("settings.patch", { patch });
    if (isCurrentState() && result?.state) applyState(result.state);
    if (!isCurrentFeedback()) return null;
    setMessage(messageElement, "Saved.");
    return true;
  } catch (error) {
    if (!isCurrentFeedback()) return null;
    setMessage(messageElement, error.message, true);
    if (!isCurrentState()) return false;
    try {
      const state = await getInitialState(bridge);
      if (!isCurrentState() || !isCurrentFeedback()) return false;
      applyState(state);
    } catch (resyncError) {
      if (!isCurrentState() || !isCurrentFeedback()) return false;
      log("warn", "settings state resync failed after a rejected patch", resyncError);
      applyState(latestState);
    }
    if (!isCurrentFeedback()) return false;
    setMessage(messageElement, error.message, true);
    return false;
  }
}

function hideSettings() {
  setNativeHotkeyCapture(false);
  hotkeyEditorState = null;
  bridge.notify("window.action", { window: "settings", action: "hide" });
}

function bindUi() {
  recoveryButton.addEventListener("click", () => {
    recoveryButton.disabled = true;
    void bridge.request("player.recover", {}, { timeoutMs: 30000 })
      .then((result) => { if (result?.state) applyState(result.state); })
      .catch((error) => { recoveryMessage.textContent = error.message; })
      .finally(() => { recoveryButton.disabled = false; });
  });
  closeButton.addEventListener("click", hideSettings);
  header.addEventListener("mousedown", (event) => {
    if (event.button !== 0 || event.target.closest("[data-no-drag]")) return;
    event.preventDefault();
    bridge.notify("window.action", { window: "settings", action: "drag" });
  });
  document.addEventListener("keydown", (event) => {
    if (hotkeyEditorState) {
      if (hotkeyEditorState.capturing) {
        handleHotkeyCapture(event);
        return;
      }
      if (event.key === "Escape") {
        event.preventDefault();
        cancelHotkeyEdit();
        return;
      }
    }
    if (event.key !== "Escape") return;
    event.preventDefault();
    hideSettings();
  });
  hotkeyApplyButton.addEventListener("click", applyHotkeyEdit);
  hotkeyCancelButton.addEventListener("click", () => cancelHotkeyEdit());
  hotkeyDefaultsButton.addEventListener("click", restoreHotkeyDefaults);
  checkUpdatesButton.addEventListener("click", checkForUpdates);
  installUpdateButton.addEventListener("click", installUpdate);
  projectRepositoryButton.addEventListener("click", () => openProject("repository"));
  projectReleasesButton.addEventListener("click", () => openProject("releases"));
  projectHelpButton.addEventListener("click", () => openProject("help"));

  repairStatsButton.addEventListener("click", () => {
    repairStatsButton.disabled = true;
    setMessage(detectionActionMessage, "Checking configuration…");
    void bridge.request("stats.repair", {}, { timeoutMs: 30000 })
      .then((result) => {
        if (result?.state) applyState(result.state);
        const message = result?.message || "Configuration checked.";
        setMessage(detectionActionMessage, message);
      })
      .catch((error) => {
        setMessage(detectionActionMessage, error.message, true);
      })
      .finally(() => { repairStatsButton.disabled = false; });
  });
  autoRestoreToggle.addEventListener("change", () => {
    void patchSettings({ autoRestoreAfterMatch: autoRestoreToggle.checked }, playerMessage);
  });
  volumeSlider.addEventListener("input", () => {
    volumeLabel.textContent = `${volumeSlider.value}%`;
  });
  volumeSlider.addEventListener("change", () => {
    void patchSettings({ volume: Number(volumeSlider.value) }, playerMessage);
  });
  mutedToggle.addEventListener("change", () => {
    void patchSettings({ muted: mutedToggle.checked }, playerMessage);
  });
  for (const button of sizeButtons) {
    button.addEventListener("click", () => {
      void patchSettings({ sizePreset: button.dataset.size }, appearanceMessage);
    });
  }
  for (const button of opacityButtons) {
    button.addEventListener("click", () => {
      void patchSettings({ opacity: Number(button.dataset.opacity) }, appearanceMessage);
    });
  }
  resetLayoutButton.addEventListener("click", () => {
    setMessage(appearanceMessage, "Resetting…");
    void bridge.request("layout.reset")
      .then((result) => {
        if (result?.state) applyState(result.state);
        setMessage(appearanceMessage, "Window positions reset.");
      })
      .catch((error) => {
        setMessage(appearanceMessage, error.message, true);
      });
  });
  passThroughToggle.addEventListener("change", () => {
    const requestedState = passThroughToggle.checked;
    const presentation = buildPassThroughPresentation(requestedState, latestState.runtime.hotkeys);
    const recoveryUnavailable = hasHotkeyFailure(latestState.runtime, "toggle-interactivity");
    void patchSettings({ passThrough: requestedState }, passThroughMessage)
      .then((saved) => {
        if (saved === true) {
          setMessage(
            passThroughMessage,
            recoveryUnavailable && requestedState
              ? "Pass-through enabled. Keep this Settings window open; the recovery shortcut is unavailable."
              : presentation.savedMessage,
          );
        } else if (saved === false) passThroughToggle.checked = latestState.settings.passThrough;
      });
  });

  window.addEventListener("beforeunload", () => {
    setNativeHotkeyCapture(false);
    for (const unsubscribe of unsubscribers) unsubscribe();
    bridge.destroy();
  }, { once: true });
}

async function refreshState() {
  try {
    applyState(await getInitialState(bridge));
  } catch (error) {
    setMessage(playerMessage, "Host unavailable.", true);
    log("error", "settings initialization failed", error);
  }
}

async function init() {
  bindUi();
  unsubscribers.push(
    bridge.on("state.changed", ({ state }) => applyState(state)),
    bridge.on("settings.focus", () => void refreshState()),
    bridge.on("hotkeys.captured", (binding) => applyCapturedHotkey(binding)),
  );
  bridge.start();
  await refreshState();
  log("info", "standalone settings initialized");
}

void init();
