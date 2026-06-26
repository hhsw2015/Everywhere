---
name: everywhere
description: Guidance for using Everywhere — a local agent runtime with three INDEPENDENT capability families: Perception (read what the user means), Computer Use (drive native apps), Browser Use (drive web pages). Use this skill to pick the right family, sequence calls correctly, and avoid crosswiring.
---

# Everywhere

Everywhere exposes three **independent** capability families. They share an MCP server but they answer different questions and aren't substitutes for each other:

1. **Perception** *(Everywhere-native, cross-platform)* — what is the *user* already pointing at? A pinned element, a screen frame they drew, highlighted text, focused window, clipboard, idle time, browser url, Finder selection. Perception tools are READ-ONLY and don't drive any UI. They tell you what the user means before you act. **This is Everywhere's distinctive layer** — most agents can't ask the user "what do you mean by *this*?" without launching a picker; Everywhere already has the answer cached.
2. **Computer Use** *(macOS only in this build)* — drive native apps via accessibility: click, type, scroll, drag inside AppKit / SwiftUI / Catalyst / Electron / Office. Backed by the vendored OpenComputerUseKit Swift bridge (`libAxHelper.dylib`). The vendored Swift library targets `.macOS(.v14)`; OCCU upstream ships separate Windows (UI Automation) and Linux (AT-SPI2) implementations as part of its npm CLI, so cross-platform parity is achievable by spawning `open-computer-use mcp` as a child process — not yet wired in this build.
3. **Browser Use** *(cross-platform, requires the Everywhere browser extension)* — DOM-level automation of the **user's real browser** (Arc / Chrome / Edge / Brave / any Chromium-based browser running the companion extension). Operates on the same tabs the user can see, with their cookies, sessions, and audio output. The extension does the work in the background — the agent never needs to bring the browser to the foreground.

Pick families ORTHOGONALLY:
- Perception is "read input" — used before deciding which other family to invoke (or whether to invoke any).
- Computer Use is "operate native apps".
- Browser Use is "operate web pages".

A single user task often uses Perception once + one of the other two — never all three at the same step.

## Decision flow

```
question
  │
  ├─ Step A (always free, cheap):
  │    is the user pointing at something? (pin / whiteboard / selection / focus)
  │      yes → Perception read first; result tells you what they mean
  │      no  → skip
  │
  └─ Step B: where does the action live?
       inside a web page (DOM)        → Browser Use (selectors, dispatch_keys, CDP)
       native app on macOS            → Computer Use
       browser chrome / window itself → Computer Use (palette / tab strip / settings UI)
       neither / read-only            → done after Perception
```

Perception never *replaces* Computer Use or Browser Use. It's the input lens you use *before* picking one of those two — or before deciding no action is needed at all.

## Tool families

### Perception (read what the user means) — start here

Tools: `pick_element`, `read_pick`, `read_whiteboard`, `read_whiteboard_image`, `get_selected_text`, `get_focused_context`, `get_app_context`, `get_finder_selection`, `get_terminal_output`, `get_clipboard`, `get_idle_time`, `get_browser_url`, `get_browser_tabs`, `screenshot`, `expand_element`.

These are **read-only** and **standalone** — they don't act on anything. They turn ambient user intent (a Pin, a frame, a highlight, a focused window) into a concrete target so the next step doesn't have to guess.

Signal priority — when a request is ambiguous about *what*, read the strongest available signal **once**, not all of them:

| Priority | Signal | Tool | When |
|----------|--------|------|------|
| 1 | User actively pinned an element | `read_pick` | They just used the Pin hotkey |
| 2 | User drew a frame on screen | `read_whiteboard` / `read_whiteboard_image` | They just used the Whiteboard hotkey |
| 3 | User highlighted text | `get_selected_text` | "Translate this", "Summarize this" |
| 4 | User's current focus | `get_focused_context` | "What am I looking at?" |
| 5 | User named the app | `get_app_context(app_hint)` | "Open the email I'm writing" — does list+match+snapshot in one call |

Higher priority wins. Don't fan out across all five.

See [references/perception.md](references/perception.md) for lifecycle (when pinned/whiteboard state expires) and combination patterns.

### Computer Use (operate any app)

Tools: `list_apps`, `get_app_state`, `click`, `perform_secondary_action`, `scroll`, `drag`, `type_text`, `press_key`, `set_value`.

Core workflow:

1. Inspect what's running: `list_apps`.
2. Snapshot the target window: `get_app_state(app: "<name or bundle id>")`. The result is an indexed a11y tree; each row is prefixed `[<element_index>]`.
3. Use that index in subsequent actions. Always `get_app_state` again after navigation, modal changes, or a failed action — indices expire.
4. For long text (chat history, email body, document body), pass `show_full_text: true`.
5. Prefer semantic actions over coordinates: `set_value` on text controls, `perform_secondary_action` for exposed AXActions, indexed `click` over `click(x,y)`.

See [references/computer-use.md](references/computer-use.md) for advanced cases (drag, double-click, right-click, secondary actions).

### Browser Use (operate the user's real browser)

35+ `browser_*` tools that drive the user's actual browser through the Everywhere companion extension: tabs, navigation, DOM read/write, dispatched keys, screenshots, CDP, cookies, bookmarks, history. The user's session, cookies, and audio output are all in scope. The extension does its work in the background — it does NOT switch the user's frontmost app to the browser; the agent can act on a non-active tab while the user keeps working in another window.

Common entry points:

- `browser_tab_list` — start here when you don't have a tab id yet.
- `browser_page_analyze` / `browser_dom_query` — read the page.
- `browser_element_click` / `browser_element_fill` / `browser_dispatch_keys` — act.
- `browser_cdp_evaluate` — escape hatch for sites that block extension-side `evaluate_js` (most production pages with strict CSP).

This is NOT a headless / sandbox browser. If you want a sandboxed browser separate from the user's session, that's a different skill.

See [references/browser-use.md](references/browser-use.md) for the full tool list, MV3 CSP gotchas, and Browser-Use-vs-Computer-Use guidance on the same browser window.

## Operating rules

- Treat the target desktop as the user's real session. Do not inspect password managers, unrelated private content, or sensitive apps unless the user explicitly asked for that task.
- Ask before sending, deleting, purchasing, approving, uploading, or making other externally visible changes.
- Always `get_app_state` before using `element_index`. Indices do not survive across sessions or large UI changes.
- Prefer semantic actions (`set_value`, `perform_secondary_action`) and indexed `click` over coordinate `click(x,y)`.
- Do not enable `EVERYWHERE_ALLOW_GLOBAL_POINTER_FALLBACKS=1` unless the user explicitly wants diagnostic behavior that may move the real pointer.
- Switching between Computer Use, Browser Use, and Perception mid-step loses state. Pick a surface, complete the action, then switch.

## Hand-off patterns

Move data between surfaces explicitly — coordinates and element indices do not transfer.

- **Browser → Computer**: copy text in the browser via Cmd+C, then read with `get_clipboard` from Computer Use side.
- **Computer → Browser**: write a file with `set_value` / `type_text`, the browser uploads it; or copy via Cmd+C and the browser reads via clipboard.
- **Perception → Computer**: `read_pick` returns an `element_index` valid for the current `get_app_state` of that app — pass it to `click`/`set_value` directly.

## Common failure modes

- "App not found" → run `list_apps`, use the exact name or bundle id.
- "Element index expired" → re-run `get_app_state`.
- Element click silently no-ops on a SwiftUI gesture button → some apps reject AXPress; fall back to coordinate `click` at the element's center.
- Text replace puts caret instead of replacing → `set_value` ignores selection; for "clear and replace" prefer `set_value` over selection-based type.

For deeper debugging, see [references/troubleshooting.md](references/troubleshooting.md).

## References

- [references/computer-use.md](references/computer-use.md) — full Computer Use tool reference, advanced patterns
- [references/browser-use.md](references/browser-use.md) — Browser Use tool list, common patterns
- [references/perception.md](references/perception.md) — pick/whiteboard/selection lifecycle and combination
- [references/troubleshooting.md](references/troubleshooting.md) — permissions, snapshot failures, action failures
