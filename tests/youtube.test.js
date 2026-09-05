import assert from "node:assert/strict";
import test from "node:test";

import {
  buildEmbedUrl,
  parseTimestamp,
  parseYouTubeInput,
} from "../src/Rot.App/Web/common/youtube.js";

const VIDEO_ID = "dQw4w9WgXcQ";
const PLAYLIST_ID = "PL1234567890abcdef";

test("all supported YouTube video forms resolve to the same ID", () => {
  const inputs = [
    `https://www.youtube.com/watch?v=${VIDEO_ID}`,
    `https://youtu.be/${VIDEO_ID}`,
    `https://www.youtube.com/embed/${VIDEO_ID}`,
    `https://www.youtube.com/shorts/${VIDEO_ID}`,
    `www.youtu.be/${VIDEO_ID}`,
    `m.youtube.com/watch?v=${VIDEO_ID}`,
    VIDEO_ID,
  ];

  for (const input of inputs) {
    assert.equal(parseYouTubeInput(input).videoId, VIDEO_ID, input);
  }
});

test("timestamps accept seconds, unit notation, colon notation, and fragments", () => {
  assert.equal(parseTimestamp("90"), 90);
  assert.equal(parseTimestamp("1m30s"), 90);
  assert.equal(parseTimestamp("1:30"), 90);
  assert.equal(parseTimestamp("1h2m3s"), 3723);
  assert.equal(parseTimestamp("nonsense"), 0);
  assert.equal(parseYouTubeInput(`https://www.youtube.com/watch?v=${VIDEO_ID}&t=90`).startSeconds, 90);
  assert.equal(parseYouTubeInput(`https://youtu.be/${VIDEO_ID}#t=1m30s`).startSeconds, 90);
});

test("watch and Shorts sources retain playlists and start times", () => {
  for (const path of ["watch?v=", "shorts/"]) {
    const separator = path.startsWith("watch") ? "&" : "?";
    const parsed = parseYouTubeInput(
      `https://www.youtube.com/${path}${VIDEO_ID}${separator}list=${PLAYLIST_ID}&start=17`,
    );
    assert.deepEqual(
      { videoId: parsed.videoId, playlistId: parsed.playlistId, startSeconds: parsed.startSeconds },
      { videoId: VIDEO_ID, playlistId: PLAYLIST_ID, startSeconds: 17 },
    );
  }
});

test("playlist-only sources remain playable", () => {
  const parsed = parseYouTubeInput(`https://www.youtube.com/playlist?list=${PLAYLIST_ID}`);
  assert.equal(parsed.videoId, null);
  assert.equal(parsed.playlistId, PLAYLIST_ID);
  assert.equal(new URL(parsed.canonicalUrl).searchParams.get("list"), PLAYLIST_ID);
});

test("parser rejects non-YouTube hosts and malformed IDs", () => {
  assert.throws(() => parseYouTubeInput(`https://example.com/watch?v=${VIDEO_ID}`), /Only official YouTube/);
  assert.throws(() => parseYouTubeInput("short"), /not a recognized YouTube/);
  assert.throws(() => parseYouTubeInput("https://www.youtube.com/watch?v=too-short"), /does not contain/);
});

test("embed URLs use official paths, playlist identity, timestamps, and rot.local", () => {
  const url = new URL(buildEmbedUrl({
    videoId: VIDEO_ID,
    playlistId: PLAYLIST_ID,
    startSeconds: 90,
  }));
  assert.equal(url.origin, "https://www.youtube.com");
  assert.equal(url.pathname, `/embed/${VIDEO_ID}`);
  assert.equal(url.searchParams.get("enablejsapi"), "1");
  assert.equal(url.searchParams.get("listType"), "playlist");
  assert.equal(url.searchParams.get("list"), PLAYLIST_ID);
  assert.equal(url.searchParams.get("start"), "90");
  assert.equal(url.searchParams.get("origin"), "https://rot.local");

  const playlistUrl = new URL(buildEmbedUrl({ playlistId: PLAYLIST_ID }));
  assert.equal(playlistUrl.pathname, "/embed/videoseries");
});
