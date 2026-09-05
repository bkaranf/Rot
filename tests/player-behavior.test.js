import assert from "node:assert/strict";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const repositoryRoot = fileURLToPath(new URL("../", import.meta.url));
const playerUrl = pathToFileURL(`${repositoryRoot}src/Rot.App/Web/player/player.js`).href;
const VIDEO_A = "dQw4w9WgXcQ";
const VIDEO_B = "M7lc1UVf-VE";

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
}

class FakeElement extends EventTarget {
  constructor(tagName, ownerDocument) {
    super();
    this.ownerDocument = ownerDocument;
    this.tagName = String(tagName).toUpperCase();
    this.classList = new FakeClassList();
    this.children = [];
    this.attributes = new Map();
    this.dataset = {};
    this.style = {};
    this.hidden = false;
    this.isConnected = false;
    this.textContent = "";
    this.src = "";
    this.id = "";
    this.parentNode = null;
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
    if (name === "id") {
      this.id = String(value);
      this.ownerDocument?.byId.set(this.id, this);
    }
  }

  getAttribute(name) {
    return this.attributes.get(name) || null;
  }

  append(...children) {
    for (const child of children) {
      if (!child) continue;
      this.children.push(child);
      child.parentNode = this;
      child.isConnected = this.isConnected;
    }
  }

  appendChild(child) {
    this.append(child);
    return child;
  }

  replaceChildren(...children) {
    for (const child of this.children) {
      child.isConnected = false;
      child.parentNode = null;
    }
    this.children = [];
    this.append(...children);
  }

  remove() {
    if (this.parentNode) {
      this.parentNode.children = this.parentNode.children.filter((child) => child !== this);
    }
    this.parentNode = null;
    this.isConnected = false;
  }

  closest() {
    return null;
  }
}

class FakeDocument extends EventTarget {
  constructor() {
    super();
    this.byId = new Map();
    this.elements = [];
    this.scripts = [];
    this.hidden = false;
    this.documentElement = new FakeElement("html", this);
    this.documentElement.isConnected = true;
    this.body = new FakeElement("body", this);
    this.body.isConnected = true;
    this.head = new FakeElement("head", this);
    this.head.isConnected = true;
  }

  add(id, tagName = "div") {
    const element = new FakeElement(tagName, this);
    element.id = id;
    element.isConnected = true;
    this.byId.set(id, element);
    this.elements.push(element);
    return element;
  }

  createElement(tagName) {
    const element = new FakeElement(tagName, this);
    this.elements.push(element);
    if (String(tagName).toLowerCase() === "script") this.scripts.push(element);
    return element;
  }

  querySelector(selector) {
    if (selector.startsWith("#")) {
      return this.byId.get(selector.slice(1)) ||
        this.elements.find((element) => element.id === selector.slice(1)) ||
        null;
    }
    if (selector === "script[data-rot-youtube-api]") {
      return this.scripts.find((script) => script.dataset.rotYoutubeApi === "true") || null;
    }
    return null;
  }
}

class FakeWindow extends EventTarget {
  #nextTimer = 1;
  #timers = new Map();

  constructor() {
    super();
    this.setTimeout = this.setTimeout.bind(this);
    this.clearTimeout = this.clearTimeout.bind(this);
    this.setInterval = (...args) => globalThis["setInterval"](...args);
    this.clearInterval = (...args) => globalThis["clearInterval"](...args);
  }

  setTimeout(callback, delay = 0) {
    const id = this.#nextTimer++;
    const nativeId = globalThis.setTimeout(() => {
      if (!this.#timers.delete(id)) return;
      callback();
    }, delay);
    this.#timers.set(id, { nativeId, callback, delay });
    return id;
  }

  clearTimeout(id) {
    const timer = this.#timers.get(id);
    if (!timer) return;
    this.#timers.delete(id);
    globalThis.clearTimeout(timer.nativeId);
  }

  fire(delay) {
    for (const [id, timer] of [...this.#timers]) {
      if (timer.delay !== delay) continue;
      this.clearTimeout(id);
      timer.callback();
    }
  }

  dispose() {
    for (const id of this.#timers.keys()) this.clearTimeout(id);
  }
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function defaultState(media = VIDEO_A) {
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
    resume: {
      videoId: media,
      playlistId: null,
      seconds: 12,
      title: "Test video",
      thumbnailUrl: "",
    },
    runtime: {
      detectionState: "local",
      detectionAvailable: true,
      detectionMessage: "Training",
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
    },
  };
}

class FakeWebView extends EventTarget {
  constructor(state, { rejectMute = false, patchDelay = 0 } = {}) {
    super();
    this.state = clone(state);
    this.rejectMute = rejectMute;
    this.patchDelay = patchDelay;
    this.messages = [];
    this.requests = [];
  }

  postMessage(message) {
    this.messages.push(message);
    if (!message.requestId) return;
    this.requests.push(message);
    if (message.type === "state.get") {
      queueMicrotask(() => this.respond(message, { state: clone(this.state) }));
      return;
    }
    if (message.type === "settings.patch") {
      const shouldReject = this.rejectMute && Object.hasOwn(message.payload?.patch || {}, "muted");
      const respond = () => {
        if (shouldReject) {
          this.respond(message, {}, "The test host rejected the mute setting.");
          return;
        }
        this.state.settings = { ...this.state.settings, ...(message.payload?.patch || {}) };
        this.respond(message, { state: clone(this.state) });
      };
      if (this.patchDelay) globalThis.setTimeout(respond, this.patchDelay);
      else queueMicrotask(respond);
      return;
    }
    queueMicrotask(() => this.respond(message, {}));
  }

  respond(request, payload = {}, error = "") {
    this.dispatchEvent(new MessageEvent("message", {
      data: {
        type: "response",
        requestId: request.requestId,
        ok: !error,
        error,
        payload,
      },
    }));
  }

  emit(type, payload = {}) {
    this.dispatchEvent(new MessageEvent("message", { data: { type, payload } }));
  }
}

class FakeYouTubePlayer {
  constructor(id, options, document) {
    this.id = id;
    this.events = options.events;
    this.document = document;
    this.state = -1;
    this.muted = false;
    this.destroyed = false;
    this.calls = [];
    this.volume = 0;
  }

  getPlayerState() {
    return this.state;
  }

  getVideoUrl() {
    const iframe = this.document.querySelector(`#${this.id}`);
    const videoId = iframe?.src.match(/\/embed\/([^?]+)/)?.[1] || VIDEO_A;
    return `https://www.youtube.com/watch?v=${videoId}`;
  }

  getCurrentTime() {
    return 12;
  }

  setVolume(value) {
    this.calls.push(["setVolume", value]);
    this.volume = value;
  }

  mute() {
    this.calls.push(["mute"]);
    this.muted = true;
  }

  unMute() {
    this.calls.push(["unMute"]);
    this.muted = false;
  }

  isMuted() {
    this.calls.push(["isMuted"]);
    return this.muted;
  }

  playVideo() {
    this.calls.push(["playVideo"]);
  }

  pauseVideo() {
    this.calls.push(["pauseVideo"]);
  }

  nextVideo() {
    this.calls.push(["nextVideo"]);
  }

  destroy() {
    this.calls.push(["destroy"]);
    this.destroyed = true;
  }
}

function makeDocument() {
  const document = new FakeDocument();
  for (const id of [
    "player-window", "player-header", "player-stage", "player-notice", "browse-button",
    "settings-button", "pass-through-status", "interactivity-hotkey", "close-button",
    "resize-grip", "browse-hotkey", "detection-dot",
  ]) document.add(id, id === "player-stage" ? "main" : "div");
  return document;
}

async function flush() {
  await Promise.resolve();
  await new Promise((resolve) => setImmediate(resolve));
  await Promise.resolve();
}

let moduleId = 0;

async function loadPlayer(options = {}) {
  const document = makeDocument();
  const window = new FakeWindow();
  const webview = new FakeWebView(options.state || defaultState(), options);
  const players = [];
  class Player extends FakeYouTubePlayer {
    constructor(id, playerOptions) {
      super(id, playerOptions, document);
      players.push(this);
    }
  }
  const priorGlobals = new Map([
    ["document", globalThis.document],
    ["window", globalThis.window],
    ["chrome", globalThis.chrome],
    ["location", globalThis.location],
    ["YT", globalThis.YT],
    ["Element", globalThis.Element],
  ]);
  Object.assign(globalThis, {
    document,
    window,
    chrome: { webview },
    location: { href: "https://rot.local/player/" },
    Element: FakeElement,
    YT: {
      Player,
      PlayerState: { UNSTARTED: -1, ENDED: 0, PLAYING: 1, PAUSED: 2, BUFFERING: 3, CUED: 5 },
    },
  });
  await import(`${playerUrl}?behavior=${++moduleId}`);
  await flush();
  return {
    document,
    window,
    webview,
    players,
    async dispose() {
      window.dispatchEvent(new Event("beforeunload"));
      window.dispose();
      for (const [name, value] of priorGlobals) {
        if (value === undefined) delete globalThis[name];
        else globalThis[name] = value;
      }
      await flush();
    },
  };
}

function lastCommandResult(webview, commandId) {
  return webview.messages
    .filter((message) => message.type === "player.command.result" && message.payload?.commandId === commandId)
    .at(-1)?.payload;
}

function command(webview, commandId, name, payload = {}) {
  webview.emit("player.command", { ...payload, command: name, commandId });
}

async function ready(harness, player = harness.players.at(-1)) {
  player.events.onReady({ target: player });
  await flush();
}

test("late onReady after native pause cannot start the YouTube player", async (t) => {
  const harness = await loadPlayer();
  t.after(() => harness.dispose());
  const stale = harness.players[0];

  command(harness.webview, "play-before-ready", "play");
  command(harness.webview, "pause-before-ready", "pause");
  stale.events.onStateChange({ data: 1 });
  stale.events.onReady({ target: stale });
  await flush();

  assert.equal(stale.calls.some(([name]) => name === "playVideo"), false);
  assert.equal(stale.calls.some(([name]) => name === "pauseVideo"), true);
});

test("late PLAYING after native pause is re-paused instead of granting playback", async (t) => {
  const harness = await loadPlayer();
  t.after(() => harness.dispose());
  const player = harness.players[0];
  await ready(harness, player);
  player.calls.length = 0;

  command(harness.webview, "play", "play");
  player.state = 1;
  player.events.onStateChange({ data: 1 });
  command(harness.webview, "pause", "pause");
  player.events.onStateChange({ data: 1 });
  player.state = 2;
  player.events.onStateChange({ data: 2 });
  await flush();

  assert.equal(player.calls.filter(([name]) => name === "pauseVideo").length >= 2, true);
  assert.equal(lastCommandResult(harness.webview, "pause")?.ok, true);
  assert.equal(lastCommandResult(harness.webview, "pause")?.desiredPlaying, false);
});

test("overlapping media loads keep the latest generation and destroy stale players", async (t) => {
  const harness = await loadPlayer();
  t.after(() => harness.dispose());
  const first = harness.players[0];
  command(harness.webview, "load-a", "load", { media: { videoId: VIDEO_A } });
  command(harness.webview, "load-b", "load", { media: { videoId: VIDEO_B } });
  await flush();

  const second = harness.players.at(-1);
  assert.notEqual(second, first);
  first.events.onReady({ target: first });
  await ready(harness, second);
  await flush();

  assert.equal(first.destroyed, true);
  assert.equal(second.destroyed, false);
  assert.equal(lastCommandResult(harness.webview, "load-a")?.ok, false);
  assert.equal(lastCommandResult(harness.webview, "load-b")?.ok, true);
  assert.match(harness.webview.messages.at(-1)?.payload?.videoId || "", new RegExp(`^${VIDEO_B}$`));
});

test("a newer play intent wins over an earlier pause acknowledgement", async (t) => {
  const harness = await loadPlayer();
  t.after(() => harness.dispose());
  const player = harness.players[0];
  await ready(harness, player);
  player.state = 1;

  command(harness.webview, "pause-old", "toggle-play-pause");
  command(harness.webview, "play-new", "play");
  player.events.onStateChange({ data: 2 });
  player.state = 1;
  player.events.onStateChange({ data: 1 });
  await flush();

  assert.equal(lastCommandResult(harness.webview, "pause-old")?.ok, false);
  assert.equal(lastCommandResult(harness.webview, "play-new")?.ok, true);
  assert.equal(lastCommandResult(harness.webview, "play-new")?.desiredPlaying, true);
});

test("pause reports failure when YouTube never acknowledges the pause", async (t) => {
  const harness = await loadPlayer();
  t.after(() => harness.dispose());
  const player = harness.players[0];
  await ready(harness, player);
  player.state = 1;

  command(harness.webview, "pause-timeout", "pause");
  await new Promise((resolve) => globalThis.setTimeout(resolve, 800));

  assert.equal(lastCommandResult(harness.webview, "pause-timeout")?.ok, false);
});

test("failed mute persistence leaves the embedded audio unchanged", async (t) => {
  const harness = await loadPlayer({ rejectMute: true });
  t.after(() => harness.dispose());
  const player = harness.players[0];
  await ready(harness, player);
  player.calls.length = 0;

  command(harness.webview, "mute-failure", "toggle-mute");
  await flush();

  assert.equal(lastCommandResult(harness.webview, "mute-failure")?.ok, false);
  assert.equal(player.muted, false);
  assert.equal(player.calls.some(([name]) => name === "mute"), false);
  assert.equal(harness.webview.requests.filter((request) => request.type === "settings.patch").length, 1);
});

test("fallback reload disables autoplay and ignores the destroyed player's late ready event", async (t) => {
  const harness = await loadPlayer();
  t.after(() => harness.dispose());
  const stale = harness.players[0];

  harness.window.fire(6000);
  await flush();
  const retry = harness.players.at(-1);
  assert.notEqual(retry, stale);
  stale.events.onReady({ target: stale });
  harness.window.fire(6000);
  await flush();

  assert.equal(stale.destroyed, true);
  assert.equal(retry.destroyed, true);
  const fallback = harness.document.byId.get("player-stage").children.at(-1);
  assert.equal(fallback.tagName, "IFRAME");
  assert.equal(new URL(fallback.src).searchParams.get("autoplay"), "0");
});
