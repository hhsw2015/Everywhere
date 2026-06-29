#!/usr/bin/env node
// Static replay server for bench fixtures. spec §11.1, §11.2.
// static_html: serve `page/` over file://-equivalent localhost.
// har_replay : (TODO Phase 0.5) replay HAR through a simple route table.
import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import { existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, arr) => {
    if (a.startsWith("--")) acc.push([a.slice(2), arr[i + 1]]);
    return acc;
  }, []),
);
const fixture = args.fixture;
const port = parseInt(args.port || "7977", 10);
if (!fixture) {
  console.error("usage: replay-server.mjs --fixture <id> [--port 7977]");
  process.exit(2);
}

const root = resolve(
  fileURLToPath(import.meta.url),
  "../../fixtures",
  fixture,
  "page",
);
if (!existsSync(root)) {
  console.error(`page/ missing for fixture ${fixture}: ${root}`);
  process.exit(3);
}

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".js":   "application/javascript; charset=utf-8",
  ".css":  "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".png":  "image/png",
  ".jpg":  "image/jpeg",
  ".jpeg": "image/jpeg",
  ".svg":  "image/svg+xml",
};

const srv = createServer(async (req, res) => {
  try {
    let p = decodeURIComponent(req.url.split("?")[0]);
    if (p === "/") p = "/index.html";
    const file = join(root, p);
    if (!file.startsWith(root)) {
      res.writeHead(403).end("forbidden");
      return;
    }
    const s = await stat(file).catch(() => null);
    if (!s || !s.isFile()) {
      res.writeHead(404).end("not found");
      return;
    }
    const ext = file.slice(file.lastIndexOf("."));
    res.writeHead(200, { "content-type": MIME[ext] || "application/octet-stream" });
    res.end(await readFile(file));
  } catch (e) {
    res.writeHead(500).end(String(e));
  }
});

srv.listen(port, "127.0.0.1", () => {
  console.error(`replay-server: fixture=${fixture} root=${root} port=${port}`);
});
