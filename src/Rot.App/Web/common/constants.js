export const APP_ORIGIN = "https://rot.local";

export const HOTKEYS = Object.freeze({
  TOGGLE_PLAYER: "togglePlayer",
  TOGGLE_BROWSE: "toggleBrowse",
  PLAY_PAUSE: "playPause",
  MUTE: "mute",
  NEXT: "next",
  OPACITY: "opacity",
  INTERACTIVITY: "interactivity",
});

export const HOTKEY_ORDER = Object.freeze([
  HOTKEYS.TOGGLE_PLAYER,
  HOTKEYS.TOGGLE_BROWSE,
  HOTKEYS.PLAY_PAUSE,
  HOTKEYS.MUTE,
  HOTKEYS.NEXT,
  HOTKEYS.OPACITY,
  HOTKEYS.INTERACTIVITY,
]);

export const HOTKEY_FALLBACKS = Object.freeze({
  [HOTKEYS.TOGGLE_PLAYER]: "Ctrl+Shift+Y",
  [HOTKEYS.TOGGLE_BROWSE]: "Ctrl+Shift+F",
  [HOTKEYS.PLAY_PAUSE]: "Ctrl+Shift+K",
  [HOTKEYS.MUTE]: "Ctrl+Shift+M",
  [HOTKEYS.NEXT]: "Ctrl+Shift+N",
  [HOTKEYS.OPACITY]: "Ctrl+Shift+O",
  [HOTKEYS.INTERACTIVITY]: "Ctrl+Shift+P",
});

export const DEFAULT_HOTKEY_BINDINGS = Object.freeze({
  "toggle-overlay": Object.freeze({ modifiers: 6, virtualKey: 89 }),
  "toggle-browse": Object.freeze({ modifiers: 6, virtualKey: 70 }),
  "toggle-playback": Object.freeze({ modifiers: 6, virtualKey: 75 }),
  "toggle-mute": Object.freeze({ modifiers: 6, virtualKey: 77 }),
  next: Object.freeze({ modifiers: 6, virtualKey: 78 }),
  "cycle-opacity": Object.freeze({ modifiers: 6, virtualKey: 79 }),
  "toggle-interactivity": Object.freeze({ modifiers: 6, virtualKey: 80 }),
});

export const OPACITY_STEPS = Object.freeze([1, 0.85, 0.7, 0.55]);

export const SIZE_PRESETS = Object.freeze({
  compact: Object.freeze({ id: "compact", label: "Compact", width: 426, playerHeight: 240 }),
  medium: Object.freeze({ id: "medium", label: "Medium", width: 640, playerHeight: 360 }),
  large: Object.freeze({ id: "large", label: "Large", width: 854, playerHeight: 480 }),
});

export const CHROME_IDLE_MS = 2000;
export const RESUME_WRITE_INTERVAL_MS = 5000;
export const PLAYER_READY_TIMEOUT_MS = 6000;

export const DEFAULT_STATE = Object.freeze({
  schemaVersion: 2,
  settings: Object.freeze({
    volume: 75,
    muted: false,
    opacity: 1,
    sizePreset: "medium",
    passThrough: false,
    autoRestoreAfterMatch: true,
  }),
  resume: null,
  runtime: Object.freeze({
    version: "2.1.1",
    revision: "",
    detectionState: "disconnected",
    detectionAvailable: false,
    detectionMessage: "With Rocket League focused, manual hotkeys are available.",
    restartRequired: false,
    borderlessWarning: false,
    playerCapabilities: Object.freeze({ ready: false, appControls: false, reason: "Player is starting." }),
    hotkeyFailures: Object.freeze([]),
    hotkeys: Object.freeze({ ...HOTKEY_FALLBACKS }),
    hotkeyBindings: Object.freeze({ ...DEFAULT_HOTKEY_BINDINGS }),
    hotkeyDefaults: Object.freeze({ ...DEFAULT_HOTKEY_BINDINGS }),
    update: Object.freeze({
      currentVersion: "2.1.1",
      latestVersion: "2.1.1",
      isUpdateAvailable: false,
      message: "",
      busy: false,
      notice: "",
    }),
  }),
});

export function withStateDefaults(value) {
  const source = value && typeof value === "object" ? value : {};
  const settings = source.settings && typeof source.settings === "object" ? source.settings : {};
  const runtime = source.runtime && typeof source.runtime === "object" ? source.runtime : {};
  return {
    ...DEFAULT_STATE,
    ...source,
    settings: { ...DEFAULT_STATE.settings, ...settings },
    resume: source.resume || null,
    runtime: {
      ...DEFAULT_STATE.runtime,
      ...runtime,
      update: { ...DEFAULT_STATE.runtime.update, ...(runtime.update || {}) },
      hotkeys: { ...HOTKEY_FALLBACKS, ...(runtime.hotkeys || {}) },
      hotkeyBindings: { ...DEFAULT_HOTKEY_BINDINGS, ...(runtime.hotkeyBindings || {}) },
      hotkeyDefaults: { ...DEFAULT_HOTKEY_BINDINGS, ...(runtime.hotkeyDefaults || {}) },
      playerCapabilities: {
        ...DEFAULT_STATE.runtime.playerCapabilities,
        ...(runtime.playerCapabilities || {}),
      },
      hotkeyFailures: Array.isArray(runtime.hotkeyFailures) ? runtime.hotkeyFailures : [],
    },
  };
}
