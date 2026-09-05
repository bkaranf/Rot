export const NATIVE_HOST_NAME = "com.rot.send_to_rot";

const ALLOWED_HOSTS = new Set([
  "youtube.com",
  "www.youtube.com",
  "m.youtube.com",
  "music.youtube.com",
  "youtu.be",
]);

export function validateYouTubeUrl(value) {
  if (typeof value !== "string" || value.length === 0) {
    return null;
  }

  let url;
  try {
    url = new URL(value);
  } catch {
    return null;
  }

  if (
    url.protocol !== "https:" ||
    url.port !== "" ||
    url.username ||
    url.password ||
    !ALLOWED_HOSTS.has(url.hostname.toLowerCase()) ||
    !isVideoPath(url)
  ) {
    return null;
  }

  return url.href;
}

export function createSendToRotRequest(value) {
  const url = validateYouTubeUrl(value);
  return url === null ? null : { action: "send-to-rot", url };
}

export async function sendCurrentTabToRot(chromeApi, tab = null) {
  const selectedTab =
    tab ??
    (await chromeApi.tabs.query({
      active: true,
      lastFocusedWindow: true,
    }))[0];
  const request = createSendToRotRequest(selectedTab?.url);
  if (request === null) {
    const response = { ok: false, message: "Open a YouTube video before sending it to Rot." };
    setActionStatus(chromeApi, selectedTab?.id, response.message, false);
    return response;
  }

  try {
    const response = await sendNativeMessage(chromeApi, request);
    if (!response || response.ok !== true) {
      const failure = {
        ok: false,
        message: response?.message || "Rot did not accept the selected video.",
      };
      setActionStatus(chromeApi, selectedTab?.id, failure.message, false);
      return failure;
    }

    const success = {
      ok: true,
      message: response.message || "Video sent to Rot.",
    };
    setActionStatus(chromeApi, selectedTab?.id, success.message, true);
    return success;
  } catch (error) {
    const failure = {
      ok: false,
      message: error instanceof Error ? error.message : "Rot could not be reached.",
    };
    setActionStatus(chromeApi, selectedTab?.id, failure.message, false);
    return failure;
  }
}

function sendNativeMessage(chromeApi, request) {
  return new Promise((resolve, reject) => {
    chromeApi.runtime.sendNativeMessage(NATIVE_HOST_NAME, request, (response) => {
      const nativeError = chromeApi.runtime.lastError;
      if (nativeError) {
        reject(new Error(nativeError.message || "Rot could not be reached."));
        return;
      }

      resolve(response);
    });
  });
}

function setActionStatus(chromeApi, tabId, message, ok) {
  const details = { title: "Rot: " + message };
  if (Number.isInteger(tabId)) {
    details.tabId = tabId;
  }
  chromeApi.action.setTitle(details);
  const badge = ok ? "Sent" : "!";
  chromeApi.action.setBadgeText({ text: badge, ...(Number.isInteger(tabId) ? { tabId } : {}) });
  if (badge === "Sent") {
    chromeApi.action.setBadgeBackgroundColor({ color: "#188038", ...(Number.isInteger(tabId) ? { tabId } : {}) });
  } else if (badge === "!") {
    chromeApi.action.setBadgeBackgroundColor({ color: "#b3261e", ...(Number.isInteger(tabId) ? { tabId } : {}) });
  }
}

function isVideoPath(url) {
  if (url.hostname.toLowerCase() === "youtu.be") {
    return url.pathname.split("/").filter(Boolean).length > 0;
  }

  const segments = url.pathname.split("/").filter(Boolean);
  return (
    segments.length > 0 &&
    ["watch", "shorts", "live", "embed", "v", "playlist"].includes(segments[0].toLowerCase())
  );
}
