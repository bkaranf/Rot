import { createWriteStream, mkdirSync } from "node:fs";
import { dirname, resolve } from "node:path";

const endpoint = process.env.ROT_STATS_WS || "ws://127.0.0.1:49124";
const startedAt = Date.now();
const durationMs = Number.parseInt(process.env.ROT_PROBE_MS || "900000", 10);
const verbose = process.env.ROT_PROBE_VERBOSE === "1";
const outputPath = resolve(
  process.env.ROT_PROBE_FILE || "validation/phase0-stats-api-raw.log",
);

mkdirSync(dirname(outputPath), { recursive: true });
const output = createWriteStream(outputPath, { flags: "a", encoding: "utf8" });

let stopped = false;
let retryMs = 500;

function stamp() {
  return new Date().toISOString();
}

function log(kind, details = "") {
  process.stdout.write(`${stamp()} ${kind}${details ? ` ${details}` : ""}\n`);
}

function guidFrom(data) {
  let parsed = data;
  if (typeof parsed === "string") {
    try {
      parsed = JSON.parse(parsed);
    } catch {
      return "";
    }
  }
  if (!parsed || typeof parsed !== "object") return "";
  const value = parsed.MatchGuid ?? parsed.MatchGUID ?? parsed.matchGuid ?? "";
  return typeof value === "string" ? value.trim() : "";
}

async function messageText(value) {
  if (typeof value === "string") return value;
  if (value instanceof ArrayBuffer) return new TextDecoder().decode(value);
  if (ArrayBuffer.isView(value)) {
    return new TextDecoder().decode(value);
  }
  if (value && typeof value.text === "function") return value.text();
  return String(value ?? "");
}

function connect() {
  if (stopped) return;

  let socket;
  try {
    socket = new WebSocket(endpoint);
  } catch (error) {
    log("connect-error", error instanceof Error ? error.message : String(error));
    scheduleReconnect();
    return;
  }

  socket.addEventListener("open", () => {
    retryMs = 500;
    log("connected", endpoint);
  });

  socket.addEventListener("message", async (message) => {
    try {
      const text = await messageText(message.data);
      output.write(`${stamp()} ${text}\n`);
      const payload = JSON.parse(text);
      const eventName = typeof payload?.Event === "string" ? payload.Event : "<missing-event>";
      const matchGuid = guidFrom(payload?.Data);
      if (verbose || eventName !== "UpdateState") {
        log("event", `${eventName} guid=${matchGuid || "<empty>"}`);
      }
    } catch (error) {
      log("invalid-message", error instanceof Error ? error.message : String(error));
    }
  });

  socket.addEventListener("error", () => {
    log("socket-error");
  });

  socket.addEventListener("close", (event) => {
    log("closed", `code=${event.code}`);
    scheduleReconnect();
  });
}

function scheduleReconnect() {
  if (stopped) return;
  const delay = retryMs;
  retryMs = Math.min(retryMs * 2, 5000);
  setTimeout(connect, delay);
}

function stop() {
  if (stopped) return;
  stopped = true;
  log("finished");
  output.end(() => process.exit(0));
}

process.on("SIGINT", stop);
process.on("SIGTERM", stop);
setTimeout(stop, Math.max(1000, durationMs));
log("starting", `endpoint=${endpoint} durationMs=${durationMs} output=${outputPath}`);
connect();
