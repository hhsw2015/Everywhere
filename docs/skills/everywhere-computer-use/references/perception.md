# Perception Reference

Read what the user means before acting. Read-only. Cross-platform.

## Tools

| Tool | Returns | When |
|------|---------|------|
| `pick_element` | Picked element + tree slice. Cancellable: `{cancelled:true}`. | User explicitly asks to pick now. Modal — don't spawn uninvited. |
| `read_pick` | Latest pinned element (no UI prompt). | User used Pin hotkey. |
| `read_whiteboard` | Latest user-drawn frame as text. | User just drew on screen. |
| `read_whiteboard_image` | Same as PNG. | When OCR/visual cues matter. |
| `get_selected_text` | OS-wide selection. | "Translate this" / "Summarize this". |
| `get_focused_context` | Currently focused element + window. | "What am I looking at?" |
| `get_app_context(app_hint)` | Fuzzy-matches an app + returns its `get_app_state`. | User named an app inexactly. PREFER over `list_apps + get_app_state`. |
| `get_finder_selection` | Selected files in front Finder window. | "Open these files". |
| `get_terminal_output` | Visible scrollback of front terminal. | "What did that command print?" |
| `get_clipboard` | OS clipboard text. | Hand-off from another tool / user copy. |
| `get_idle_time` | Seconds since last input. | Decide whether to interrupt. |
| `get_browser_url` / `get_browser_tabs` | User's REAL browser frontmost tab / tab list. | NOT the same as Browser Use's working tab. |
| `screenshot` | PNG of element / window / screen. | Vision reasoning, fallback when a11y misses. |
| `expand_element(element_index)` | Expand a collapsed tree node so children appear next snap. | Lazy outline rows. |

## Signal priority

```
1. read_pick          — explicit pin, strongest
2. read_whiteboard*   — user-drawn frame
3. get_selected_text  — highlighted text
4. get_focused_context— current focus
5. get_app_context    — user named an app
6. ask                — no signal
```

One read. Then act. Don't fan out.

## Lifecycle

| Source | Persistence |
|--------|-------------|
| Pin (`read_pick`) | Stable across turns until user re-pins / dismisses / restart. Reuse safely. |
| Whiteboard | Per-gesture. Read in same turn or stash yourself. |
| Selected text | Live read at call time. Collapses on next click. |
| Focused context | Live. Stale the moment user activates another window. |
| `get_browser_url` / tabs | User's real browser, NOT Browser Use's working tab. Don't conflate. |

## Patterns

| Intent | Sequence |
|--------|----------|
| "Click the thing I pinned" | `read_pick` → `click(app, element_index)` (index from pick is valid) |
| "Translate this" | `get_selected_text` → LLM, no automation |
| "Reply to email I'm writing" | `get_focused_context` → draft → `set_value(app, idx, draft)` |
| "What did this region say?" | `read_whiteboard_image` → vision read |
| "Open these Finder files in VSCode" | `get_finder_selection` → shell or VSCode CLI |

## When NOT to use

User already gave you the target verbally and unambiguously ("open Calculator and compute 7×8"). Skip perception, go straight to `list_apps` / `get_app_context`.
