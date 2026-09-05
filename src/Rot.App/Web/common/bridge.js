import { DEFAULT_STATE, withStateDefaults } from "./constants.js";

const BRIDGE_VERSION = 1;
const DEFAULT_TIMEOUT_MS = 8000;
const ALLOWED_EXTERNAL_HOSTS = new Set([
  "www.youtube.com",
  "youtube.com",
  "m.youtube.com",
  "youtu.be",
]);

function clone(value) {
  return value == null ? value : JSON.parse(JSON.stringify(value));
}

export function log(level, message, detail) {
  const method = level === "error" ? "error" : level === "warn" ? "warn" : "log";
  if (detail === undefined) console[method](`[rot] ${level}: ${message}`);
  else console[method](`[rot] ${level}: ${message}`, detail);
}

export function isAllowedExternalUrl(value) {
  try {
    const url = new URL(String(value));
    return url.protocol === "https:" && ALLOWED_EXTERNAL_HOSTS.has(url.hostname.toLowerCase());
  } catch {
    return false;
  }
}

class BrowserFallback {
  constructor(emit) {
    this.emit = emit;
    this.state = withStateDefaults(clone(DEFAULT_STATE));
  }

  async request(type, payload = {}) {
    switch (type) {
      case "state.get":
        return { state: clone(this.state) };
      case "settings.patch":
        this.state.settings = { ...this.state.settings, ...(payload.patch || {}) };
        this.changed();
        return { state: clone(this.state) };
      case "settings.reset":
        this.state = withStateDefaults(clone(DEFAULT_STATE));
        this.changed();
        return { state: clone(this.state) };
      case "external.open":
        if (!isAllowedExternalUrl(payload.url)) throw new Error("Rot refused to open an untrusted address.");
        globalThis.open?.(payload.url, "_blank", "noopener,noreferrer");
        return {};
      case "bridge.ready":
      case "layout.reset":
      case "player.capabilities":
      case "player.status":
      case "player.command.result":
      case "playback.save":
      case "browse.parse-result":
      case "stats.repair":
      case "window.action":
        return {};
      default:
        throw new Error(`The local preview host does not implement ${type}.`);
    }
  }

  changed() {
    Promise.resolve().then(() => this.emit("state.changed", { state: clone(this.state) }));
  }
}

export class HostBridge {
  constructor(view) {
    this.view = String(view || "unknown");
    this.webview = globalThis.chrome?.webview || null;
    this.nextId = 1;
    this.pending = new Map();
    this.listeners = new Map();
    this.fallback = new BrowserFallback((type, payload) => this.#emit(type, payload));
    if (this.webview) {
      this.webview.addEventListener("message", (event) => this.#receive(event.data));
    }
  }

  get available() {
    return Boolean(this.webview);
  }

  start() {
    this.notify("bridge.ready", {
      view: this.view,
      version: BRIDGE_VERSION,
      href: globalThis.location?.href || "",
    });
  }

  notify(type, payload = {}) {
    const message = { type, payload };
    if (this.webview) {
      this.webview.postMessage(message);
      return;
    }
    void this.fallback.request(type, payload).catch((error) => {
      log("debug", `preview notification ${type} was ignored`, error);
    });
  }

  request(type, payload = {}, { timeoutMs = DEFAULT_TIMEOUT_MS } = {}) {
    if (!this.webview) return this.fallback.request(type, payload);
    const requestId = `${this.view}-${Date.now().toString(36)}-${this.nextId++}`;
    return new Promise((resolve, reject) => {
      const timeout = globalThis.setTimeout(() => {
        this.pending.delete(requestId);
        reject(new Error(`Rot's desktop host did not answer ${type}.`));
      }, timeoutMs);
      this.pending.set(requestId, { resolve, reject, timeout, type });
      this.webview.postMessage({ type, requestId, payload });
    });
  }

  on(type, listener) {
    if (typeof listener !== "function") throw new TypeError("Bridge listener must be a function.");
    const listeners = this.listeners.get(type) || new Set();
    listeners.add(listener);
    this.listeners.set(type, listeners);
    return () => {
      listeners.delete(listener);
      if (!listeners.size) this.listeners.delete(type);
    };
  }

  destroy() {
    for (const pending of this.pending.values()) {
      globalThis.clearTimeout(pending.timeout);
      pending.reject(new Error("The Rot page was closed before the host replied."));
    }
    this.pending.clear();
    this.listeners.clear();
  }

  #receive(raw) {
    let message = raw;
    if (typeof raw === "string") {
      try {
        message = JSON.parse(raw);
      } catch {
        log("warn", "desktop host sent invalid JSON");
        return;
      }
    }
    if (!message || typeof message !== "object" || typeof message.type !== "string") {
      log("warn", "desktop host sent an invalid bridge envelope", message);
      return;
    }

    if (message.requestId && this.pending.has(String(message.requestId))) {
      const pending = this.pending.get(String(message.requestId));
      this.pending.delete(String(message.requestId));
      globalThis.clearTimeout(pending.timeout);
      if (message.ok === false) {
        pending.reject(new Error(String(message.error || `${pending.type} failed.`)));
      } else {
        pending.resolve(message.payload ?? {});
      }
      return;
    }
    this.#emit(message.type, message.payload ?? {});
  }

  #emit(type, payload) {
    for (const listener of this.listeners.get(type) || []) {
      try {
        listener(payload);
      } catch (error) {
        log("error", `bridge listener for ${type} failed`, error);
      }
    }
  }
}

export async function getInitialState(bridge) {
  const response = await bridge.request("state.get");
  return withStateDefaults(response?.state || response);
}

export async function openExternal(bridge, url) {
  if (!isAllowedExternalUrl(url)) throw new Error("Rot refused to open an untrusted address.");
  await bridge.request("external.open", { url });
}
