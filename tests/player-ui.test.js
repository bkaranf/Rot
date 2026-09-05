import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = fileURLToPath(new URL("../", import.meta.url));

const [html, css, script, constants, theme, nativePlayer, controller] = await Promise.all([
  readFile(`${repositoryRoot}src/Rot.App/Web/player/index.html`, "utf8"),
  readFile(`${repositoryRoot}src/Rot.App/Web/player/player.css`, "utf8"),
  readFile(`${repositoryRoot}src/Rot.App/Web/player/player.js`, "utf8"),
  readFile(`${repositoryRoot}src/Rot.App/Web/common/constants.js`, "utf8"),
  readFile(`${repositoryRoot}src/Rot.App/Web/common/theme.css`, "utf8"),
  readFile(`${repositoryRoot}src/Rot.App/Views/PlayerWindow.xaml.cs`, "utf8"),
  readFile(`${repositoryRoot}src/Rot.App/Services/ApplicationController.cs`, "utf8"),
]);

test("player header contains status plus exactly Browse, Settings, and Close controls", () => {
  const header = html.match(/<header id="player-header"[\s\S]*?<\/header>/)?.[0] || "";
  assert.ok(header);
  assert.equal((header.match(/<button\b/g) || []).length, 3);
  assert.match(header, /id="detection-dot"/);
  assert.match(header, /id="browse-button"/);
  assert.match(header, /id="settings-button"/);
  assert.match(header, /id="close-button"/);
  assert.equal((header.match(/<svg\b/g) || []).length, 3);
  assert.doesNotMatch(html, /<footer\b|wordmark|rot-mark/i);
});

test("empty player state keeps an actionable Browse control", () => {
  assert.match(html, /class="empty-state__mark"[\s\S]*?icon-color\.png/);
  assert.match(html, /Find your next video/);
  assert.match(html, /Browse YouTube to get started\./);
  assert.match(html, /id="empty-browse-button"[^>]*>Browse YouTube<\/button>/);
  assert.match(script, /markImage\.src\s*=\s*["']\.\.\/assets\/icon-color\.png["']/);
  assert.match(script, /slogan\.textContent\s*=\s*["']Find your next video["']/);
  assert.match(script, /hint\.append\(["']Browse YouTube to get started\./);
  assert.match(script, /stage\.addEventListener\("click"[\s\S]*?show-browse/);
  assert.match(script, /Choose a video in Browse to enable playback controls\./);
});

test("empty player mark scales for compact windows without changing the three-control header", () => {
  assert.match(css, /\.empty-state__mark\s*\{[\s\S]*?width:\s*40px[\s\S]*?height:\s*40px/);
  assert.match(css, /@media\s*\(max-width:\s*420px\)[\s\S]*?\.empty-state__mark\s*\{[\s\S]*?width:\s*28px[\s\S]*?height:\s*28px/);
  const header = html.match(/<header id="player-header"[\s\S]*?<\/header>/)?.[0] || "";
  assert.equal((header.match(/<button\b/g) || []).length, 3);
});

test("player chrome fades after two seconds and returns on pointer activity", () => {
  assert.match(constants, /CHROME_IDLE_MS\s*=\s*2000/);
  assert.match(script, /setTimeout\(hideChrome, CHROME_IDLE_MS\)/);
  assert.match(script, /addEventListener\("pointermove", revealChrome/);
  assert.match(script, /addEventListener\("pointerenter", revealChrome/);
  assert.match(css, /\.player-window\.is-chrome-hidden \.player-chrome\s*\{[\s\S]*?opacity:\s*0/);
  assert.match(css, /\.player-window\.is-chrome-hidden \.player-header\s*\{[\s\S]*?pointer-events:\s*none/);
  assert.match(nativePlayer, /Interval\s*=\s*TimeSpan\.FromMilliseconds\(100\)/);
  assert.match(nativePlayer, /GetCursorPos[\s\S]*?PointerActivity\?\.Invoke/);
  assert.match(controller, /"pointer\.activity"/);
  assert.match(script, /bridge\.on\("pointer\.activity", \(\) => revealChrome\(\)\)/);
  assert.match(script, /playerWindow\.addEventListener\("focusin", revealChrome\)/);
  assert.match(css, /\.player-window\.is-chrome-hidden:focus-within \.player-chrome/);
});

test("resize remains keyboard reachable", () => {
  assert.match(script, /resizeGrip\.addEventListener\("keydown"[\s\S]*?fireWindowAction\("resize"/);
});

test("pass-through recovery pill stays in the header outside the fading subcontainer", () => {
  const header = html.match(/<header id="player-header"[\s\S]*?<\/header>/)?.[0] || "";
  const chromeStart = header.indexOf('id="player-chrome"');
  const pill = header.indexOf('id="pass-through-status"');
  const pillTag = header.lastIndexOf("<div", pill);
  const beforePill = header.slice(chromeStart, pillTag);
  assert.ok(chromeStart >= 0 && pill > chromeStart);
  assert.match(beforePill, /<div class="player-controls"[\s\S]*?<\/div>\s*<\/div>\s*$/);
  assert.match(css, /\.pass-through-status\s*\{/);
  assert.doesNotMatch(css, /is-chrome-hidden[^\{]*pass-through-status/);
  assert.match(script, /passThroughShortcutFailed/);
  assert.match(script, /Unavailable: use Settings/);
});

test("next command advances only an active YouTube playlist", () => {
  assert.match(script, /currentMedia\?\.playlistId\s*&&\s*playerReady/);
  assert.match(script, /player\.nextVideo\(\)/);
  const retiredEndpoint = ["de", String.fromCharCode(113, 117, 101, 117, 101)].join("");
  assert.equal(script.includes(retiredEndpoint), false);
});

test("native Browse input is parsed by the shared player parser with correlation", () => {
  assert.match(script, /bridge\.on\("browse\.parse", \(payload\) => parseBrowseInput\(payload\)\)/);
  assert.match(script, /bridge\.notify\("browse\.parse-result", \{ correlationId, media, error:/);
  assert.match(script, /parseYouTubeInput\(input\)/);
});

test("shared visual tokens match the v2 graphite and red palette", () => {
  const normalized = theme.toLowerCase();
  for (const token of ["#0a0a0b", "#1c1c1e", "#2c2c2e", "#e5383b", "#ffb340"]) {
    assert.ok(normalized.includes(token), token);
  }
  assert.match(theme, /Segoe UI Variable Display/);
  assert.doesNotMatch(theme, /\.rot-mark/);
});
