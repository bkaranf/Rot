import { HOTKEY_FALLBACKS, HOTKEYS } from "../common/constants.js";

function bindingOrFallback(value, hotkeyName) {
  const candidate = String(value || "").trim();
  return candidate || HOTKEY_FALLBACKS[hotkeyName];
}

export function buildPassThroughPresentation(active, hotkeys = {}) {
  const interactivityBinding = bindingOrFallback(
    hotkeys[HOTKEYS.INTERACTIVITY],
    HOTKEYS.INTERACTIVITY,
  );
  const browseBinding = bindingOrFallback(
    hotkeys[HOTKEYS.TOGGLE_BROWSE],
    HOTKEYS.TOGGLE_BROWSE,
  );

  if (active) {
    return {
      active: true,
      badge: "Pass-through on",
      interactivityBinding,
      browseBinding,
      recoveryPill: `Pass-through · ${interactivityBinding}`,
      playerButtonLabel: "Pass-through is on: open pass-through settings",
      settingsDescription: `On: the player ignores all mouse input. Settings stays interactive. Press ${interactivityBinding} to restore player controls.`,
      savedMessage: `Pass-through enabled. Keep this Settings window open, or press ${interactivityBinding} at any time to restore player controls.`,
    };
  }

  return {
    active: false,
    badge: "Player interactive",
    interactivityBinding,
    browseBinding,
    recoveryPill: "",
    playerButtonLabel: "Open pass-through settings",
    settingsDescription: `Off: the player accepts mouse input. You can also toggle this globally with ${interactivityBinding}.`,
    savedMessage: "Player interactivity restored.",
  };
}
