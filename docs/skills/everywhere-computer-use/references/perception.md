# Perception Reference

Perception tools tell you **what the user means** without firing any action. They are Everywhere's differentiator — most agents have to guess from the prompt and then fan out exploratory reads. Here, the user has *already* told you (with a Pin, a frame, a highlight, a focused window). Read that signal first; act second.

Perception tools work on every platform, including the platforms where Computer Use isn't wired yet (Windows / Linux).

## Tools

| Tool | What it returns | When |
|------|-----------------|------|
| `pick_element` | Opens an interactive picker. Returns the picked element's data. Cancellable: `{cancelled: true}`. | The user explicitly asks "pick this for me" *now*. Avoid spawning a picker uninvited — it's modal. |
| `read_pick` | Reads the LATEST pinned element (no picker prompt). | The user already used the Pin hotkey; you want what they pinned. |
| `read_whiteboard` | The latest user-drawn screen frame as text. | User just drew on screen and asked something. |
| `read_whiteboard_image` | Same frame but as an image. | When OCR or visual cues matter. |
| `get_selected_text` | OS-wide currently selected text. | "Translate this", "summarize this". |
| `get_focused_context` | The currently focused element / window across the whole desktop. | "What am I looking at?", "Continue the email". |
| `get_app_context(app_hint)` | Fuzzy-matches an app name + returns `get_app_state` of its largest window. | The user said an app name, you want to act on it. PREFER over `list_apps` + `get_app_state`. |
| `get_finder_selection` | Selected files in the front Finder window. | "Open these files", "rename these". |
| `get_terminal_output` | Visible scrollback in the front terminal. | "What did that command print?" |
| `get_clipboard` | OS clipboard text. | Hand-off from another tool / the user's manual copy. |
| `get_idle_time` | Seconds since last user input. | Decide whether to interrupt with a notification or wait. |
| `get_browser_url` | URL of the user's frontmost browser tab. | Context for "this page". |
| `get_browser_tabs` | Open tabs in the user's real browser. | "Switch to my GitHub tab". |
| `screenshot` | A PNG of an indexed element OR window OR full screen. | Visual reasoning, fallback when a11y misses. |
| `expand_element(element_index)` | Expand a tree node so its children appear in the next `get_app_state`. | Some collapsed disclosures, lazy-loaded outline rows. |

## Signal priority

When the user's request is ambiguous about a target, read the strongest available signal *only*:

```
1. read_pick              ← strongest: explicit pin, just for this task
2. read_whiteboard*       ← user drew something, very recently
3. get_selected_text      ← user highlighted text
4. get_focused_context    ← user's current focus
5. get_app_context(hint)  ← user named an app
6. nothing                ← ask the user
```

Don't fan out across all five. One read, then act.

## Lifecycle

- **Pin (read_pick)** survives until the user pins something else, dismisses, or restarts Everywhere. Stable across multiple tool turns. Read it once and reuse the index — it points to a specific element identity, not a tree slot.
- **Whiteboard (read_whiteboard*)** is per-gesture. After the user closes the frame, the data is gone. Read it during the same conversation turn or summarize and store yourself.
- **Selected text** is a live OS read — value at call time. Selections collapse the moment the user clicks elsewhere.
- **Focused context** is also a live read; the moment the user activates another window it's stale.
- **Browser url / tabs** read the user's REAL browser, not Browser Use's managed session. Don't conflate them.

## Combination patterns

### "Click the thing I pinned"

```
read_pick                         → returns element_index for the pinned element
click(app, element_index)
```

`read_pick` already implicitly snapshots the host app, so the index is valid for `click` immediately.

### "Translate this"

```
get_selected_text                 → returns the highlighted string
# pass to LLM, no Computer Use needed
```

### "Reply to the email I'm writing"

```
get_focused_context               → shows the currently focused text editor + selection
# LLM drafts reply text
set_value(app, element_index, draft)
```

### "What did this region say?" (text inside a Canvas/PDF/image)

```
read_whiteboard_image             → image
# LLM reads the image directly, no AX path required
```

### "Open these Finder files in VS Code"

```
get_finder_selection              → array of file paths
# pass paths to a shell tool or to VS Code's CLI; no clicking needed
```

## When NOT to use perception

If the user has already given you the target verbally and unambiguously — "open Calculator and compute 7×8" — perception adds latency without information. Skip straight to `list_apps` / `get_app_context`.

## Cross-platform

All perception tools are cross-platform. Computer Use tools require macOS today (see [computer-use.md](computer-use.md)). On Windows/Linux, you can still: read clipboard, read selected text, read focused context, read browser url/tabs, screenshot — and then think about the task even if you can't yet drive the GUI directly.
