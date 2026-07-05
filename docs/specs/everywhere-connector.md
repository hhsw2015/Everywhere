# Everywhere Connector Integration Spec (v1 — draft)

Vendors [oomol-lab/open-connector](https://github.com/oomol-lab/open-connector) into
the Everywhere daemon so Cebian and MCP clients gain access to its 840-provider
SaaS action catalog (~8,300 actions) without running the upstream Node.js
gateway. Adopts the same architecture as `Everywhere.Mcp/OpenCli` — a
dedicated ClearScript V8 isolate, a `HostShim` bridge, a `Runtime` +
`Tools` pair — to keep bug surface and maintenance cost minimal.

Upstream is Apache-2.0. The Everywhere additions in this spec are the same
license as the rest of `Everywhere.Mcp`.

Related specs:
- `docs/specs/everywhere-opencli-adapters.md` — the pattern this spec mirrors.
  Read that first if you have not already; every ambiguity in this document
  should be resolved by asking "what did OpenCLI do?" and matching it.
- `docs/specs/opendia-cebian-merge.md` — the extension side that will consume
  the OAuth callback bridge (Phase 3, not Phase 1).

---

## 1. Goal (Phase 1 = POC scope)

Prove that a single upstream provider — **GitHub** — runs end-to-end inside
the new connector V8 isolate and returns real data:

```
mcp: connector_run(service=github, action=get_current_user, arguments_json="{}")
→ { ok: true, data: { login: "wowdd1", id: ..., ... } }
```

Non-goals for Phase 1:
- Persistent credential store (env var is enough)
- OAuth flow (PAT is enough)
- Web Console (Phase 3)
- More than one provider (Phase 2)
- Cebian sidebar surface (Phase 4)

Everything downstream in §11 is optional and gated behind Phase-1 success.

---

## 2. Why not the upstream Node gateway

Upstream ships a hono + `@hono/node-server` gateway on `:3000` plus a SQLite
credential store. Adopting it would mean:

1. Ship a Node.js runtime alongside Everywhere (already refused earlier in
   the OBC PoC).
2. Two credential stores (Cebian's `browser.storage.local` + connector's
   SQLite) that can drift.
3. A second static-web build, a second REST surface, a second process to
   supervise.

Rejected in favour of embedding upstream **provider definitions and executors
only** and treating the gateway as reference material, not a runtime
dependency.

The one piece of upstream we do keep verbatim is the pre-built Web Console
(§9 "Web UI static hosting") — Phase 3 only, and only as static files served
by Kestrel.

---

## 3. Architecture (parallel to OpenCLI, not merged)

```
┌─────────────────────────────────────────────────────────────┐
│ Everywhere.Mcp                                              │
│                                                             │
│  ┌──────────────────┐         ┌──────────────────────┐      │
│  │ OpenCliRuntime   │         │ ConnectorRuntime     │      │
│  │ (existing)       │         │ (new, this spec)     │      │
│  │                  │         │                      │      │
│  │ V8ScriptEngine ◄─┼─────────┼─► V8ScriptEngine     │      │
│  │ HostShim         │  reuse  │  (SEPARATE isolate,  │      │
│  │ HttpClient       │  the    │   see §3.1)          │      │
│  │ 1257 adapters    │  code,  │  ConnectorHostShim   │      │
│  │                  │  not    │  1 provider (Phase 1)│      │
│  └──────────────────┘  the    └──────────────────────┘      │
│                        instance                             │
│                                                             │
│  ┌──────────────────┐         ┌──────────────────────┐      │
│  │ OpenCliTools     │         │ ConnectorTools       │      │
│  │ opencli_list     │         │ connector_list       │      │
│  │ opencli_describe │         │ connector_describe   │      │
│  │ opencli_run      │         │ connector_run        │      │
│  └──────────────────┘         └──────────────────────┘      │
└─────────────────────────────────────────────────────────────┘
```

### 3.1 Isolation decision — separate V8 engine, not shared

Each subsystem gets its own `V8ScriptEngine`. Reasons:

- **Global namespace collisions.** OpenCLI installs `globalThis.__opencliHost`
  and imports named `@jackwener/opencli/*`. Connector installs
  `globalThis.__connectorHost` and its own module map. Sharing an engine
  means one bad adapter can shadow the other's globals.
- **Bundle size hygiene.** Loading 5,600 lines of open-connector core into
  the OpenCLI engine slows OpenCLI cold start for zero benefit — most
  sessions hit only one of the two.
- **Fault isolation.** A syntax error in a connector provider must not
  poison OpenCLI's registry.
- **Cost is trivial.** ClearScript reports ~10 MB per idle V8 isolate;
  we already accept that overhead for OpenCLI. Adding a second is fine.

Trade-off: two `HttpClient` instances (fine — .NET pools sockets globally),
two shim source strings, two boot lifecycles. These are small.

---

## 4. Directory layout

```
Everywhere/
├── src/
│   ├── Everywhere.Mcp/
│   │   ├── OpenCli/                     (existing, untouched)
│   │   ├── Connector/                   (NEW)
│   │   │   ├── ConnectorRuntime.cs
│   │   │   ├── ConnectorHostShim.cs
│   │   │   ├── ConnectorProviderDef.cs  (mirrors AdapterDef)
│   │   │   ├── CredentialResolver.cs
│   │   │   └── Executors/               (empty in Phase 1)
│   │   └── Tools/
│   │       ├── OpenCliTools.cs          (existing)
│   │       └── ConnectorTools.cs        (NEW)
│   └── Build.ConnectorBundle.targets   (NEW, mirrors OpenCliBundle)
│
└── 3rd/
    └── open-connector/                  (NEW, git subtree or vendor script)
        ├── UPSTREAM_SHA
        ├── UPSTREAM_REF
        ├── src/
        │   ├── core/                    (types.ts, cast.ts, ...)
        │   └── providers/
        │       ├── provider-runtime.ts
        │       └── github/              (Phase 1 provider)
        └── dist/                        (build output — see §5)
            ├── connector.bundle.js
            └── connector-manifest.json
```

Why a `3rd/open-connector/` sibling to `3rd/opencli/`: identical vendor
convention, identical MSBuild-copy pattern, identical upstream-tracking
files. **If a reader has understood `3rd/opencli/`, they already understand
this.**

---

## 5. Bundle build pipeline (KEY DESIGN DECISION)

Upstream is TypeScript **without** a build step (`npm run build` only
typechecks; there is no `dist/`). We cannot run raw `.ts` inside V8. Three
options were considered:

| Option | Verdict |
|---|---|
| **A.** Ship raw `.ts`, transpile at daemon startup with esbuild-wasm | Rejected — 500 ms startup penalty, extra native binary in publish output |
| **B.** Rewrite each provider in JS-only or C# | Rejected — kills upstream reuse, contradicts primary design goal |
| **C.** Pre-build to a single IIFE bundle at MSBuild time via `esbuild` from Node dev-dep | **Adopted** |

Chosen build (Option C):

```
src/scripts/build-connector-bundle.mjs   (NEW, ~40 lines)
```

Runs during `dotnet build` via `Build.ConnectorBundle.targets` **before**
the C# compile step. Discovery mirrors upstream's own
`scripts/provider-source.ts`: scan `3rd/open-connector/src/providers/*/`
directories, treat each as a provider whose entry is
`{dir}/definition.ts` + `{dir}/executors.ts`. There is **no** `index.ts` —
upstream doesn't ship one. In Phase 1 the discovery script is manually
seeded with a `PROVIDERS = ["github"]` allowlist to keep the bundle small.

Emits:
- `3rd/open-connector/dist/connector.bundle.js` — one IIFE that publishes
  `globalThis.__connectorProviders = { github: { definition, executors, credentialValidators }, ... }`
- `3rd/open-connector/dist/connector-manifest.json` — flat list of
  `{service, displayName, actions: [{name, description, requiredScopes}]}`
  used by `connector_list` before V8 ever boots (mirrors OpenCLI's
  `cli-manifest.json`).

esbuild config essentials:
- `format: "iife"`, `platform: "browser"`, `target: "es2022"` (ClearScript
  V8 tracks recent V8, so this is safe).
- `bundle: true` to inline every relative `.ts` import.
- `--define:process.env.NODE_ENV="production"` — never referenced today,
  cheap insurance if upstream adds one.
- **`Buffer` shim.** `core/cast.ts` imports `Buffer` from `node:buffer`
  and uses only `Buffer.from(str, "base64")`. The bundle script provides
  a 5-line replacement module and configures esbuild's `alias` to
  redirect `node:buffer` → the shim. Shim is `export const Buffer = {
  from(input, encoding) { if (encoding !== "base64") throw new Error(...);
  return Uint8Array.from(atob(input), c => c.charCodeAt(0)); } }`.
  This is the ONLY node-import in the Phase-1 dependency tree; verified
  by `grep -R "node:" 3rd/open-connector/src/{core,providers/github,providers/provider-runtime.ts}`.

esbuild is invoked as an npm-installed dev dep, not a global — same pattern
as OpenCLI's own tooling. If Node is missing at build time, the target
fails cleanly with an actionable message; runtime does not need Node at all.

**Upstream tracking:** `scripts/vendor-open-connector.mjs` clones/rsyncs
the upstream repo at a pinned SHA into `3rd/open-connector/src/`, records
the SHA in `UPSTREAM_SHA`, and never modifies the vendored `.ts`.
Provider-level bug fixes go **upstream first**, then the SHA is bumped.

---

## 6. V8 host contract

The connector isolate sees a smaller surface than OpenCLI because upstream
providers are pure-fetch API clients, not browser-driven scrapers.

`globalThis.__connectorHost` methods:

| Method | Semantics |
|---|---|
| `fetchAsync(url, initJson)` | Bridges to `HttpClient`. Reuses OpenCLI's `HostShim.fetchAsync` verbatim. It returns a C# object with `.ok`, `.status`, `.statusText`, `.headers` (dict), `.text` (fully-buffered string), `.setCookies`. See `Response wrapper` below. |
| `getCredential(service)` | Returns `{authType, ...}` JSON matching upstream's `ResolvedCredential`. Phase 1 reads env var; Phase 2 reads SQLite. |
| `warn(msg)` | Log passthrough. |
| `abortSignal()` | Optional — Phase 1 leaves `undefined`. |

Phase-1 shim already needed (verified by static scan):
- **`Buffer` alias** — `core/cast.ts` uses `Buffer.from(str, "base64")` only.
  Handled entirely at bundle time via esbuild alias (see §5), so runtime
  V8 never sees a `node:buffer` import. Not a `__connectorHost` method.
- **`Response` wrapper.** Upstream code uses standard `Response` methods:
  `await response.text()`, `await response.json()`, `response.headers.get(...)`,
  `response.ok`, `response.status`. OpenCLI's `FetchResponse` exposes these
  as **synchronous fields**, not standard-shape methods. The connector
  bundle prepends a small polyfill:
  ```js
  function wrapFetchResponse(raw) {
    return {
      ok: raw.ok, status: raw.status, statusText: raw.statusText,
      headers: { get: (k) => raw.headers[k.toLowerCase()] ?? null,
                 has: (k) => k.toLowerCase() in raw.headers },
      text: async () => raw.text,
      json: async () => JSON.parse(raw.text),
      // body/getReader intentionally absent — Phase 1 providers don't use it.
    };
  }
  globalThis.fetch = async (url, init) =>
    wrapFetchResponse(await __connectorHost.fetchAsync(url, JSON.stringify(init ?? {})));
  ```
  When a Phase 5 provider needs streaming, extend the wrapper. Do not
  monkey-patch upstream — patch the shim.

Phase-1 out-of-scope (add if a provider needs it):
- `cryptoHash` / `cryptoHmac` — required by dozens of upstream providers
  (`bark`, `feishu_custom_bot`, `googleads`, ...). OpenCLI's HostShim
  already has these on the sibling isolate; the same C# helpers can be
  reused with a copy-paste. Not needed for GitHub.
- `transitFiles` — required by file-download / -upload actions. Not
  needed for GitHub read actions in Phase 1's smoke test.

None of these block Phase 1. Reject-with-clear-error when a provider tries
to use one that has not been shimmed yet — same policy as OpenCLI's
`opencli/launcher` stub.

---

## 7. Credential resolution

### Phase 1 (POC): environment variable

`ExecutionContext.getCredential("github")` checks `Environment.GetEnvironmentVariable("EVERYWHERE_CONNECTOR_GITHUB_PAT")`.
If set, returns:
```json
{
  "authType": "api_key",
  "apiKey": "<value>",
  "values": { "apiKey": "<value>" },
  "profile": { "accountId": "env", "displayName": "GitHub PAT (env)", "grantedScopes": [] },
  "metadata": {}
}
```
If unset: returns `undefined`, upstream code raises `ProviderRequestError(401, "Configure github API key credentials first.")`, MCP surface returns `{ok: false, code: "authorization_failed"}`.

### Phase 2: JSON-backed store

Implemented as `Connector/JsonCredentialStore.cs` — a single file at
`~/.everywhere/connector/connections.json` with atomic rename writes and
0600 perms. SQLite was rejected because it added a runtime dependency
without buying anything at this scale (one row per service, one writer).

The document holds three top-level maps:
```json
{
  "connections": { "<service>": { authType, apiKey|accessToken|refreshToken, profile, metadata, createdAt } },
  "oauthClients": { "<service>": { clientId, clientSecret, redirectUri, extra, updatedAt } },
  "oauthPending": { "<state>": { service, codeVerifier, createdAt } }
}
```

Encryption (Phase 6): AES-256-GCM with a per-install keyring at
`~/.everywhere/connector/keyring.bin` (0600, generated on first use).
Every value under any of `apiKey`, `clientSecret`, `accessToken`,
`refreshToken` is wrapped with an `enc:v1:` prefix. Legacy Phase-2
plaintext values decrypt as-is (`CredentialEncryptor.Decrypt` no-ops on
strings without the prefix) and re-encrypt on the next write —
zero-downtime migration.

Threat model: protects against a stolen `connections.json` when the
keyring stays behind. Does *not* protect against attacker with full
home-directory read (they take both files). OS-keychain wrapping is
future work; matches how Everywhere already handles LLM API keys.

### Phase 3: OAuth via daemon loopback callback

Implemented directly by the daemon — the extension is not involved.
Tradeoff: users need one browser tab open on `localhost` during the
consent flow, but Cebian keeps its "MCP client only" posture and the
callback gets loopback-only enforcement for free (already applied by
`EverywhereMcpHttpHost.LoopbackOnly`).

Flow:
1. Client `POST /api/oauth/authorize/:service` — daemon reads the
   provider's OAuth definition from the manifest (§8.5), generates
   `state` + optional PKCE, returns the authorization URL.
2. Client (usually the connector Web Console at `/connector-ui/`) opens
   that URL in a browser tab.
3. Provider redirects to `http://127.0.0.1:PORT/api/oauth/callback?code&state`.
4. Daemon's callback endpoint validates state, POSTs to the token URL
   (`OAuthFlowService.HandleCallbackAsync`), stores the resulting oauth2
   credential, and renders a "connected — you can close this tab"
   HTML response.

Refresh (Phase 6): `ConnectorRuntime.InvokeAsync` calls
`IOAuthRefresher.NeedsRefresh(service)` before every provider action
and refreshes preemptively when `expiresAt < 60s`.

---

## 8. MCP surface

Three tools, envelope identical to `OpenCliTools.Envelope(...)`:

### 8.1 `connector_list`
- No args → list of `{service, displayName, actionCount, categories}`.
- `service=X` → drill into one provider's actions.
- `query=X` → fuzzy match on service/action name/description (cap 60).
- Reads `connector-manifest.json`; **no V8 boot required.**

### 8.2 `connector_describe`
- Args: `service`, `name`.
- Returns full `ActionDefinition` (input/output JSON schema, requiredScopes,
  followUpActions).
- Reads manifest; no V8 boot.

### 8.3 `connector_run`
- Args: `service`, `name`, `arguments_json`.
- Boots V8 lazily on first call.
- Returns upstream's `ExecutionResult` shape flattened into the shared envelope:
  ```json
  // Success:
  {
    "schema_version": "1",
    "ok": true,
    "service": "github",
    "name": "get_current_user",
    "data": { "login": "wowdd1", "id": 12345, ... }
  }

  // Error:
  {
    "schema_version": "1",
    "ok": false,
    "service": "github",
    "name": "get_current_user",
    "code": "authorization_failed",
    "error": "Configure github API key credentials first.",
    "hint": "Set env var EVERYWHERE_CONNECTOR_GITHUB_PAT (Phase 1)."
  }
  ```
- Envelope adaptation rules (adapter code lives in `ConnectorTools.cs`,
  not in vendored TS):
  - Upstream `ok: true` → envelope `ok: true`, `data = upstream.output`.
  - Upstream `ok: false` → envelope `ok: false`,
    `code = upstream.error.code`, `error = upstream.error.message`,
    upstream `error.details` dropped (leaked stack surfaces).
  - Envelope adds a `hint` field only for `authorization_failed` in
    Phase 1, pointing users at the env-var name for that service.
- Values `code` may hold: `authorization_failed | rate_limited |
  invalid_input | provider_error` — passthrough from upstream, plus
  `RUNTIME_HOST_ERROR` when the C# side (V8 boot / bundle load / bridge)
  fails before the executor runs.

### 8.4 LLM usage recipe (baked into tool descriptions)

The MCP `[Description(...)]` attributes must guide the LLM through the
list → describe → run funnel to keep tokens bounded. Suggested phrasing:

- `connector_list`: "List SaaS providers integrated via open-connector.
  No args → provider index (~1KB per provider). service=X → drill into
  one provider's actions. query=X → fuzzy search across all providers
  (cap 60). Pair with connector_describe for schemas, connector_run to
  execute. Prefer this over connector_run when unsure which action fits."
- `connector_describe`: "Full input/output JSON schema + required OAuth
  scopes for one action. Call before connector_run when arguments are
  non-trivial. Args: service (e.g. \"github\"), name (e.g. \"create_issue\")."
- `connector_run`: "Execute one provider action. Requires credentials
  configured (Phase 1: env var). arguments_json is a JSON object matching
  the action's inputSchema — call connector_describe first if unsure."

Copy exact wording during Phase 1 implementation; iterate based on
Cebian dogfooding data.

### 8.5 Distinct from OpenCLI's `opencli_run`
Semantics are close but **not identical**:
- OpenCLI adapters can be `strategy: "cookie"|"intercept"|"ui"` — need a
  browser. Connector actions are always pure API calls; there is no
  browser dependency check.
- OpenCLI's error taxonomy is Everywhere-native (`RUNTIME_NOT_FOUND`,
  `BROWSER_NOT_READY`); connector's is upstream-native. We do **not**
  translate — LLM tool descriptions carry the taxonomy.

---

## 9. Web UI static hosting (Phase 3)

Upstream ships a React web console at `web/` producing static assets on
`vite build`. Everywhere already runs Kestrel for the MCP HTTP transport.

Adding it is a ~20-line change:
```csharp
app.UseStaticFiles(new StaticFileOptions {
    FileProvider = new PhysicalFileProvider(Path.Combine(baseDir, "connector-web")),
    RequestPath  = "/connector-ui",
});
```

Behind that, the console makes 8 REST calls listed in §9.1. Six are trivial
CRUD; two need real work (§9.2).

### 9.1 REST endpoints (Phase 3, mirror upstream verbatim)

| Endpoint | Backing service |
|---|---|
| `GET /api/providers` | `ConnectorRuntime.ListProviders()` |
| `GET /api/providers/:service` | manifest lookup |
| `PUT /api/connections/:service` | writes `connector_connections` |
| `GET /api/connections` | reads `connector_connections` |
| `DELETE /api/connections/:service` | deletes row |
| `POST /v1/actions/:actionId` | `ConnectorRuntime.InvokeAsync` |
| `GET /api/runs` | reads run log |
| `GET /api/oauth/configs` | reads OAuth client config table |

### 9.2 OAuth flow endpoints
`POST /api/oauth/configs` and `POST /api/oauth/authorize` are the hard part
and land with §7 Phase 3.

### 9.3 Cebian entry (external link, not iframe)

Original spec called for a `/connectors` route inside Cebian's sidepanel
that iframes `/connector-ui/`. **Reverted after Phase 4 review** —
Cebian is an MCP client and shouldn't own provider-config UI. The
active shape:

- `OpenDiaBridgeSection` inside Cebian settings has one button, "Open
  Connector Manager", that calls `chrome.tabs.create({url: ".../connector-ui/"})`
  — no route, no iframe, no in-extension state.
- Everything else about connector management lives on the daemon side.

If a future user story needs richer in-sidebar interaction, prefer
adding it as a *native* React page hitting `/api/*` directly, not as an
iframe.

---

## 10. Testing / regression strategy

### 10.1 Provider-level tests
Upstream **has no provider-layer tests** — all 23 upstream test files live
in `core/`, `oauth/`, `web/`. Provider correctness is enforced upstream by
their local console + real API calls, not by tests. This means:

- Vendoring tests is not applicable.
- Regression relies on real API calls against a live account.
- **Every provider we ship must have a smoke test that hits its cheapest
  identity endpoint** — `github.get_current_user`, `openai.list_models`, etc.

Smoke test target: `dotnet test --filter Category=ConnectorSmoke`. Reads
`.env.test` for credentials; skips when unset. CI does not run these
(would leak PATs into logs); developers run them locally after a
provider bump.

### 10.2 Bundle build verification
Every CI run:
```
node src/scripts/build-connector-bundle.mjs
# The IIFE assigns to globalThis; --input-type=module is not required
# because the bundle format is IIFE, not ESM.
node -e "require('./3rd/open-connector/dist/connector.bundle.js'); \
  const p = globalThis.__connectorProviders || {}; \
  const services = Object.keys(p); \
  if (!services.length) throw new Error('bundle produced no providers'); \
  console.log('providers:', services.join(','));"
```
Fails the build if the bundle does not surface the expected provider count.
The step also reads `dist/connector-manifest.json` and asserts its
`services` list matches the runtime output — catches manifest/bundle
drift, which is the single biggest source of "list says X but run fails
with unknown_action" bugs.

### 10.3 Contract snapshot
`connector-manifest.json` is checked into version control. Diff on that
file during upstream bumps flags any accidental action removal or schema
change **before** we ship it to LLMs whose tool descriptions cache it.

---

## 11. Phased rollout

| Phase | Scope | Signal it worked |
|---|---|---|
| **1 — POC** | github only, PAT via env, MCP surface only | `connector_run(github, get_current_user)` returns your login |
| **2 — Multi-provider + persistent creds** | ~10 hand-picked no-auth + api-key providers, SQLite store | Cebian can save a PAT and it survives daemon restart |
| **3 — Web Console + OAuth** | static-host upstream web/, 8 REST endpoints, extension OAuth bridge | Google/Slack/Notion connect end-to-end |
| **4 — Cebian external link + Web Console tab** | Cebian settings adds "Open Connector Manager" opening `/connector-ui/` in a new browser tab — no in-extension routes | User can jump from Cebian to daemon UI in one click |
| **5 — Node-shimmed providers** | node:buffer + node:crypto shims via esbuild alias; TextEncoder/URL/Buffer polyfills in the V8 boot script | Providers using `Buffer.from(base64)` / `createHash("sha256")` execute end-to-end |
| **6 — Encryption + auto refresh** | AES-256-GCM at rest for secret fields; opportunistic OAuth refresh 60s before expiry | Rotated tokens survive daemon restart; disk-only theft yields ciphertext |
| **7 — OAuth curated map + bulk providers** | +5 OAuth definitions (Discord/Dropbox/Figma/Calendly/ClickUp), +30 providers | 61 providers, 754 actions in bundle |
| **8 — Auto-generated OAuth map + transit files + more providers** | Manifest carries every provider's `auth[]`; TransitFileStore + `/v1/files/*` REST | 109 providers, 1239 actions, upload/download works |
| **9 — Full-catalog scan + polyfills** | Auto-scan allowlist; TextEncoder/URL/Buffer polyfills; 3 skips (flomo/jin10/linux_do) | 828 providers, 8141 actions in a single bundle |
| **10 — CI drift check + spec alignment** | Bundle-vs-manifest verifier, manifest checked into VCS, spec text ↔ code reconciled | Upstream bumps show manifest diffs; docs match reality |

Counts derived from `grep -RlE "^\s*(import\|require)\s.*node:" 3rd/open-connector/src/providers/ | cut -d/ -f1 | sort -u | wc -l` at pinned SHA. Refresh on every upstream bump.

Each phase's success gates the next. If Phase 1 finds an unforeseen block,
we redesign this doc, not soldier on.

---

## 12. Failure modes we accept

- **Upstream breaking change.** Bumping `UPSTREAM_SHA` may break bundle
  build; `connector-manifest.json` diff catches it in CI. Recovery: pin
  the previous SHA, file upstream issue.
- **Provider-side rate limits.** Upstream translates 429 → `rate_limited`.
  We surface it as-is; caller retries with backoff. No local rate
  limiting in Phase 1-4.
- **Silent quota exhaustion.** GitHub PAT with insufficient scopes returns
  403 → `authorization_failed`. LLM can read the error and re-prompt user
  for a wider-scope PAT. Same UX as CLI tools.
- **V8 isolate death.** ClearScript engine can crash on adversarial
  responses. `ConnectorRuntime` mirrors `OpenCliRuntime`'s `_engineTask`
  refresh-on-fault pattern (see `OpenCliRuntime.cs:76-86`) so a poisoned
  Task never permanently poisons subsequent calls. On refresh, the
  bundle is re-read from `dist/connector.bundle.js` on disk (not from
  a cached string) so a hot-swap during development picks up the new
  file automatically. In-flight requests hitting the crashed engine
  see `RUNTIME_HOST_ERROR` and can retry once.

---

## 12.5 Upstream bump procedure

Ordered checklist for `git subtree pull` or SHA bump:

1. Update `3rd/open-connector/UPSTREAM_SHA` and `UPSTREAM_REF`.
2. Run `node src/scripts/build-connector-bundle.mjs` locally.
3. Diff `3rd/open-connector/dist/connector-manifest.json` against the
   previous one:
   - **Removed action** → check callers (LLMs cache tool descriptions).
     If it's in Cebian's default toolset, coordinate a Cebian release.
   - **Removed field in an action's outputSchema** → same as above.
   - **New required field in inputSchema** → downstream callers break.
     Bump connector schema_version to 2 in the envelope.
   - **New action** → free win, no action needed.
4. Run `dotnet test --filter Category=ConnectorSmoke` locally with a
   real PAT. The smoke test asserts `github.get_current_user` still
   returns the expected shape.
5. If a new upstream provider or executor introduces a new `node:*`
   import, the bundle build fails with a clear message. Choose:
   (a) add an esbuild alias like the `Buffer` shim, (b) exclude that
   provider from the build's `PROVIDERS` allowlist.
6. Commit the vendored source bump and the manifest diff in the same
   commit so `git blame` on either surfaces the upstream SHA.

Do not skip step 3. That is the entire point of shipping the manifest
into version control.

---

## 13. Non-obvious invariants

1. **Never edit vendored `.ts`.** All Everywhere-side patches live in
   `Connector/*.cs`, `HostShim`, or the bundle build script — never inside
   `3rd/open-connector/`. This is what keeps `git subtree pull upstream`
   trivial.

2. **Manifest is the source of truth for cheap ops.** `connector_list`
   and `connector_describe` MUST NOT boot V8. The bundle build's job is
   to ensure the manifest stays in sync with the bundle content.

3. **One credential per service, no aliases in Phase 1-2.** Upstream
   supports named connections (`github.default`, `github.work`); we
   flatten to one connection per service until Phase 4 to keep the store
   schema simple.

4. **Two separate V8 engines. Never share.** See §3.1.

5. **PAT never leaves the daemon.** Cebian never sees the raw token —
   it sends `{service, values}`, daemon writes to store, daemon injects
   into `getCredential()` for the executor. Cebian only sees the
   `profile` field back.

---

## 14. Open questions

- **Q1. RESOLVED.** Upstream uses static `import` inside executors and
  dynamic `import()` only in `scripts/generate-provider-registry.ts`
  (build-time, not runtime). esbuild `format: "iife" + bundle: true`
  inlines the static graph without touching dynamic imports.
- **Q2.** OpenCLI copies pre-baked `.js`; we introduce a `dotnet build`
  → `node esbuild` dependency. CI runners already have Node (WXT extension
  build needs it) so this is fine. Local `dotnet build` on a machine with
  no Node fails the connector target with an actionable error and leaves
  everything else buildable.
- **Q3. RESOLVED.** OpenCLI's `FetchResponse` already exposes
  `ok/status/statusText/headers/text/setCookies` (see
  `HostShim.cs:1096`). Upstream code expects a web-standard `Response`
  (async `.text()`, `.headers.get(name)`), which we get via a 12-line
  wrapper prepended to the bundle (§6). No OpenCLI-side changes.
  `body.getReader()` is **not** needed for Phase 1 (GitHub only calls
  `response.text()`); `readBoundedResponseBytes` is only used by
  `uploadProviderUrlToTransitFile`, which no Phase-1 action invokes.
- **Q4. RESOLVED (Phase 11).** `definition.ts` is enough. Upstream's
  `web/src/model.ts::credentialFieldsFor` derives every field the console
  renders — `label` / `placeholder` / `description` / `extraFields` for
  api_key; the full `fields[]` for custom_credential — from the
  `AuthDefinition` we already ship in the manifest under `service.auth[]`.
  `credential-fields.ts` is a build-time helper, not a runtime dependency.

---

## 15. What this doc is not

This is not:
- A promise of upstream feature parity. Some providers require Node
  primitives Everywhere doesn't ship (§6, §11 Phase 5).
- A rewrite budget. Every executor's business logic stays upstream.
- A replacement for OpenCLI. OpenCLI does browser cookie/DOM strategies
  the API-first connector cannot; the two coexist in Cebian's tool
  registry as intentional siblings.

---
