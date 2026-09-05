import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, isAbsolute, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(fileURLToPath(new URL("..", import.meta.url)));
const webRoot = resolve(repositoryRoot, "src/Rot.App/Web");
const assetRoot = resolve(repositoryRoot, "assets");
const requestedPort = process.env.ROT_PREVIEW_PORT ??
  process.argv.find((argument) => argument.startsWith("--port="))?.slice(7) ??
  "4174";
const port = Number(requestedPort);
const contentTypes = new Map([
  [".html", "text/html; charset=utf-8"],
  [".js", "text/javascript; charset=utf-8"],
  [".css", "text/css; charset=utf-8"],
  [".json", "application/json; charset=utf-8"],
  [".md", "text/markdown; charset=utf-8"],
  [".png", "image/png"],
  [".jpg", "image/jpeg"],
  [".jpeg", "image/jpeg"],
  [".gif", "image/gif"],
  [".svg", "image/svg+xml"],
  [".webp", "image/webp"],
  [".ico", "image/x-icon"],
]);

function safePath(root, pathname) {
  let decoded;
  try {
    decoded = decodeURIComponent(pathname);
  } catch {
    return null;
  }
  if (decoded.includes("\0")) return null;
  const target = resolve(root, `.${decoded}`);
  const remainder = relative(root, target);
  return remainder && !isAbsolute(remainder) &&
    remainder !== ".." && !remainder.startsWith(`..${sep}`)
    ? target
    : null;
}

function respond(response, status, body, headers = {}) {
  response.writeHead(status, {
    "Cache-Control": "no-store",
    "Content-Type": "text/plain; charset=utf-8",
    ...headers,
  });
  if (response.req.method === "HEAD") response.end();
  else response.end(body);
}

function fixtureSource(fixture) {
  return `<script type="module" src="/__fixtures__/browser-host.js?fixture=${encodeURIComponent(fixture)}"></script>`;
}

async function handle(request, response) {
  if (request.method !== "GET" && request.method !== "HEAD") {
    respond(response, 405, "Method Not Allowed\n", { Allow: "GET, HEAD" });
    return;
  }

  let url;
  try {
    url = new URL(request.url ?? "/", "http://127.0.0.1");
  } catch {
    respond(response, 400, "Bad Request\n");
    return;
  }

  const fixture = url.searchParams.get("fixture");
  if (fixture !== null && !/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(fixture)) {
    respond(response, 400, "Invalid fixture label\n");
    return;
  }

  let root = webRoot;
  let pathname = url.pathname || "/settings/index.html";
  if (pathname === "/") pathname = "/settings/index.html";
  if (pathname === "/__fixtures__/browser-host.js") {
    root = resolve(repositoryRoot, "tests/fixtures");
    pathname = "/browser-host.js";
  } else if (pathname.startsWith("/assets/")) {
    root = assetRoot;
    pathname = pathname.slice("/assets".length);
  }

  const target = safePath(root, pathname);
  if (!target) {
    respond(response, 403, "Forbidden\n");
    return;
  }

  try {
    let body = await readFile(target);
    if (fixture && root === webRoot && target.endsWith(".html")) {
      const html = body.toString("utf8");
      const moduleScript = /<script\b[^>]*\btype=["']module["'][^>]*><\/script>/i;
      if (!moduleScript.test(html)) {
        respond(response, 500, "Preview page has no module entrypoint\n");
        return;
      }
      body = Buffer.from(html.replace(moduleScript, `${fixtureSource(fixture)}$&`));
    }
    respond(response, 200, body, {
      "Content-Type": contentTypes.get(extname(target).toLowerCase()) ?? "application/octet-stream",
    });
  } catch (error) {
    if (error?.code === "ENOENT") respond(response, 404, "Not Found\n");
    else {
      console.error(`[preview-ui] ${error?.message ?? error}`);
      respond(response, 500, "Preview server error\n");
    }
  }
}

if (!Number.isInteger(port) || port < 1 || port > 65535) {
  throw new Error(`Invalid preview port: ${requestedPort}`);
}

const server = createServer((request, response) => {
  void handle(request, response);
});
server.listen(port, "127.0.0.1", () => {
  console.log(`Rot UI preview: http://127.0.0.1:${port}/settings/index.html`);
});
