# Browser Use Reference

Use Browser Use when the task lives inside a webpage — anything you'd otherwise do by inspecting and manipulating the DOM. The browser session is **managed and headless**, separate from the user's real Chrome/Safari, so it doesn't disrupt their tabs.

If the page is rendered inside the user's real browser and the user explicitly wants to act there, use Computer Use (driving the browser as just another app) instead.

## When to choose Browser Use

- Read structured data (search results, table rows, post lists)
- Fill and submit forms
- Wait for XHR / dynamic content
- Walk through multi-page flows where DOM state matters
- Cross-tab / cross-iframe inspection

## When NOT to choose Browser Use

- Interacting with the chrome of the browser itself (tab bar, address bar, settings) — that's Computer Use
- Canvas / WebGL / PDF viewers / video players — DOM is opaque, fall back to Computer Use
- The page is in the user's real browser and they expect to see actions happen there — Computer Use

## Cheap → expensive page reads

Pick the lightest tool that answers the question:

| Tool | When |
|------|------|
| `tree(maxDepth?, backendNodeId?)` | Default: a semantic outline of role / name / value per node. Scope with `backendNodeId` when zooming in. |
| `findElement(role, name)` | One-shot lookup for a button/link by role + visible name. |
| `nodeDetails(backendNodeId)` | Turn a tree node into a CSS selector + attributes. |
| `markdown(selector? \| backendNodeId? \| url?)` | Readable text of one subtree (or the whole page as last resort). |
| `extract(schema)` | Structured pull: selectors → field values. The only read whose output replays as a recorded `extract(...)` call. |
| `html(selector? \| backendNodeId? \| url?)` | Raw HTML when you need attributes that markdown drops. |

## Acting on the page

| Tool | Purpose |
|------|---------|
| `goto(url)` | Navigate. |
| `click(selector? \| backendNodeId)` | Click. CSS selector preferred for replay stability. |
| `fill(selector \| backendNodeId, value)` | Type into an input. |
| `fill_form(elements)` | Fill many inputs in one call — faster, fewer turns. |
| `selectOption(selector, value)` | `<select>` choice. |
| `setChecked(selector, checked)` | Checkbox / radio. |
| `press(selector? \| backendNodeId, key)` | Keyboard event (`Enter`, `Tab`, ...). |
| `hover(selector \| backendNodeId)` | Trigger hover-only menus. |
| `drag(from_uid, to_uid)` | Drag one element onto another. |
| `scroll(x?, y?, backendNodeId?)` | Scroll window or a specific element. |
| `evaluate(script, save?)` | Page-side JS escape hatch. Use sparingly — prefer `extract`. |
| `waitForSelector(selector)` / `waitForState("networkidle")` / `waitForScript(expr)` | Synchronisation. |

## Standard sequence

```
goto(url)
  ↓
waitForState("networkidle")     ← when the page hydrates after load
tree(maxDepth: 4)               ← orient
  ↓ pick targets
fill_form([...])                ← write
click("...submit...")           ← act
waitForSelector("...result...") ← sync after action
extract({field: "selector"})    ← finish with structured data
```

`extract` is what makes a Browser Use session replayable. Answers lifted from `markdown` text in chat are not — finish data tasks with `extract`.

## Re-inspect after every state-changing action

A `click` or form submit can rewrite the whole DOM. Stale `backendNodeId`s from the old `tree` will silently miss. Re-run `tree` / `findElement` after any navigation or action.

## Search

`search(query)` runs a web search and returns `{title, url, snippet}` markdown. After a `search`, the browser DOM is in an unspecified state — use `goto(<result url>)` to interact, don't assume the DOM matches the SERP.

## Hand-off

Files: write a local file via Computer Use (`set_value` / shell), upload via Browser Use (`upload_file` if exposed) or by typing the path into the file chooser.

Text: `Cmd+C` in browser → `get_clipboard` from Computer Use side. Or `evaluate(\"document.title\")` directly.
