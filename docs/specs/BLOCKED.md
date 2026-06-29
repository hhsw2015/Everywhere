# BLOCKED — per-cap last error + push history + suggested next move

Auto-appended by the `/goal` loop. Status mirrors `parity-matrix.json`
`status=blocked` rows. Reviewer reads this when deciding whether to flip a
row in handoff.

---

## `agent_browser_read` (bench-variance-too-high)

- **First blocked**: 2026-06-29
- **Reason**: `(max-min)/median = 0.21 > 0.20` on both 5-run sets. ab
  v0.31.1 alternates between the cheap `read` tool path and the heavier
  `open → snapshot → get_text` path; token counts diverge by ~21%
  between runs even with `temperature 0` and identical system prompts —
  Claude's tool-pick non-determinism dominates.
- **Suggested next move**: tighten the fixture task body to demand a
  specific tool path ("use `agent_browser_read` to fetch the URL; do
  not snapshot"); biases ab into one code path. Or switch to
  `kind: har_replay` so request count is fixed regardless of which
  path the agent picks.

---

## (Resolved) — DANGEROUS_TOOLS implementation

Previously blocked (24 rows) on SPEC §2.4 #4 / §6 step 7. **User
approved** the implementation work via /goal session ("DANGEROUS
也做"). Implementation pushed in opendia `experiment/replace-ab`
sha `1c22c13`. Rows are at `status=in-progress`, NOT `have` — SPEC
§6 step 7 still requires human merge to main:

- `cookies_set`, `cookies_set_curl`, `storage_set`, `storage_clear`
- `set_headers`, `set_credentials`, `network_request`, `network_route`,
  `network_unroute`
- `auth_save`, `auth_login`, `auth_show`, `auth_list`, `auth_delete`
- `state_save`, `state_load`, `state_show`, `state_list`,
  `state_clear`, `state_clean`, `state_rename`
- `upload`, `eval`

**Pre-merge audit checklist**:
- per-domain allowlist for cookies_set / set_headers / set_credentials?
- on-disk encryption for `auth_*` payloads in chrome.storage.local?
- consent UI before `state_load` when the saved bundle's URL ≠ active URL?
- review `network_route` / `network_unroute` interceptor surface for
  request rewriting risks?
- `eval` allowlist / sandbox? It executes arbitrary JS in MAIN world.

---

## (Resolved) — Everywhere C# clipboard + batch

Previously blocked (5 rows) needing new C# abstractions. Resolved
in Everywhere `experiment/replace-ab`:

- `IClipboardWriter` + `NullClipboardWriter` in
  `src/Everywhere.Mcp/Input/IClipboardReader.cs`.
- `MacClipboardWriter` (NSPasteboard) in
  `src/Everywhere.Mac/Mcp/MacClipboardWriter.cs`.
- `ClipboardTools.cs` — `clipboard_read/write/copy/paste`.
- `BatchTool.cs` — `batch`, dispatching `browser_*` via OpenDiaBridge;
  `everywhere.*` arm carries a "Phase 2" note until the local-tool
  reflective dispatcher lands.
