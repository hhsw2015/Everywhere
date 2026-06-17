# Everywhere MCP Server — Specification

Status: Draft v1
Audience: implementing agent (`/goal`-style)

## Mission

Combine **Everywhere's general-purpose UI awareness** (a11y trees, selection
state, focus state, text selection, terminal output, picker hotkey, selection
cache, …) with **Computer Use's input-injection** (CGEvent / SendInput /
XTest) into ONE unified MCP interface.

- Every signal Everywhere can perceive about the user's current activity is
  surfaced as a first-class field in tool payloads — not buried in tree_text —
  so agents can act on user intent with one tool call.
- Every action a user can take with mouse + keyboard is drivable via the same
  MCP.
- Strict superset of `iFurySt/open-codex-computer-use`: tool names + JSON
  schemas byte-compatible. No runtime dependency on open-computer-use.

The result: an agent on the other end of MCP gets the same situational
awareness as a human looking over the user's shoulder, plus the ability to
operate the machine on their behalf.

### Awareness surface
list_apps / get_app_state / get_app_context / get_focused_context /
get_selected_text (OS-wide, cached) / get_terminal_output / screenshot /
pick_element + read_pick. Every snapshot payload exposes
**selected_items**, **focused_items**, **focused_path** as first-class
semantic fields derived from Everywhere's a11y abstraction so the agent never
has to grep tree_text for `[Selected]`.

### Action surface
click (element_index OR x/y) / drag / type_text / press_key / scroll /
set_value / perform_secondary_action.

---

## 1. Outcome

A new project `src/Everywhere.Mcp/` that boots an MCP server inside the Everywhere process and exposes:

- **Computer Use tools** (9): byte-compatible names + JSON schemas with upstream `open-computer-use`. Drop-in replacement.
- **Everywhere tools** (≥5): exclusive context / pickup capabilities the upstream project does not have.

Two transports:
- `stdio` — when launched as `everywhere --mcp`.
- `streamable HTTP` (MCP spec) — when Everywhere GUI is running, listens on `http://localhost:7878/mcp`.

Client config (any MCP host):

```json
{
  "mcpServers": {
    "everywhere": { "url": "http://localhost:7878/mcp" }
  }
}
```

After this server is in production, users can drop `open-computer-use` from their config without losing capability.

---

## 2. Non-goals

- No new GUI surface. Server is headless.
- No replacing Everywhere's existing internal MCP **client** layer (`Everywhere.Core/Chat/Plugins/Mcp/*`). That stays untouched.
- No browser automation, no SSH, no remote desktop. Local OS only.
- No permission UX redesign. Reuse whatever a11y/screen-recording prompts Everywhere already uses.

---

## 3. Reference upstream

`iFurySt/open-codex-computer-use` (MIT). Pinned reference commit recorded in `src/Everywhere.Mcp/UPSTREAM_REF.md` (created during impl, see §10).

Files we mirror (logic, not literal):

| Upstream | Our target |
|---|---|
| `packages/OpenComputerUseKit/Sources/OpenComputerUseKit/ToolDefinitions.swift` | `Tools/ToolSchemas.cs` |
| `packages/OpenComputerUseKit/Sources/OpenComputerUseKit/AccessibilitySnapshot.swift` | `Snapshot/AppSnapshot.cs` |
| `packages/OpenComputerUseKit/Sources/OpenComputerUseKit/InputSimulation.swift` | `Input/MacInputSimulator.cs` |
| `packages/OpenComputerUseKit/Sources/OpenComputerUseKit/KeyMapping.swift` | `Input/KeyMapping.cs` |
| `packages/OpenComputerUseKit/Sources/OpenComputerUseKit/ComputerUseToolDispatcher.swift` | `Server/ToolDispatcher.cs` |
| `apps/OpenComputerUseLinux/main.go` | `Input/LinuxInputSimulator.cs` + `Snapshot/LinuxAppSnapshot.cs` |
| `apps/OpenComputerUseLinux/runtime.py` | `Input/LinuxWaylandPortal.cs` |
| `apps/OpenComputerUseWindows/main.go` | `Input/WindowsInputSimulator.cs` |
| `apps/OpenComputerUseWindows/runtime.ps1` | (consult only) |

Mirroring rules — see §10.

---

## 4. Project layout

New csproj. Add to `Everywhere.slnx`, `Everywhere.Mac.slnx`, `Everywhere.Windows.slnx`, `Everywhere.Linux.slnx`.

```
src/Everywhere.Mcp/
  Everywhere.Mcp.csproj
  UPSTREAM_REF.md
  THIRD_PARTY_NOTICES.md          ; MIT attribution to upstream
  EverywhereMcpServiceExtensions.cs   ; AddEverywhereMcp() DI hook

  Server/
    EverywhereMcpServer.cs        ; MCP server boot
    ToolDispatcher.cs             ; routes tool name → handler
    SessionStore.cs               ; per-app element index registry
    StdioTransport.cs
    HttpTransport.cs              ; Kestrel listener at :7878/mcp

  Snapshot/
    AppSnapshot.cs                ; tree + screenshot + element index
    ElementIndexer.cs             ; assigns stable int indices per snapshot
    AppDiscovery.cs               ; list_apps backing
    SnapshotRenderer.cs           ; tree → text (mirrors upstream renderedText)

  Input/
    IInputSimulator.cs
    KeyMapping.cs                 ; xdotool-style names → platform keycodes
    Mac/MacInputSimulator.cs      ; CGEventPost
    Windows/WindowsInputSimulator.cs ; SendInput
    Linux/LinuxInputSimulator.cs  ; XTest (X11)
    Linux/LinuxWaylandPortal.cs   ; xdg-desktop-portal RemoteDesktop (Wayland)
    FocusBorrow.cs                ; bring window to front, do work, restore

  Tools/
    ToolSchemas.cs                ; JSON schema for all tools
    ListAppsTool.cs
    GetAppStateTool.cs
    ClickTool.cs
    DragTool.cs
    TypeTextTool.cs
    PressKeyTool.cs
    ScrollTool.cs
    SetValueTool.cs
    PerformSecondaryActionTool.cs
    GetFocusedContextTool.cs      ; Everywhere-only
    GetSelectedTextTool.cs        ; Everywhere-only
    PickElementTool.cs            ; Everywhere-only
    ExpandElementTool.cs          ; Everywhere-only
    GetTerminalOutputTool.cs      ; Everywhere-only
    ScreenshotTool.cs             ; Everywhere-only

tests/Everywhere.Mcp.Tests/
  Everywhere.Mcp.Tests.csproj
  Snapshot/                        ; mirrors upstream test cases
  Input/                           ; key mapping fixtures
  Tools/                           ; per-tool schema + handler tests
  Fixtures/                        ; same fixture data as upstream where possible
```

---

## 5. Tool catalog

### 5.1 Computer Use tools (byte-compatible with open-computer-use)

All annotations and schemas mirrored verbatim from upstream `ToolDefinitions.swift`. `app` parameter is required on every tool. **Do not change names, parameter names, or accepted values.**

| Tool | Required args | Optional args | Annotations |
|---|---|---|---|
| `list_apps` | — | — | readOnlyHint |
| `get_app_state` | `app` | `show_full_text:bool=false` | readOnlyHint |
| `click` | `app` | `element_index?`, `x?`, `y?`, `click_count?=1`, `mouse_button?="left"` (`left`\|`right`\|`middle`) | — |
| `drag` | `app, from_x, from_y, to_x, to_y` | — | — |
| `type_text` | `app, text` | — | — |
| `press_key` | `app, key` | — | — |
| `scroll` | `app, element_index, direction` (`up`\|`down`\|`left`\|`right`) | `pages?=1.0` | — |
| `set_value` | `app, element_index, value` | — | — |
| `perform_secondary_action` | `app, element_index, action` | — | — |

Schema source: copy from upstream `ToolDefinitions.swift`. Keep wording of `description` fields identical (so any tool description hashes match).

#### Behavioral contracts

`get_app_state`:
- Brings target app into focus if not already (mirrors upstream session activation).
- Captures screenshot + a11y tree.
- Compresses screenshot: `maxDimension=1280, minScale=0.25, maxPNGBytes=900_000` (upstream constants).
- Renders tree text. Truncates per-node text at `500` chars unless `show_full_text=true`.
- Tree caps: `maxNodeCount=1200, maxDepth=64` (upstream constants).
- **Issues a fresh `element_index` map** for this snapshot. Stores in `SessionStore[appKey]`. Old indices for that app become invalid (return `element_index_expired` error).
- Output JSON shape (mirror upstream): `{ window_title, window_bounds, screenshot_png_b64, tree_text, focused_summary?, selected_text? }`.

`click`:
- If `element_index` provided: resolve via session, perform a11y `Press` (or AXUIElement action), no pointer movement.
- If `x,y` provided: `FocusBorrow(app)` → `InputSimulator.MoveTo + MouseDown + MouseUp` × `click_count`.
- Element-path click does **not** require window focus.
- Coordinate-path click **requires** target window foreground; use `FocusBorrow`.

`drag`, `type_text`, `press_key`: always coordinate / keyboard path → `FocusBorrow` mandatory.

`scroll`, `set_value`, `perform_secondary_action`: pure a11y, no `FocusBorrow`.

#### Error model

Mirror upstream string-based errors. Examples:
- `"Failed to create HID event source."`
- `"Element index 42 not found in current snapshot."`
- `"App 'Safari' not running. Call list_apps."`

Return as MCP `tool_call` error (not protocol error). Schema: `{ isError: true, content: [{type:"text", text:"..."}] }`.

### 5.2 Everywhere-only tools

Names use `_` separator to match upstream style. Names must be unique vs §5.1.

| Tool | Required args | Optional args | Notes |
|---|---|---|---|
| `get_focused_context` | — | `budget?:int=4000` | One-shot snapshot of current foreground app. Skips `list_apps → get_app_state` two-step. |
| `get_selected_text` | — | — | Returns currently selected text across the OS via `IVisualElementContext.SelectionData`. Empty string if none. |
| `pick_element` | — | `mode?:string="element"` (`element`\|`window`\|`screen`) | Triggers visual picker UI. User clicks → returns selected element snapshot + new `element_index`. Bridges to `IVisualElementContext.PickVisualElementAsync`. Cancellable; returns `{cancelled:true}` if user dismisses. |
| `expand_element` | `element_index` | `budget?:int=2000` | Re-runs tree builder rooted at given element. Use when prior snapshot reported `omitted_children=true`. |
| `get_terminal_output` | — | `lines_back?:int=200` | Returns recent PTY output of focused terminal app via `Everywhere.Terminal`. Empty if focused app is not a terminal. |
| `screenshot` | — | `element_index?:string` | Element-scoped screenshot if `element_index`, else focused window. Same compression rules as `get_app_state`. |

`get_focused_context` extra output fields (in addition to `get_app_state` shape):
- `omitted_children: bool` — true if budget was hit
- `omitted_node_count: int`
- `tree_json: object` — structured form for agents that want to walk the tree (Everywhere-only; not in upstream)

### 5.3 Tool descriptions for agent guidance

Each Everywhere-only tool's `description` must instruct the agent to **prefer** it for relevant intents:

```
get_focused_context: Get a single rich snapshot of whatever the user is
currently looking at — focused window, selected text, accessibility tree
with priority-based budget pruning, and screenshot. PREFER THIS over
list_apps + get_app_state when the user uses deictic references
("this", "that", "the error", "this code", "这个"). Cheaper and faster
than the two-step flow.
```

This is the only "magic" we add. The upstream-mirrored tools keep their original descriptions.

---

## 6. Element index design

Stable across multiple tool calls within a session, invalidated on next `get_app_state` for the same app.

```
SessionStore = ConcurrentDictionary<string /*appKey*/, AppSession>

class AppSession {
    int LastSnapshotEpoch;
    Dictionary<int, IVisualElement> ElementsByIndex;
    DateTime CapturedAt;
    nint WindowHandle;
}
```

`appKey` = bundle id (mac), exe path (win), `WM_CLASS` (linux). Falls back to lowercase app name.

`element_index` wire format: integer string, e.g. `"42"`. Why integer not GUID: matches upstream wire format → existing test corpus reusable.

Lookup miss: return `error: element_index_expired (call get_app_state again)`.

---

## 7. Focus borrow

```
class FocusBorrow {
    static IDisposable Acquire(WindowHandle target, bool requireFocus)
}
```

`Acquire`:
1. If `requireFocus == false`: returns no-op disposable.
2. Saves `prev = GetForegroundWindow()`.
3. Tries a11y raise (`AXUIElementSetAttributeValue kAXMainAttribute=true` on macOS, `IUIAutomationElement.SetFocus` on Windows). Sleep `120ms`.
4. If still not foreground, falls back to `NSRunningApplication.activate` / `SetForegroundWindow` / `_NET_ACTIVE_WINDOW` request. Sleep `250ms`.
5. On dispose: best-effort restore prev. Failures swallowed (log warn).

Constants `120ms` and `250ms` from upstream (`InputSimulation.swift` line refs in `UPSTREAM_REF.md`).

Concurrency: only one `FocusBorrow` at a time per process. Lock + queue. Calls beyond 5s old timeout error out.

---

## 8. Transport layer

### 8.1 stdio

CLI flag: `everywhere --mcp` (add to existing CLI parser if any; otherwise new arg in `Program.cs` of platform projects).

When this flag set:
- Skip GUI bootstrap.
- Boot `EverywhereMcpServer` over stdio.
- Process exits when stdin closes.

Client config:
```json
{ "mcpServers": { "everywhere": { "command": "everywhere", "args": ["--mcp"] } } }
```

### 8.2 HTTP

When Everywhere GUI starts (normal mode), `AddEverywhereMcp()` registers a Kestrel endpoint at `http://localhost:7878/mcp` using MCP streamable HTTP transport.

Port `7878` configurable via `EVERYWHERE_MCP_PORT` env or `Settings > Mcp Server`. If port busy: try `7879..7888`, then fail (log error, GUI keeps running, server disabled).

CORS: allow only `localhost`/`127.0.0.1`. No auth in v1 (local-only). Add bearer-token gate in §13 if v2.

### 8.3 SDK

Use `ModelContextProtocol` official .NET SDK NuGet package. Wire tools via attribute or fluent builder — pick whatever the SDK version supports cleanest. Don't roll our own MCP framing.

---

## 9. Integration with existing Everywhere

`Everywhere.Mcp.csproj` references:
- `Everywhere.Abstractions`
- `Everywhere.Core` (for `IVisualElementContext`, `VisualContextBuilder`, `Everywhere.Terminal`)

Platform projects (`Everywhere.Mac`, `Everywhere.Windows`, `Everywhere.Linux`) reference `Everywhere.Mcp` and provide platform-specific `IInputSimulator` via DI:

```csharp
// Everywhere.Mac/Startup.cs (or equivalent)
services.AddEverywhereMcp(options => {
    options.HttpPort = 7878;
    options.EnableHttp = true;
});
services.AddSingleton<IInputSimulator, MacInputSimulator>();
```

`AddEverywhereMcp` registers:
- `SessionStore` (singleton)
- All `Tools/*Tool` (singleton, take `IVisualElementContext`, `IInputSimulator`, `SessionStore`)
- `EverywhereMcpServer`
- `HttpTransport` as `IHostedService` (only if `EnableHttp`)

No changes to existing chat plugin / MCP **client** code. They live in different namespaces and don't cross.

---

## 10. Mirroring rules (anti-bug discipline)

Every C# file that ports upstream logic carries a header:

```csharp
// Mirrors: packages/OpenComputerUseKit/Sources/OpenComputerUseKit/InputSimulation.swift
// Upstream: iFurySt/open-codex-computer-use@<sha-pinned-in-UPSTREAM_REF.md>
// Method-level cross-refs below as // mirrors: <file>:<line>
```

Per-method cross-refs:
```csharp
// mirrors: InputSimulation.swift:60-75 (clickGlobally)
public void Click(...) { ... }
```

`UPSTREAM_REF.md` content:

```
Pinned commit: <sha>
Last synced: YYYY-MM-DD

File mapping:
  InputSimulation.swift           → Input/Mac/MacInputSimulator.cs
  KeyMapping.swift                → Input/KeyMapping.cs
  AccessibilitySnapshot.swift     → Snapshot/AppSnapshot.cs (Mac portion)
  ToolDefinitions.swift           → Tools/ToolSchemas.cs
  ComputerUseToolDispatcher.swift → Server/ToolDispatcher.cs
  apps/OpenComputerUseLinux/main.go → Input/Linux/LinuxInputSimulator.cs
  apps/OpenComputerUseWindows/main.go → Input/Windows/WindowsInputSimulator.cs

Constants imported (do not adjust without re-validating):
  accessibilityTreeMaxNodeCount = 1200
  accessibilityTreeMaxDepth = 64
  screenshotResultMaxPNGBytes = 900_000
  screenshotResultMaxDimension = 1280
  screenshotResultMinScale = 0.25
  snapshotTextDefaultCharacterLimit = 500
  windowVisibilityRecoveryDelay = 0.7s
  maxKeyboardUnicodeChunkLength = 64
  focusActivateDelay = 0.25s (after activate)
  focusAxRaiseDelay = 0.12s (after AX raise)

Diff sync workflow:
  git -C <upstream-clone> log --since=<last-sync> -- <mirrored-files>
  Apply matching changes to mirrored C# files. Bump pinned sha. Update Last synced.
```

`THIRD_PARTY_NOTICES.md`:
```
This component incorporates code patterns and constants from
iFurySt/open-codex-computer-use, licensed under MIT. See
UPSTREAM_REF.md for file-level mapping and pinned commit.
```

### Translation discipline

For each file in §3 mapping table:
1. Read upstream source side-by-side.
2. Keep function names the same (CamelCase per C# convention) — `clickGlobally` → `ClickGlobally`. Don't rename for "clarity" early.
3. Keep variable names. `clickCount`, `appKey`, `targetWindowID` survive verbatim.
4. Keep error message strings byte-equivalent (lets us share fixtures).
5. Don't introduce abstractions. No `IClickStrategy` factory. If upstream has `if-else`, we have `if-else`.
6. Don't async-ify what isn't async upstream. `CGEventPost` is sync; keep it sync.

These rules relax once **all upstream tests pass** (§11). Then optional ergonomics.

---

## 11. Tests

`tests/Everywhere.Mcp.Tests/`. Use the same xUnit setup the project already uses.

### 11.1 Schema tests

For each of the 9 upstream tools, assert generated MCP `tools/list` JSON exactly matches a snapshot copied from upstream (you'll generate this snapshot once by running upstream `open-computer-use mcp` and saving its `tools/list` response into `Fixtures/upstream-tools-list.json`).

### 11.2 KeyMapping tests

Port `OpenComputerUseKit/Tests/KeyMappingTests` (or equivalent) verbatim. Inputs: xdotool key names. Outputs: platform keycodes. Coverage must equal upstream coverage.

### 11.3 Snapshot tests

Use upstream `OpenComputerUseFixture` test target if portable; otherwise hand-port its JSON fixtures. Same input app state → same `tree_text` output (modulo platform-specific element ids).

### 11.4 Tool integration tests

For each tool: smoke test that runs against a mock `IVisualElementContext` and asserts the dispatched a11y / input call.

### 11.5 Cross-implementation parity test (optional)

For dev environments with `open-computer-use` installed: a script that drives both servers with the same script of MCP calls and diffs JSON outputs. Run on demand, not in CI.

---

## 12. Phased implementation plan

Each phase ends with a green test suite.

### Phase 1 — Skeleton + stdio
- New csproj, references, DI hook.
- `EverywhereMcpServer` boots empty tool list over stdio.
- `everywhere --mcp` CLI flag.
- One smoke test: client `tools/list` returns empty array.

Exit: `claude` MCP host can connect and see the server.

### Phase 2 — A11y-only Computer Use tools (no input simulation)
- Implement `list_apps`, `get_app_state` (with screenshot + tree), `click(element_index)`, `set_value`, `scroll`, `perform_secondary_action`.
- `SessionStore`, `ElementIndexer`, `SnapshotRenderer`.
- Mirror upstream ports for these handlers.
- Schema parity test passes.
- Snapshot fixture tests pass.

Exit: real-world demo — drive Safari (mac) / Notepad (win) / gedit (linux) via Claude Code. No input simulation needed because all listed tools are pure a11y.

### Phase 3 — Everywhere-only tools
- `get_focused_context`, `get_selected_text`, `pick_element`, `expand_element`, `get_terminal_output`, `screenshot`.
- Wires existing `IVisualElementContext`, `Everywhere.Terminal`.

Exit: Claude Code can answer "explain this" referencing user's current screen with one tool call.

### Phase 4 — Input simulation (closes superset)
- `IInputSimulator` + three platform impls. Mirror `InputSimulation.swift` / Linux Go / Windows Go.
- `KeyMapping.cs` ported.
- `FocusBorrow` impl.
- `click(x,y)`, `drag`, `type_text`, `press_key` come online.
- KeyMapping tests pass.

Exit: full feature parity with upstream. `open-computer-use` can be removed from user MCP config without regression.

### Phase 5 — HTTP transport
- Kestrel listener at `:7878/mcp`.
- Port-conflict fallback.
- Settings page entry: enable/disable + port.

Exit: client can connect via HTTP URL config.

### Phase 6 — Hardening
- Cross-impl parity test (§11.5) clean run.
- Logging via existing Everywhere logging.
- Telemetry hook (use existing build target if applicable).
- Docs: `docs/Mcp/USAGE.md` (user-facing).

Exit: shipped.

---

## 13. Out-of-scope (future)

- Auth / token gating for HTTP transport.
- Wayland production-grade input (Phase 4 ships X11 working + Wayland portal best-effort).
- Composite tool `do(intent: string)` that runs internal agent loop.
- WebSocket subscription for `selection_changed` push events.

---

## 14. Acceptance criteria

- `claude mcp` from Claude Code lists all 15+ tools.
- Smoke flow runs clean on macOS, Windows, Linux:
  ```
  list_apps → get_app_state(app=...) → click(element_index=...) → type_text(...) → get_app_state again
  ```
- All ported upstream tests in `tests/Everywhere.Mcp.Tests/` pass.
- User can replace `open-computer-use` mcpServer entry with `everywhere` URL and existing skills/agents keep working.
- `UPSTREAM_REF.md` present with pinned sha and mapping.
- `THIRD_PARTY_NOTICES.md` present with MIT attribution.

---

## 15. Open questions for implementer

These are not blockers; default per §10 if uncertain.

1. .NET MCP SDK API surface for HTTP transport: confirm streamable HTTP support level. If immature, ship stdio-only in Phase 1, add HTTP in Phase 5.
2. Wayland Portal: upstream uses `runtime.py` calling xdg-desktop-portal. Acceptable to require Python at runtime, or rewrite as native libdbus calls? Default: shell out to a small Python helper for v1, native v2.
3. CGEventTap permission prompt timing on macOS: best place to surface prompt. Default: lazy on first input simulation; fail loudly with actionable error.
4. Element index lifetime across `get_app_state` of *different* apps: separate scopes per `appKey` (current spec). Confirm no cross-app references.

---

End of spec.
