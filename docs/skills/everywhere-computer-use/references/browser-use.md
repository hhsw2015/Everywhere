# Browser Use Reference

Browser Use drives **the user's real browser** (Arc / Chrome / Edge / Brave / any Chromium-based browser running the Everywhere companion extension). It is NOT a headless / sandbox browser — operations happen on the same tabs the user can see, with the user's cookies, sessions, logins, audio output, and visual rendering. Frontmost is preserved: tools work fully in the background, the user does not need to context-switch into the browser for the agent to act on it.

Use Browser Use whenever a task involves **content inside a web page**, period. Computer Use can also click on a browser window, but Computer Use only sees the accessibility tree — Browser Use sees the live DOM, network responses, cookies, console, and the currently rendered video / audio element.

## When to choose Browser Use

- Read or extract structured data from a webpage (search results, tables, posts)
- Fill and submit web forms
- Walk through a multi-page flow where DOM state matters
- Interact with the user's logged-in services (Gmail, GitHub, Notion, ...) — cookies and session are the user's
- Trigger media playback the user can actually hear (audio elements need a real browser)
- Cross-tab navigation, tab management, bookmarks, history
- Inspect cookies, localStorage, console messages, network requests

## When NOT to choose Browser Use

- The target is the **chrome of the browser itself** (tab strip, address bar, bookmark bar, browser settings UI). Use Computer Use; those are native AppKit widgets, not DOM.
- The target is a **non-browser native app** (Notes, Calculator, Finder, IDE, Slack desktop app). Use Computer Use.
- The user has not granted the Everywhere extension on this browser. Tools return a clear failure; surface it to the user instead of falling through to Computer Use.

## Tool inventory

The exact MCP tools exposed by `Everywhere.Mcp` (35+ at last count). Group by purpose:

### Tabs and navigation
| Tool | Purpose |
|------|---------|
| `browser_tab_list` | List every tab across windows: id, title, url, active, pinned, status. Start here when the agent doesn't know the tab id. |
| `browser_tab_create(url?)` | Open a new tab. |
| `browser_tab_close(tabId)` | Close a tab. |
| `browser_tab_switch(tabId)` | Make a tab the active one (still no app activation; user's frontmost app is unchanged). |
| `browser_page_navigate(tabId, url)` | Navigate the given tab. |
| `browser_claim_tab(tabId)` | Mark a tab "owned" by the agent so concurrent tools don't collide. |
| `browser_finalize_tabs` | Release claims when the task is done. |
| `browser_name_session(name)` | Tag the current automation session for log clarity. |

### Reading the page
| Tool | Purpose |
|------|---------|
| `browser_page_analyze(tabId)` | Quick semantic outline of the page (role / text / id). Default starting point. |
| `browser_dom_query(tabId, selector, action?, attributes?)` | Single-element DOM lookup. `action` controls what to do with the match (read attrs, get text, ...). |
| `browser_dom_query_all(tabId, selector)` | List form. |
| `browser_page_extract_content(tabId)` | Readable-mode-style extraction of main content. |
| `browser_get_page_links(tabId)` | All anchors, deduped. |
| `browser_get_selected_text(tabId)` | What the user currently has highlighted. |
| `browser_get_cookies(tabId, domain?)` | Read cookies. |
| `browser_screenshot(tabId, fullPage?)` | PNG of the rendered page. |
| `browser_page_style(tabId, selector)` | Computed CSS for an element. |
| `browser_element_get_state(tabId, selector)` | Visibility, disabled, value, etc. |

### Acting on the page
| Tool | Purpose |
|------|---------|
| `browser_element_click(tabId, selector)` | Click. |
| `browser_element_fill(tabId, selector, value)` | Fill an input. |
| `browser_dispatch_keys(tabId, keys: [string])` | Send keyboard events directly to the page. **`keys` MUST be an array** (e.g. `["k"]` to toggle YouTube playback). Sending bare `"k"` returns "keys array required". |
| `browser_page_scroll(tabId, x?, y?)` | Scroll the page or a container. |
| `browser_wait_for_selector(tabId, selector, timeoutMs?)` | Sync barrier after a DOM-changing action. |
| `browser_page_wait_for(tabId, condition)` | Wait for a load state / network idle / etc. |
| `browser_clipboard_read_text` / `browser_clipboard_write_text` | Page-side clipboard (separate from system clipboard). |

### Bookmarks / history
| Tool | Purpose |
|------|---------|
| `browser_get_bookmarks` / `browser_add_bookmark(url, title?)` | Read or write the user's bookmark tree. |
| `browser_get_history(query?)` | Search the browser history. |

### CDP (Chrome DevTools Protocol) — power-user
| Tool | Purpose |
|------|---------|
| `browser_cdp_input_mouse(tabId, x, y, type)` | Synthesise mouse events at coordinates. Use when a target intercepts CSS clicks (custom canvas / WebGL / shadow DOM). |
| `browser_cdp_input_keys(tabId, ...)` | Lower-level than `dispatch_keys`. |
| `browser_cdp_list_network_requests(tabId)` | Inspect XHR / fetch traffic. |
| `browser_cdp_get_response_body(tabId, requestId)` | Pull a specific network response body. |
| `browser_cdp_list_console_messages(tabId)` | Console output. |
| `browser_cdp_upload_file(tabId, selector, path)` | File-input upload. |
| `browser_cdp_evaluate(tabId, expression)` | Evaluate an expression via CDP — bypasses MV3 CSP that blocks `browser_evaluate_js` on most production pages. Prefer `browser_dom_query` / `browser_dispatch_keys` first; reach for this when nothing else works. |

### Identity helpers
| Tool | Purpose |
|------|---------|
| `get_browser_url` | URL of the user's frontmost tab in their REAL browser. **Not the agent's working tab** — keep them separate. |
| `get_browser_tabs` | Same scope as above. |

## Standard workflow

```
get_browser_url            ← (perception) what page is the user on?
  ↓ if relevant, claim it
browser_tab_list
  ↓ pick tabId
browser_page_analyze       ← orient
  ↓
browser_dom_query / get_selected_text / extract_content   ← read
  ↓
browser_element_fill / click / dispatch_keys   ← act
  ↓
browser_wait_for_selector  ← sync after a state change
  ↓
browser_dom_query          ← verify the new state
```

## Important constraints

- **MV3 CSP blocks `Function`-from-string.** A naive `browser_evaluate_js` that wraps user code in `new Function(...)` is rejected by most production pages with `"evaluate_js requires Function-from-string, which MV3 + page CSP forbid"`. Use `browser_dom_query` / `browser_dom_query_all` / `browser_dispatch_keys` / `browser_wait_for_selector` for DOM operations. As a last resort use `browser_cdp_evaluate`, which goes through CDP rather than the extension content script and is not subject to that restriction.
- **`browser_dispatch_keys` requires an array.** `keys: "k"` errors with `"keys array required"`; use `keys: ["k"]`. Multiple keys go in the array in order.
- **Tab ids change** when the user closes / reopens a tab. Always `browser_tab_list` again after a navigation that might have spawned a new tab; don't assume the id you used five turns ago is still valid.
- **Some sites need a user gesture to start audio/video.** YouTube auto-plays muted; sending `dispatch_keys ["k"]` un-pauses; sending `["m"]` toggles mute. Setting `video.muted = false` directly through CDP works after the first user gesture has been issued in that page session.

## Browser Use vs Computer Use on the same browser window

| Question | Choose |
|----------|--------|
| Click an element identified by a CSS selector | Browser Use |
| Click a window control (close/minimise/zoom) | Computer Use |
| Click an arbitrary `<button>` inside a webpage | Browser Use |
| Click an item in the Arc command palette / sidebar | Computer Use (palette is native, not DOM) |
| Read the visible text of an article | `browser_page_extract_content` |
| Read the page title bar | `get_app_state(app)` from Computer Use |
| Pause a YouTube video without pulling focus | `browser_dispatch_keys` with `["k"]` |

If the page UI element really is in DOM, Browser Use beats Computer Use on every axis: precision (selector vs hit-test), background-safety (extension does not need the window foregrounded), and reproducibility (selectors survive screen layout changes that hit coordinates can't).

## Hand-off

- **Real-browser context to agent**: `get_browser_url` + `get_browser_tabs` (perception, cross-platform) tell the agent which page the user is on. From there, switch to Browser Use to act on that page.
- **Browser to system**: `browser_get_selected_text` for highlighted prose; or use system `get_clipboard` (perception) after a `Cmd+C` style copy.
- **System to browser**: `browser_set_cookie` / `browser_clipboard_write_text` for one-shot pushes; or pass URLs to `browser_page_navigate`.
