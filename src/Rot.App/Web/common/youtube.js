import { APP_ORIGIN } from "./constants.js";

export const VIDEO_ID_PATTERN = /^[A-Za-z0-9_-]{11}$/;
export const PLAYLIST_ID_PATTERN = /^[A-Za-z0-9_-]{10,64}$/;

const YOUTUBE_HOSTS = new Set([
  "youtube.com",
  "www.youtube.com",
  "m.youtube.com",
  "music.youtube.com",
  "youtube-nocookie.com",
  "www.youtube-nocookie.com",
]);

function positiveInteger(value) {
  const parsed = Number.parseInt(String(value), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
}

export function parseTimestamp(value) {
  if (value == null) return 0;
  const raw = String(value).trim().toLowerCase();
  if (!raw) return 0;
  if (/^\d+$/.test(raw)) return positiveInteger(raw);

  if (/^\d+(?::\d+){1,2}$/.test(raw)) {
    const parts = raw.split(":").map((part) => Number.parseInt(part, 10));
    return parts.some((part) => !Number.isFinite(part))
      ? 0
      : parts.reduce((seconds, part) => seconds * 60 + part, 0);
  }

  const match = raw.match(/^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$/);
  if (!match || !match.slice(1).some(Boolean)) return 0;
  return positiveInteger(match[1] || 0) * 3600 +
    positiveInteger(match[2] || 0) * 60 +
    positiveInteger(match[3] || 0);
}

function normalizeUrlInput(input) {
  const trimmed = String(input || "").trim();
  if (/^(?:www\.|m\.|music\.)?youtube\.com\//i.test(trimmed) ||
      /^(?:www\.)?youtu\.be\//i.test(trimmed) ||
      /^(?:www\.)?youtube-nocookie\.com\//i.test(trimmed)) {
    return `https://${trimmed}`;
  }
  return trimmed;
}

function cleanVideoId(value) {
  const candidate = String(value || "").trim();
  return VIDEO_ID_PATTERN.test(candidate) ? candidate : null;
}

function cleanPlaylistId(value) {
  const candidate = String(value || "").trim();
  return PLAYLIST_ID_PATTERN.test(candidate) ? candidate : null;
}

export function parseYouTubeInput(input) {
  const raw = String(input || "").trim();
  if (!raw) throw new TypeError("Enter a YouTube link or video ID.");

  const bareVideoId = cleanVideoId(raw);
  if (bareVideoId) {
    return {
      videoId: bareVideoId,
      playlistId: null,
      startSeconds: 0,
      canonicalUrl: `https://www.youtube.com/watch?v=${bareVideoId}`,
    };
  }

  let url;
  try {
    url = new URL(normalizeUrlInput(raw));
  } catch {
    throw new TypeError("That is not a recognized YouTube link or 11-character video ID.");
  }

  const host = url.hostname.toLowerCase();
  const isShortHost = host === "youtu.be" || host === "www.youtu.be";
  if (!isShortHost && !YOUTUBE_HOSTS.has(host)) {
    throw new TypeError("Only official YouTube links are supported.");
  }

  const segments = url.pathname.split("/").filter(Boolean);
  let videoId = null;
  if (isShortHost) videoId = cleanVideoId(segments[0]);
  else if (url.pathname === "/watch") videoId = cleanVideoId(url.searchParams.get("v"));
  else if (["embed", "shorts", "live", "v"].includes(segments[0])) {
    videoId = cleanVideoId(segments[1]);
  }

  const playlistId = cleanPlaylistId(url.searchParams.get("list"));
  const fragmentParams = new URLSearchParams(url.hash.replace(/^#/, ""));
  const startSeconds = parseTimestamp(
    url.searchParams.get("start") ||
    url.searchParams.get("t") ||
    fragmentParams.get("start") ||
    fragmentParams.get("t"),
  );

  if (!videoId && !playlistId) {
    throw new TypeError("The YouTube link does not contain a valid video or playlist ID.");
  }

  const canonical = new URL("https://www.youtube.com/watch");
  if (videoId) canonical.searchParams.set("v", videoId);
  if (playlistId) canonical.searchParams.set("list", playlistId);
  if (startSeconds) canonical.searchParams.set("t", String(startSeconds));
  return { videoId, playlistId, startSeconds, canonicalUrl: canonical.toString() };
}

export function buildEmbedUrl(media, {
  host = "www.youtube.com",
  jsApi = true,
  controls = true,
  autoplay = true,
} = {}) {
  const videoId = cleanVideoId(media?.videoId);
  const playlistId = cleanPlaylistId(media?.playlistId);
  if (!videoId && !playlistId) {
    throw new TypeError("A valid video or playlist ID is required to build an embed URL.");
  }
  if (!["www.youtube.com", "www.youtube-nocookie.com"].includes(host)) {
    throw new TypeError("Embeds must use an official YouTube embed host.");
  }

  const url = new URL(`https://${host}${videoId ? `/embed/${videoId}` : "/embed/videoseries"}`);
  if (jsApi) {
    url.searchParams.set("enablejsapi", "1");
    url.searchParams.set("origin", APP_ORIGIN);
  }
  url.searchParams.set("autoplay", autoplay ? "1" : "0");
  url.searchParams.set("controls", controls ? "1" : "0");
  url.searchParams.set("rel", "0");
  url.searchParams.set("modestbranding", "1");
  url.searchParams.set("playsinline", "1");
  url.searchParams.set("iv_load_policy", "3");
  if (playlistId) {
    url.searchParams.set("listType", "playlist");
    url.searchParams.set("list", playlistId);
  }
  const startSeconds = positiveInteger(media?.startSeconds ?? media?.seconds ?? 0);
  if (startSeconds) url.searchParams.set("start", String(startSeconds));
  return url.toString();
}

export function thumbnailForVideo(videoId) {
  const clean = cleanVideoId(videoId);
  return clean ? `https://i.ytimg.com/vi/${clean}/mqdefault.jpg` : "";
}
