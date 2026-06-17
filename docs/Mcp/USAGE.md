# Everywhere MCP Server — usage

The Everywhere desktop app ships an embedded
[Model Context Protocol](https://modelcontextprotocol.io) server. It exposes
the same Computer Use tools as
[`iFurySt/open-codex-computer-use`](https://github.com/iFurySt/open-codex-computer-use)
plus six Everywhere-only context tools so an agent can pick up the
**focused window**, **selected text**, and **terminal output** without a
two-step tool dance.

## Transports

| Transport | Address | When to use |
|-----------|---------|-------------|
| stdio     | `everywhere --mcp`                  | Desktop agents (Claude Desktop, Codex, Cursor, etc.) — they spawn the binary and talk over stdin/stdout. |
| streamable HTTP | `http://localhost:7878/mcp`   | Web agents and tools that prefer an HTTP transport. Loopback-only. |

The HTTP listener is bound automatically when the GUI is running. Override
the port with `EVERYWHERE_MCP_PORT` (or via `Settings → MCP server` once
the page lands). On bind conflict it walks `7878..7888` before failing.

## Client config

### Claude Desktop / `claude mcp` / Codex CLI

```jsonc
{
  "mcpServers": {
    "everywhere": {
      "command": "everywhere",
      "args": ["--mcp"]
    }
  }
}
```

`open-computer-use` users can drop their existing entry — the tool names,
parameter names, and JSON shapes are byte-compatible.

### HTTP

```jsonc
{
  "mcpServers": {
    "everywhere-http": {
      "url": "http://localhost:7878/mcp"
    }
  }
}
```

## Tools

### Computer Use (mirrored from upstream)

`list_apps`, `get_app_state`, `click`, `drag`, `type_text`, `press_key`,
`scroll`, `set_value`, `perform_secondary_action`. Schemas, names, and
descriptions are mirrored verbatim from
[ToolDefinitions.swift](https://github.com/iFurySt/open-codex-computer-use)
so existing test corpora and prompt templates apply unchanged.

### Everywhere-only

| Tool | Use when |
|------|----------|
| `read_pick`           | The user pre-pinned an element via the Pin-Element hotkey. ALWAYS try this first on deictic refs. Reading consumes the pin. |
| `get_app_context`     | The user named an app (*"the browser"*, *"slack"*, *"vscode"*). One-shot fuzzy resolve + snapshot. |
| `get_focused_context` | The user references their current view (*"this"*, *"that"*, *"here"*, *"这个"*) and there's no fresh pin. |
| `get_clipboard`       | The user references *"剪贴板"* / *"the thing I just copied"*. |
| `get_idle_time`       | Decide whether the user is at the keyboard before grabbing focus. |
| `get_browser_url`     | The user asks "what page am I on" / "current URL". |
| `get_finder_selection` | The user references files they have selected in Finder (full POSIX paths + names + is_dir). |
| `get_browser_tabs`    | The user asks about all open browser tabs (Safari/Chrome/Arc/Brave/Edge). |
| `get_selected_text`   | OS-wide selection. Returns `""` if nothing is selected. |
| `read_pick`           | Reads the element the user pre-pinned via the Agent Pick hotkey (Settings → Shortcut → "Pin Element for AI Agent"). PREFER this BEFORE `get_focused_context` on deictic references. Reading consumes the pin. |
| `pick_element`        | Triggers Everywhere's visual picker — user clicks an element/window/screen. |
| `expand_element`      | Re-walk a previously indexed subtree with a fresh budget when `omitted_children=true`. |
| `get_terminal_output` | Visible PTY of the focused terminal app. Empty string if not on a terminal. |
| `screenshot`          | Element-scoped or window-scoped PNG, same compression envelope as `get_app_state`. |

## Element index

`get_app_state`, `get_focused_context`, `pick_element`, and
`expand_element` issue an integer-string index for every node they emit
(`[42] Button "Submit" …`). Pass that index back to `click`, `scroll`,
`set_value`, `perform_secondary_action`, `expand_element`, and
`screenshot` to act on the element directly — no pointer movement, no
focus borrow, target window need not even be foreground.

Indices are scoped per app key (bundle id / exe path / `WM_CLASS`) and
invalidated on the next snapshot for that app. Calling a tool with an
expired index returns:

```json
{ "isError": true, "content": [{ "type": "text", "text": "Element index 42 not found in current snapshot." }] }
```

Recover by calling `get_app_state` again.

## Behavioral contracts

- **Coordinate paths** (`click(x,y)`, `drag`, `type_text`, `press_key`)
  borrow foreground briefly, run the input event, then restore the
  previous foreground window. Single-flight per process; concurrent
  callers wait up to 5 s.
- **Element paths** never touch the focus stack.
- **Screenshots** are PNG-base64, capped at `maxDimension=1280`,
  `minScale=0.25`, `maxPNGBytes=900_000` — values mirrored from upstream.
- **Errors** come through as MCP tool-call errors
  (`{ isError:true, content:[{type:"text",text:"…"}] }`), not protocol
  errors. Strings are mirrored from upstream so existing client-side
  parsers still work.

## Integration with existing Everywhere

Adding the listener to a host project:

```csharp
services
    .AddSingleton<IInputSimulator, MyPlatformInputSimulator>()
    .AddSingleton<IFocusBackend, MyPlatformFocusBackend>()
    .AddEverywhereMcp(opts =>
    {
        opts.Port = 7878;
        opts.Enabled = true;
    });

// Then, once the GUI service provider is built:
await provider.StartEverywhereMcpHttpAsync(cancellationToken);
```

When `IInputSimulator` / `IFocusBackend` are not registered the abstraction
falls back to a `NotSupportedInputSimulator` that returns a clear error
explaining how to wire the platform implementation. Element-index paths
continue to work because they never call into the simulator.

## Snapshot Context hotkey + Claude Code hook

For zero-friction "I was just looking at X, now I'm in the terminal asking about
it" flow, install the UserPromptSubmit hook:

1. Bind a hotkey in **Settings → Shortcut → Snapshot Context for AI Agent**
2. Add the hook to `~/.claude/settings.json`:

```jsonc
{
  "hooks": {
    "UserPromptSubmit": [
      {
        "command": "/Applications/Everywhere.app/Contents/Helpers/everywhere-context-hook"
      }
    ]
  }
}
```

How it works:

- You press the hotkey while looking at any app → Everywhere captures
  `app + window_title + url + selection` into
  `~/Library/Application Support/Everywhere/context-stash.json`
  (atomic write, expires after 5 minutes).
- You switch to your terminal and ask the agent something.
- On Enter, Claude Code runs the hook (~3 ms binary, no network).
- If the file exists & is fresh, the hook prints its contents to stdout — Claude
  Code prepends those bytes to your prompt as
  `[everywhere-ctx] app=… title=… url=… selection=…` plus a structured
  `[everywhere-ctx-json] {…}` line.
- The hook deletes the file on read (Take semantics; no stale context).
- File absent / stale → hook exits silently, prompt unchanged.

The `[everywhere-ctx]` line is a pointer, not a deep snapshot — the agent calls
`get_focused_context` / `get_app_context` / `screenshot` only when it actually
needs to drill in.

## Limitations (v1)

- HTTP transport has no auth — local-loopback only by middleware policy.
- `get_terminal_output` reads what the a11y layer surfaces; PTY-level
  capture is on the v2 list.
- Wayland input simulation is best-effort; X11 is the supported path.
