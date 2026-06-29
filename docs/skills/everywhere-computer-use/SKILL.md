---
name: everywhere
description: Local agent runtime exposing 4 capability families on macOS (Browser Use + Doc Readers cross-platform). Use this skill to pick the right tool family before acting.
---

# Everywhere

Four independent families. Pick one per step, do not mix mid-action.

| Family | What | Platforms |
|---|---|---|
| Perception | Read user-current state (pin / whiteboard / selection / focus / clipboard / browser url / Finder selection / terminal output / idle / screenshot) | All |
| Computer Use | Drive native macOS apps (click / type / scroll / drag / set_value / press_key / perform_secondary_action / get_app_state / list_apps) | macOS only |
| Browser Use | Drive user's real browser via companion extension (`browser_*` tools: tabs / DOM / dispatch_keys / CDP / cookies / bookmarks / history / screenshot / clipboard) | All (where extension installed) |
| Doc Readers | Extract text from a file on disk: `doc_read_pdf` / `doc_read_docx` / `doc_read_xlsx` / `doc_read_pptx` / `doc_read_epub` / `doc_read_html` / `doc_read_txt`. Dispatch by `kind_hint` from `get_finder_selection`. | All |

## Decision

```
Step 1: User pointing at something (pin/whiteboard/highlight/focus)?
        yes → Perception read first
        no  → skip
Step 2: Where does the action live?
        DOM inside a web page              → Browser Use
        macOS native app or browser chrome → Computer Use
        a file on disk (the user pointed at one in Finder, or named a path)
                                           → Doc Readers, dispatched by kind_hint
        nothing to drive                   → done after Perception
```

Perception NEVER replaces Computer Use / Browser Use. It resolves "what" before deciding "how".

## Boundary vs your own tools

You already have Bash / Read / Grep / web fetch (and vision/ASR if your model is multimodal). Use those for system state, files, web content. Use Everywhere ONLY for:

- What the user is pointing at right now (pin / whiteboard / focused window / selected text).
- The user's real browser tabs and DOM (cookies, session, audio output).
- macOS GUI automation (the user's actual apps; not a sandbox).

## Signal priority (when "what" is ambiguous)

| Priority | Signal | Tool |
|---|---|---|
| 1 | User just pinned an element | `read_pick` |
| 2 | User just drew a frame | `read_whiteboard` / `read_whiteboard_image` |
| 3 | User highlighted text | `get_selected_text` |
| 4 | User's current focus | `get_focused_context` |
| 5 | User named an app | `get_app_context(app_hint)` |

Pick one. Do not fan out.

## Operating rules

- `get_app_state` returns indexed a11y tree. Indices expire on every fresh snapshot — re-snap before using `element_index` after any state change.
- Prefer semantic actions (`set_value`, `perform_secondary_action`, `browser_element_click` with selector) over coordinates.
- Never act on password managers, banking, or sensitive surfaces unless the user explicitly asked.
- Pause before send / delete / submit / approve / purchase.
- `EVERYWHERE_ALLOW_GLOBAL_POINTER_FALLBACKS=1` is diagnostic only. Don't enable.
- Switching families mid-step loses state. Pick one, finish, then switch.

## Long-tail tools (when core doesn't fit)

`tools/list` ships ~32 core tools — perception priority, plus high-frequency
operations. Niche tools are hidden to keep the system prompt small. Reach
them on demand:

| Step | Tool | Use |
|---|---|---|
| 1 | `list_more_tools(category)` | Discover names + one-line description. |
| 2 | `call_tool(name, arguments)` | Invoke. Args are passed through verbatim. |

Categories: `action_browser` | `action_macos` | `perception_active` |
`perception_content` | `debug` | `config`. Omit for an overview with counts.

When to reach for it:
- Browser cookies / storage / state / auth → `config`
- CDP eval / React tree / network capture / vitals → `debug`
- find_by_role / find_by_label / find_by_testid / hover / select → `action_browser`
- `doc_read_xlsx` / `pptx` / `epub` / `html` / `txt` → `perception_content`
- `drag` / `perform_secondary_action` / `pick_element` → `action_macos`
- `list_apps` / `expand_element` / `get_browser_tabs` → `perception_active`

Native tools are dispatched in-process; browser tools forward through the
OpenDia bridge. If `call_tool` returns `{"ok":false,"error":"opendia-not-connected"}`
the browser extension isn't connected — surface this; do NOT retry.

## Hand-off

| From → To | How |
|---|---|
| Browser → System | `Cmd+C` then `get_clipboard` (perception); or `browser_get_selected_text` |
| System → Browser | `browser_set_cookie` / `browser_clipboard_write_text` / `browser_page_navigate` |
| Perception → Computer Use | `read_pick` returns `element_index` valid for current `get_app_state`; use it directly |
| Perception → Doc Readers | `get_finder_selection` returns each file's `kind_hint` (`pdf` / `docx` / `xlsx` / `pptx` / `epub` / `html` / `text` / `folder` / `image` / `unknown`) and `mime`; map `kind_hint` straight to `doc_read_<kind>(path)`. `unknown` (incl. legacy `.doc` / `.xls` / `.ppt`) → fall back to `get_clipboard` / user prompt, do NOT invoke a reader. |

## Common failures

- "App not found" → `list_apps` for exact name/bundle id.
- "Element index expired" → re-run `get_app_state`.
- AXPress refused on SwiftUI gesture button → coordinate `click` at element center.
- `browser_evaluate_js` returns "Function-from-string forbidden" → MV3 CSP. Use `browser_dom_query` / `browser_dispatch_keys`, or `browser_cdp_evaluate`.
- `browser_dispatch_keys` errors "keys array required" → `keys: ["k"]` not `keys: "k"`.
- Computer Use tool errors "only available on macOS" → Win/Linux automation not wired yet; Browser Use + Perception still work.
- `doc_read_pdf` returned `metadata.likely_scanned=true` with near-empty `text` → scanned PDF, no OCR on this branch. Tell the user; do not retry.
- `doc_read_xlsx` raw cell values look like `36528` not `01/03/00 12:00` → reader exposes serial dates verbatim, no formatting. Convert downstream if needed.

## References

- [computer-use.md](references/computer-use.md) — full Computer Use tool reference
- [browser-use.md](references/browser-use.md) — full Browser Use tool reference + MV3 / CDP gotchas
- [perception.md](references/perception.md) — pick / whiteboard lifecycle, combination patterns
- [troubleshooting.md](references/troubleshooting.md) — permissions, snapshot failures, action failures
