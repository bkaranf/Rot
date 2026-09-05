import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import test from "node:test";

import {
  NATIVE_HOST_NAME,
  createSendToRotRequest,
  sendCurrentTabToRot,
  validateYouTubeUrl,
} from "../browser-extension/extension.js";

function makeChrome({ response = { ok: true, message: "Selection ready." }, lastError = null } = {}) {
  const calls = {
    native: [],
    titles: [],
    badges: [],
    badgeColors: [],
  };
  const chromeApi = {
    tabs: {
      async query() {
        return [{ id: 17, url: "https://www.youtube.com/watch?v=video-1" }];
      },
    },
    action: {
      setTitle(details) {
        calls.titles.push(details);
      },
      setBadgeText(details) {
        calls.badges.push(details);
      },
      setBadgeBackgroundColor(details) {
        calls.badgeColors.push(details);
      },
    },
    runtime: {
      lastError,
      sendNativeMessage(hostName, message, callback) {
        calls.native.push({ hostName, message });
        queueMicrotask(() => callback(response));
      },
    },
  };
  return { chromeApi, calls };
}

test("extension validates only HTTPS YouTube video-shaped URLs", () => {
  assert.equal(
    validateYouTubeUrl("https://www.youtube.com/watch?v=video-1"),
    "https://www.youtube.com/watch?v=video-1",
  );
  assert.equal(validateYouTubeUrl("https://www.youtube.com/playlist?list=playlist"), "https://www.youtube.com/playlist?list=playlist");
  assert.equal(validateYouTubeUrl("https://www.youtube.com:444/watch?v=video-1"), null);
  assert.equal(validateYouTubeUrl("https://www.youtube.com/clip/clip-1"), null);
  assert.equal(validateYouTubeUrl("http://www.youtube.com/watch?v=video-1"), null);
  assert.equal(validateYouTubeUrl("https://www.youtube.com@evil.example/watch?v=video-1"), null);
});

test("toolbar click sends the minimal native request to Rot", async () => {
  const { chromeApi, calls } = makeChrome();
  const response = await sendCurrentTabToRot(chromeApi, {
    id: 23,
    url: "https://youtu.be/video-2?t=90",
  });

  assert.deepEqual(response, { ok: true, message: "Selection ready." });
  assert.deepEqual(calls.native, [{
    hostName: NATIVE_HOST_NAME,
    message: { action: "send-to-rot", url: "https://youtu.be/video-2?t=90" },
  }]);
  assert.deepEqual(calls.titles.at(-1), { tabId: 23, title: "Rot: Selection ready." });
  assert.deepEqual(calls.badges.at(-1), { tabId: 23, text: "Sent" });
  assert.deepEqual(calls.badgeColors.at(-1), { tabId: 23, color: "#188038" });
});

test("invalid tab URLs never reach native messaging and report a useful error", async () => {
  const { chromeApi, calls } = makeChrome();
  const response = await sendCurrentTabToRot(chromeApi, {
    id: 24,
    url: "https://example.com/watch?v=video-3",
  });

  assert.equal(response.ok, false);
  assert.match(response.message, /YouTube/);
  assert.deepEqual(calls.native, []);
  assert.match(calls.titles.at(-1).title, /Open a YouTube video/);
  assert.deepEqual(calls.badges.at(-1), { tabId: 24, text: "!" });
});

test("native host rejection and Chrome transport errors surface through the toolbar title", async () => {
  const rejected = makeChrome({
    response: { ok: false, message: "Rot is unavailable during an online match." },
  });
  const rejectedResponse = await sendCurrentTabToRot(rejected.chromeApi);
  assert.deepEqual(rejectedResponse, {
    ok: false,
    message: "Rot is unavailable during an online match.",
  });
  assert.match(rejected.calls.titles.at(-1).title, /online match/);
  assert.deepEqual(rejected.calls.badges.at(-1), { tabId: 17, text: "!" });

  const unavailable = makeChrome({
    lastError: { message: "Could not establish connection." },
  });
  const unavailableResponse = await sendCurrentTabToRot(unavailable.chromeApi);
  assert.equal(unavailableResponse.ok, false);
  assert.match(unavailableResponse.message, /Could not establish connection/);
});

test("manifest stays minimal and carries a stable public key", async () => {
  const manifest = JSON.parse(await readFile(new URL("../browser-extension/manifest.json", import.meta.url)));
  const hostManifest = JSON.parse(
    await readFile(new URL("../browser-extension/native-host-manifest.template.json", import.meta.url)),
  );

  assert.deepEqual(manifest.permissions, ["activeTab", "nativeMessaging"]);
  assert.equal(manifest.host_permissions, undefined);
  assert.equal(manifest.content_scripts, undefined);
  assert.equal(manifest.version, "2.1.0");
  assert.deepEqual(manifest.action.default_icon, {
    "16": "icons/icon-16.png",
    "32": "icons/icon-32.png",
    "48": "icons/icon-48.png",
    "128": "icons/icon-128.png",
  });
  for (const size of [16, 32, 48, 128]) {
    const icon = await readFile(new URL(`../browser-extension/icons/icon-${size}.png`, import.meta.url));
    assert.ok(icon.length > 0);
  }
  assert.match(manifest.key, /^[A-Za-z0-9+/=]+$/);
  const alphabet = "abcdefghijklmnop";
  const digest = createHash("sha256").update(Buffer.from(manifest.key, "base64")).digest();
  const extensionId = [...digest.subarray(0, 16)]
    .flatMap((byte) => [alphabet[(byte >>> 4) & 0x0f], alphabet[byte & 0x0f]])
    .join("");
  assert.deepEqual(hostManifest.allowed_origins, [`chrome-extension://${extensionId}/`]);
  assert.equal(manifest.background.type, "module");
});
