import {
  CHROME_IDLE_MS,
  HOTKEYS,
  HOTKEY_FALLBACKS,
  PLAYER_READY_TIMEOUT_MS,
  RESUME_WRITE_INTERVAL_MS,
  withStateDefaults,
} from "../common/constants.js";
import {
  getInitialState,
  HostBridge,
  log,
  openExternal,
} from "../common/bridge.js";
import {
  buildEmbedUrl,
  parseYouTubeInput,
  thumbnailForVideo,
} from "../common/youtube.js";
import { buildPassThroughPresentation } from "./pass-through.js";

const bridge = new HostBridge("player");
const playerWindow = document.querySelector("#player-window");
const header = document.querySelector("#player-header");
const stage = document.querySelector("#player-stage");
const notice = document.querySelector("#player-notice");
const browseButton = document.querySelector("#browse-button");
const settingsButton = document.querySelector("#settings-button");
const passThroughStatus = document.querySelector("#pass-through-status");
const interactivityHotkey = document.querySelector("#interactivity-hotkey");
const closeButton = document.querySelector("#close-button");
const resizeGrip = document.querySelector("#resize-grip");
const browseHotkey = document.querySelector("#browse-hotkey");
const detectionDot = document.querySelector("#detection-dot");

const EMPTY_PLAYER_CAPABILITY_MESSAGE = "Choose a video in Browse to enable playback controls.";

let latestState = withStateDefaults();
let currentMedia = null;
let player = null;
let playerReady = false;
let fallbackMode = false;
let youtubeApiPromise = null;
let readyTimer = 0;
let noticeTimer = 0;
let resumeTimer = 0;
let chromeTimer = 0;
let loadGeneration = 0;
let attemptToken = 0;
let lastResumeWriteAt = 0;
let initialResumeLoaded = false;
// Keep command intent separate from native permission. A late IFrame callback
// may observe PLAYING, but it can never grant permission after native pause.
let desiredPlaying = false;
let nativePlaybackAllowed = false;
let playbackIntent = 0;
let muteOperation = Promise.resolve();
const playerStateWaiters = new Set();
const unsubscribers = [];

function showNotice(message, durationMs = 0) {
  if (noticeTimer) window.clearTimeout(noticeTimer);
  noticeTimer = 0;
  notice.textContent = message || "";
  notice.hidden = !message;
  if (message && durationMs > 0) {
    noticeTimer = window.setTimeout(() => showNotice(""), durationMs);
  }
}

function hideChrome() {
  chromeTimer = 0;
  playerWindow.classList.add("is-chrome-hidden");
}

function revealChrome() {
  playerWindow.classList.remove("is-chrome-hidden");
  if (chromeTimer) window.clearTimeout(chromeTimer);
  chromeTimer = window.setTimeout(hideChrome, CHROME_IDLE_MS);
}

function clearTimers() {
  if (readyTimer) window.clearTimeout(readyTimer);
  if (noticeTimer) window.clearTimeout(noticeTimer);
  if (resumeTimer) window.clearInterval(resumeTimer);
  if (chromeTimer) window.clearTimeout(chromeTimer);
  readyTimer = 0;
  noticeTimer = 0;
  resumeTimer = 0;
  chromeTimer = 0;
}

function destroyPlayer() {
  if (readyTimer) window.clearTimeout(readyTimer);
  if (resumeTimer) window.clearInterval(resumeTimer);
  cancelPlayerStateWaiters();
  readyTimer = 0;
  resumeTimer = 0;
  playerReady = false;
  try {
    player?.destroy?.();
  } catch (error) {
    log("debug", "YouTube player cleanup returned an error", error);
  }
  player = null;
  stage.replaceChildren();
}

function createIframe(media, host, jsApi, autoplay) {
  const iframe = document.createElement("iframe");
  iframe.id = `rot-youtube-player-${loadGeneration}-${attemptToken}`;
  iframe.src = buildEmbedUrl(media, { host, jsApi, controls: true, autoplay });
  iframe.title = "YouTube video player";
  iframe.allow = "autoplay; encrypted-media; picture-in-picture";
  iframe.allowFullscreen = true;
  iframe.referrerPolicy = "strict-origin-when-cross-origin";
  return iframe;
}

function ensureYouTubeApi({ retry = false } = {}) {
  if (globalThis.YT?.Player) return Promise.resolve(globalThis.YT);
  if (retry) {
    document.querySelector("script[data-rot-youtube-api]")?.remove();
    youtubeApiPromise = null;
  }
  if (youtubeApiPromise) return youtubeApiPromise;

  youtubeApiPromise = new Promise((resolve, reject) => {
    const previousReady = globalThis.onYouTubeIframeAPIReady;
    globalThis.onYouTubeIframeAPIReady = () => {
      try {
        if (typeof previousReady === "function") previousReady();
      } finally {
        if (globalThis.YT?.Player) resolve(globalThis.YT);
        else reject(new Error("YouTube IFrame API loaded without YT.Player."));
      }
    };

    const script = document.createElement("script");
    script.src = "https://www.youtube.com/iframe_api";
    script.async = true;
    script.dataset.rotYoutubeApi = "true";
    script.addEventListener("error", () => {
      youtubeApiPromise = null;
      reject(new Error("The YouTube IFrame API could not be downloaded."));
    }, { once: true });
    document.head.append(script);
  });
  return youtubeApiPromise;
}

function renderEmptyState() {
  destroyPlayer();
  fallbackMode = false;
  setCapabilities(false, false, EMPTY_PLAYER_CAPABILITY_MESSAGE);
  const empty = document.createElement("div");
  empty.className = "empty-state";
  const mark = document.createElement("div");
  mark.className = "empty-state__mark";
  mark.setAttribute("aria-hidden", "true");
  const markImage = document.createElement("img");
  markImage.src = "../assets/icon-color.png";
  markImage.alt = "";
  mark.append(markImage);
  const slogan = document.createElement("p");
  slogan.className = "empty-state__slogan";
  slogan.textContent = "Find your next video";
  const hint = document.createElement("p");
  hint.className = "empty-state__hint";
  const hotkey = latestState.runtime.hotkeys?.[HOTKEYS.TOGGLE_BROWSE] ||
    HOTKEY_FALLBACKS[HOTKEYS.TOGGLE_BROWSE];
  const hotkeyLabel = document.createElement("kbd");
  hotkeyLabel.textContent = hotkey;
  hint.append("Browse YouTube to get started. ", hotkeyLabel);
  const browse = document.createElement("button");
  browse.id = "empty-browse-button";
  browse.className = "primary-button";
  browse.type = "button";
  browse.textContent = "Browse YouTube";
  empty.append(mark, slogan, hint, browse);
  stage.append(empty);
}

function renderError(title, detail, { browserUrl = null, retry = false } = {}) {
  destroyPlayer();
  const card = document.createElement("div");
  card.className = "player-error";
  const heading = document.createElement("p");
  heading.className = "player-error__title";
  heading.textContent = title;
  const body = document.createElement("p");
  body.className = "player-error__detail";
  body.textContent = detail;
  const actions = document.createElement("div");
  actions.className = "player-error__actions";
  if (browserUrl) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "secondary-button";
    button.textContent = "Open on YouTube";
    button.addEventListener("click", () => {
      void openExternal(bridge, browserUrl).catch((error) => showNotice(error.message, 3500));
    });
    actions.append(button);
  }
  if (retry) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "primary-button";
    button.textContent = "Retry player";
    button.addEventListener("click", () => void loadMedia(currentMedia, { retryApi: true }));
    actions.append(button);
  }
  card.append(heading, body);
  if (actions.childElementCount) card.append(actions);
  stage.append(card);
}

function errorMessage(code) {
  switch (Number(code)) {
    case 2:
      return ["Invalid YouTube video ID", "The link contains an invalid video identifier."];
    case 5:
      return ["YouTube HTML5 player error", "The video could not be played by YouTube's HTML5 player."];
    case 100:
      return ["Video unavailable", "This video was removed, is private, or could not be found."];
    case 101:
    case 150:
      return ["Embedding unavailable", "The uploader disabled embedding for this video."];
    case 153:
      return [
        "YouTube could not identify Rot",
        "YouTube rejected the embed's client identity (error 153). Rot is using its HTTPS origin and strict referrer policy; retry or open the video on YouTube.",
      ];
    default:
      return ["YouTube player error", `YouTube returned player error ${String(code)}.`];
  }
}

function mediaBrowserUrl() {
  const videoId = currentMedia?.videoId;
  const playlistId = currentMedia?.playlistId;
  if (videoId) {
    return `https://www.youtube.com/watch?v=${encodeURIComponent(videoId)}${playlistId ? `&list=${encodeURIComponent(playlistId)}` : ""}`;
  }
  return playlistId ? `https://www.youtube.com/playlist?list=${encodeURIComponent(playlistId)}` : null;
}

function setCapabilities(ready, appControls, reason = "") {
  bridge.notify("player.capabilities", { ready, appControls, reason });
}

function handlePlayerError(event, generation, token) {
  if (generation !== loadGeneration || token !== attemptToken) return;
  const code = Number(event?.data);
  const [title, detail] = errorMessage(code);
  attemptToken += 1;
  setCapabilities(false, false, detail);
  bridge.notify("player.status", { state: "error", errorCode: code, videoId: currentMedia?.videoId || null });
  renderError(title, detail, {
    browserUrl: [101, 150, 153].includes(code) ? mediaBrowserUrl() : null,
    retry: code === 5 || code === 153,
  });
  log("warn", `YouTube player error ${code}: ${detail}`);
}

function currentVideoId() {
  if (!playerReady || typeof player?.getVideoUrl !== "function") return currentMedia?.videoId || null;
  try {
    return parseYouTubeInput(player.getVideoUrl()).videoId || currentMedia?.videoId || null;
  } catch {
    return currentMedia?.videoId || null;
  }
}

function playbackSeconds() {
  if (!playerReady || typeof player?.getCurrentTime !== "function") return 0;
  try {
    return Math.max(0, Number(player.getCurrentTime()) || 0);
  } catch {
    return 0;
  }
}

function saveResume({ force = false } = {}) {
  if (!playerReady || !currentMedia) return null;
  const now = Date.now();
  if (!force && now - lastResumeWriteAt < RESUME_WRITE_INTERVAL_MS) return null;
  const resume = {
    videoId: currentVideoId(),
    playlistId: currentMedia.playlistId || null,
    seconds: playbackSeconds(),
    title: currentMedia.title || (currentMedia.playlistId ? "YouTube playlist" : "YouTube video"),
    thumbnailUrl: currentMedia.thumbnailUrl || "",
    updatedAt: new Date().toISOString(),
  };
  bridge.notify("playback.save", { resume });
  lastResumeWriteAt = now;
  return resume;
}

function startResumeTimer() {
  if (resumeTimer) window.clearInterval(resumeTimer);
  resumeTimer = window.setInterval(saveResume, RESUME_WRITE_INTERVAL_MS);
}

function stopResumeTimer({ save = true } = {}) {
  if (resumeTimer) window.clearInterval(resumeTimer);
  resumeTimer = 0;
  if (save) saveResume();
}

function stateName(value) {
  if (!globalThis.YT?.PlayerState) return "unknown";
  const names = new Map([
    [YT.PlayerState.UNSTARTED, "unstarted"],
    [YT.PlayerState.ENDED, "ended"],
    [YT.PlayerState.PLAYING, "playing"],
    [YT.PlayerState.PAUSED, "paused"],
    [YT.PlayerState.BUFFERING, "buffering"],
    [YT.PlayerState.CUED, "cued"],
  ]);
  return names.get(value) || "unknown";
}

function resolveStateWaiters(state) {
  for (const waiter of [...playerStateWaiters]) {
    if (waiter.expected.has(state)) {
      playerStateWaiters.delete(waiter);
      window.clearTimeout(waiter.timeout);
      waiter.resolve(true);
    }
  }
}

function cancelPlayerStateWaiters() {
  for (const waiter of [...playerStateWaiters]) {
    playerStateWaiters.delete(waiter);
    window.clearTimeout(waiter.timeout);
    waiter.resolve(false);
  }
}

function beginPlaybackIntent(allowed) {
  playbackIntent += 1;
  nativePlaybackAllowed = allowed;
  desiredPlaying = allowed;
  return playbackIntent;
}

function shouldAutoplay() {
  return nativePlaybackAllowed && desiredPlaying;
}

function isCurrentPlaybackOperation(intent, generation, playerAtStart) {
  return intent === playbackIntent &&
    generation === loadGeneration &&
    playerAtStart === player;
}

function waitForState(expected, timeoutMs = 750) {
  let current = null;
  try {
    current = typeof player?.getPlayerState === "function" ? player.getPlayerState() : null;
  } catch {
    current = null;
  }
  const expectedSet = new Set(expected);
  if (expectedSet.has(current)) return Promise.resolve(true);
  return new Promise((resolve) => {
    const waiter = { expected: expectedSet, resolve, timeout: 0 };
    waiter.timeout = window.setTimeout(() => {
      playerStateWaiters.delete(waiter);
      resolve(false);
    }, timeoutMs);
    playerStateWaiters.add(waiter);
  });
}

function handlePlayerState(event, generation, token) {
  if (generation !== loadGeneration || token !== attemptToken || !globalThis.YT?.PlayerState) return;
  const state = event?.data;
  resolveStateWaiters(state);
  if (state === YT.PlayerState.PLAYING) {
    if (nativePlaybackAllowed) {
      // A PLAYING notification is only an observation. Native permission must
      // already have been granted by an explicit play intent.
      desiredPlaying = true;
      startResumeTimer();
    } else {
      // YouTube can report a late PLAYING state after native pause or while a
      // new iframe is becoming ready. Re-assert the native pause immediately.
      desiredPlaying = false;
      stopResumeTimer({ save: false });
      callReadyPlayer("pauseVideo");
    }
  } else {
    if (state === YT.PlayerState.PAUSED || state === YT.PlayerState.ENDED) {
      if (!nativePlaybackAllowed) desiredPlaying = false;
    }
    stopResumeTimer();
  }

  bridge.notify("player.status", {
    state: stateName(state),
    videoId: currentVideoId(),
    seconds: playbackSeconds(),
  });
}

function onPlayerReady(event, generation, token) {
  if (generation !== loadGeneration || token !== attemptToken) {
    event?.target?.destroy?.();
    return;
  }
  if (readyTimer) window.clearTimeout(readyTimer);
  readyTimer = 0;
  player = event.target;
  playerReady = true;
  fallbackMode = false;
  try {
    player.setVolume(latestState.settings.volume);
    if (latestState.settings.muted) player.mute();
    else player.unMute();
    if (shouldAutoplay()) player.playVideo();
    else player.pauseVideo();
  } catch (error) {
    log("warn", "could not restore YouTube audio settings", error);
  }
  setCapabilities(true, true, "");
  bridge.notify("player.status", {
    state: "ready",
    videoId: currentVideoId(),
    seconds: playbackSeconds(),
  });
  showNotice("");
}

function enterControlsFallback(media) {
  attemptToken += 1;
  destroyPlayer();
  currentMedia = media;
  fallbackMode = true;
  const iframe = createIframe(media, "www.youtube-nocookie.com", false, shouldAutoplay());
  stage.append(iframe);
  const message = "App hotkeys cannot precisely control this fallback player. Use YouTube's controls. Rot will stop it for online matches by reloading the embed, so its exact resume position may not survive.";
  setCapabilities(false, false, message);
  bridge.notify("player.status", { state: "controls-fallback", videoId: media.videoId || null });
  showNotice(message);
}

async function attemptJsPlayer(media, host, attempt, generation, { retryApi = false } = {}) {
  if (generation !== loadGeneration) return;
  const token = ++attemptToken;
  destroyPlayer();
  currentMedia = media;
  fallbackMode = false;
  setCapabilities(false, false, "YouTube player is starting.");
  const iframe = createIframe(media, host, true, shouldAutoplay());
  stage.append(iframe);

  readyTimer = window.setTimeout(() => {
    if (generation !== loadGeneration || token !== attemptToken || playerReady) return;
    if (attempt === 0) {
      log("warn", "YouTube did not become ready; retrying the privacy-enhanced host");
      void attemptJsPlayer(media, "www.youtube-nocookie.com", 1, generation);
    } else {
      log("warn", "YouTube did not become ready after both hosts; using controls-only fallback");
      enterControlsFallback(media);
    }
  }, PLAYER_READY_TIMEOUT_MS);

  try {
    const YTApi = await ensureYouTubeApi({ retry: retryApi });
    if (generation !== loadGeneration || token !== attemptToken || !iframe.isConnected) return;
    player = new YTApi.Player(iframe.id, {
      events: {
        onReady: (event) => onPlayerReady(event, generation, token),
        onStateChange: (event) => handlePlayerState(event, generation, token),
        onError: (event) => handlePlayerError(event, generation, token),
        onAutoplayBlocked: () => {
          if (generation !== loadGeneration || token !== attemptToken) return;
          const message = "YouTube blocked autoplay. Press play once in the player; Rot will resume normal control afterward.";
          showNotice(message, 7000);
          bridge.notify("player.status", {
            state: "autoplay-blocked",
            videoId: currentMedia?.videoId || null,
            seconds: playbackSeconds(),
          });
        },
      },
    });
  } catch (error) {
    if (generation !== loadGeneration || token !== attemptToken) return;
    attemptToken += 1;
    setCapabilities(false, false, "The YouTube player API is offline.");
    renderError(
      "YouTube player is offline",
      "The IFrame Player API could not load. Check the connection and retry.",
      { retry: true },
    );
    bridge.notify("player.status", { state: "offline", videoId: media.videoId || null });
    log("warn", "YouTube IFrame API load failed", error);
  }
}

async function loadMedia(media, { retryApi = false } = {}) {
  if (!media?.videoId && !media?.playlistId) {
    loadGeneration += 1;
    attemptToken += 1;
    beginPlaybackIntent(false);
    currentMedia = null;
    renderEmptyState();
    return false;
  }
  const nextMedia = { ...media };
  const generation = ++loadGeneration;
  currentMedia = nextMedia;
  await attemptJsPlayer(nextMedia, "www.youtube.com", 0, generation, { retryApi });
  return generation === loadGeneration;
}

function callReadyPlayer(method, ...args) {
  if (!playerReady || fallbackMode || typeof player?.[method] !== "function") return false;
  try {
    player[method](...args);
    return true;
  } catch (error) {
    log("warn", `YouTube ${method} failed`, error);
    return false;
  }
}

async function togglePlayPause() {
  if (fallbackMode && currentMedia) {
    if (desiredPlaying) beginPlaybackIntent(false);
    else beginPlaybackIntent(true);
    enterControlsFallback(currentMedia);
    return true;
  }
  if (!playerReady || typeof player?.getPlayerState !== "function") {
    if (desiredPlaying) beginPlaybackIntent(false);
    else beginPlaybackIntent(true);
    return true;
  }
  const generationAtStart = loadGeneration;
  const playerAtStart = player;
  const state = player.getPlayerState();
  if (state === YT.PlayerState.PLAYING) {
    const intent = beginPlaybackIntent(false);
    const called = callReadyPlayer("pauseVideo");
    const acknowledged = called && await waitForState([YT.PlayerState.PAUSED, YT.PlayerState.ENDED]);
    if (acknowledged && isCurrentPlaybackOperation(intent, generationAtStart, playerAtStart)) {
      saveResume({ force: true });
    }
    return called && acknowledged && isCurrentPlaybackOperation(intent, generationAtStart, playerAtStart);
  }
  beginPlaybackIntent(true);
  return callReadyPlayer("playVideo");
}

async function pausePlayer() {
  const intent = beginPlaybackIntent(false);
  if (fallbackMode && currentMedia) {
    // A controls-only cross-origin iframe cannot be paused through the API.
    // Recreating it with autoplay off is the only reliable way to stop audio
    // before the native window hides for an online match.
    enterControlsFallback(currentMedia);
    return true;
  }
  if (!playerReady) return true;
  const generationAtStart = loadGeneration;
  const playerAtStart = player;
  const called = callReadyPlayer("pauseVideo");
  const acknowledged = called && await waitForState([YT.PlayerState.PAUSED, YT.PlayerState.ENDED]);
  if (acknowledged && isCurrentPlaybackOperation(intent, generationAtStart, playerAtStart)) {
    saveResume({ force: true });
  }
  return called && acknowledged && isCurrentPlaybackOperation(intent, generationAtStart, playerAtStart);
}

async function applyAudio({ volume, muted } = {}) {
  if (!playerReady) return false;
  if (Number.isFinite(Number(volume))) callReadyPlayer("setVolume", Math.max(0, Math.min(100, Number(volume))));
  if (muted === true) callReadyPlayer("mute");
  else if (muted === false) callReadyPlayer("unMute");
  return true;
}

async function toggleMuteOnce() {
  if (!playerReady || typeof player?.isMuted !== "function") return false;
  const playerAtStart = player;
  const generationAtStart = loadGeneration;
  let muted;
  try {
    muted = Boolean(player.isMuted());
  } catch (error) {
    log("warn", "could not read YouTube mute state", error);
    return false;
  }
  const nextMuted = !muted;
  try {
    // Commit the native setting first. A rejected save must not leave the
    // embedded player changed behind the Settings resynchronization.
    await bridge.request("settings.patch", { patch: { muted: nextMuted } });
  } catch (error) {
    log("warn", "could not persist mute state", error);
    return false;
  }
  if (generationAtStart !== loadGeneration || playerAtStart !== player || !playerReady) return false;
  return callReadyPlayer(nextMuted ? "mute" : "unMute");
}

async function toggleMute() {
  const operation = muteOperation.then(() => toggleMuteOnce());
  muteOperation = operation.catch(() => false);
  return operation;
}

async function advancePlaylist() {
  if (currentMedia?.playlistId && playerReady && typeof player?.nextVideo === "function") {
    try {
      player.nextVideo();
      return true;
    } catch (error) {
      log("warn", "YouTube nextVideo failed", error);
      return false;
    }
  }
  showNotice("No YouTube playlist is playing.", 2500);
  return false;
}

function parseBrowseInput(payload = {}) {
  const correlationId = String(payload.correlationId ?? payload.requestId ?? "");
  const input = payload.input ?? payload.value ?? payload.url ?? "";
  try {
    const parsed = parseYouTubeInput(input);
    const media = {
      ...parsed,
      title: parsed.playlistId && !parsed.videoId ? "YouTube playlist" : "YouTube video",
      thumbnailUrl: parsed.videoId ? thumbnailForVideo(parsed.videoId) : "",
    };
    bridge.notify("browse.parse-result", { correlationId, media, error: "" });
  } catch (error) {
    bridge.notify("browse.parse-result", {
      correlationId,
      media: null,
      error: error?.message || "That is not a recognized YouTube address.",
    });
  }
}

async function executeCommand(payload = {}) {
  const command = String(payload.command || "");
  let ok = false;
  let error = "";
  try {
    switch (command) {
      case "load":
        ok = await loadMedia(payload.media || {});
        break;
      case "clear":
        loadGeneration += 1;
        attemptToken += 1;
        currentMedia = null;
        fallbackMode = false;
        lastResumeWriteAt = 0;
        beginPlaybackIntent(false);
        showNotice("");
        renderEmptyState();
        ok = true;
        break;
      case "toggle-play-pause":
        ok = await togglePlayPause();
        break;
      case "play":
        beginPlaybackIntent(true);
        if (fallbackMode && currentMedia) {
          enterControlsFallback(currentMedia);
          ok = true;
        } else {
          ok = playerReady ? callReadyPlayer("playVideo") : true;
        }
        break;
      case "pause":
        ok = await pausePlayer();
        break;
      case "toggle-mute":
        ok = await toggleMute();
        break;
      case "next":
        ok = await advancePlaylist();
        break;
      case "apply-audio":
        ok = await applyAudio(payload);
        break;
      case "retry":
        ok = currentMedia ? await loadMedia(currentMedia, { retryApi: true }) : false;
        break;
      case "save-position":
        saveResume({ force: true });
        ok = true;
        break;
      default:
        error = `Unknown player command: ${command}`;
    }
  } catch (caught) {
    error = caught?.message || String(caught);
    log("warn", `player command ${command} failed`, caught);
  }
  let resultState = fallbackMode ? "controls-fallback" : "not-ready";
  if (playerReady && typeof player?.getPlayerState === "function") {
    try {
      resultState = stateName(player.getPlayerState());
    } catch {
      resultState = "ready";
    }
  }
  bridge.notify("player.command.result", {
    commandId: payload.commandId || null,
    command,
    ok,
    error: error || (!ok ? "The YouTube player is not ready for app-level control." : ""),
    state: resultState,
    seconds: playbackSeconds(),
    desiredPlaying,
  });
}

function applyDetectionState(runtime) {
  const state = String(runtime.detectionState || "disconnected").toLowerCase();
  detectionDot.className = "status-dot";
  if (runtime.restartRequired) {
    detectionDot.classList.add("is-warning");
  } else if (!runtime.detectionAvailable || state === "disconnected") {
    detectionDot.classList.add("is-disconnected");
  } else if (state === "local") {
    detectionDot.classList.add("is-local");
  }
  const message = runtime.detectionMessage || "Rocket League status unavailable.";
  detectionDot.title = message;
  detectionDot.setAttribute("aria-label", message);
}

function applyState(value) {
  latestState = withStateDefaults(value);
  const { settings, runtime } = latestState;
  browseHotkey.textContent = runtime.hotkeys[HOTKEYS.TOGGLE_BROWSE];
  const passThrough = buildPassThroughPresentation(settings.passThrough, runtime.hotkeys);
  const passThroughShortcutFailed = hasHotkeyFailure(runtime, "toggle-interactivity");
  passThroughStatus.hidden = !passThrough.active;
  interactivityHotkey.textContent = passThroughShortcutFailed
    ? "Unavailable: use Settings"
    : passThrough.interactivityBinding;
  applyDetectionState(runtime);
  void applyAudio({ volume: settings.volume, muted: settings.muted });

  if (!initialResumeLoaded) {
    initialResumeLoaded = true;
    if (latestState.resume) {
      void loadMedia({
        videoId: latestState.resume.videoId,
        playlistId: latestState.resume.playlistId,
        startSeconds: latestState.resume.seconds,
        title: latestState.resume.title,
        thumbnailUrl: latestState.resume.thumbnailUrl,
      });
    } else {
      renderEmptyState();
    }
  }
}

function fireWindowAction(action, extra = {}) {
  bridge.notify("window.action", { window: "player", action, ...extra });
}

function hasHotkeyFailure(runtime, action) {
  return Array.isArray(runtime.hotkeyFailures) && runtime.hotkeyFailures.some(
    (failure) => String(failure?.action || "") === action,
  );
}

function bindUi() {
  header.addEventListener("mousedown", (event) => {
    if (event.button !== 0 || event.target.closest("[data-no-drag]")) return;
    event.preventDefault();
    fireWindowAction("drag");
  });
  resizeGrip.addEventListener("mousedown", (event) => {
    if (event.button !== 0) return;
    event.preventDefault();
    fireWindowAction("resize", { edge: "bottom-right" });
  });
  resizeGrip.addEventListener("keydown", (event) => {
    if (event.key !== "Enter" && event.key !== " ") return;
    event.preventDefault();
    fireWindowAction("resize", { edge: "bottom-right" });
  });
  stage.addEventListener("click", (event) => {
    if (event.target instanceof Element && event.target.closest("#empty-browse-button")) {
      fireWindowAction("show-browse");
    }
  });
  browseButton.addEventListener("click", () => fireWindowAction("show-browse"));
  settingsButton.addEventListener("click", () => fireWindowAction("open-settings"));
  closeButton.addEventListener("click", () => {
    saveResume({ force: true });
    fireWindowAction("hide");
  });
  document.addEventListener("pointermove", revealChrome, { passive: true });
  window.addEventListener("pointerenter", revealChrome, { passive: true });
  playerWindow.addEventListener("focusin", revealChrome);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) hideChrome();
    else revealChrome();
  });
  window.addEventListener("beforeunload", () => {
    saveResume({ force: true });
    clearTimers();
    for (const waiter of playerStateWaiters) {
      window.clearTimeout(waiter.timeout);
      waiter.resolve(false);
    }
    playerStateWaiters.clear();
    for (const unsubscribe of unsubscribers) unsubscribe();
    bridge.destroy();
  }, { once: true });
  revealChrome();
}

async function init() {
  bindUi();
  unsubscribers.push(
    bridge.on("state.changed", ({ state }) => applyState(state)),
    bridge.on("player.command", (payload) => void executeCommand(payload)),
    bridge.on("browse.parse", (payload) => parseBrowseInput(payload)),
    bridge.on("pointer.activity", () => revealChrome()),
    bridge.on("runtime.notice", ({ message, durationMs }) => showNotice(String(message || ""), Number(durationMs) || 0)),
  );
  bridge.start();
  try {
    applyState(await getInitialState(bridge));
    log("info", "standalone player initialized");
  } catch (error) {
    renderError("Rot could not start", "The desktop host did not provide player state. Close and reopen Rot.");
    log("error", "player initialization failed", error);
  }
}

void init();
