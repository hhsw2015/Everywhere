# Browser Use Reference

Drives the user's REAL browser (Arc / Chrome / Edge / Brave / any Chromium with the Everywhere extension). Real cookies, real session, real audio. Background — does not foreground the browser.

NOT a sandbox. For a sandbox use a separate skill.

## Tools

### Tabs / navigation
| Tool | Purpose |
|------|---------|
| `browser_tab_list` | Every tab: id, title, url, active, pinned, status. Start here. |
| `browser_tab_create(url?)` / `browser_tab_close(tabId)` / `browser_tab_switch(tabId)` | Tab CRUD. |
| `browser_page_navigate(tabId, url)` | Navigate the tab. |
| `browser_claim_tab(tabId)` / `browser_finalize_tabs` / `browser_name_session(name)` | Session management. |

### Read
| Tool | Purpose |
|------|---------|
| `browser_page_analyze(tabId)` | Semantic outline. Default starting point for orientation. |
| `browser_dom_query(tabId, selector, action?, attributes?)` | Single-element query. |
| `browser_dom_query_all(tabId, selector)` | List form. |
| `browser_page_extract_content(tabId)` | Readable-mode main content. |
| `browser_get_page_links(tabId)` | All anchors deduped. |
| `browser_get_selected_text(tabId)` | User's highlighted text. |
| `browser_get_cookies(tabId, domain?)` | Cookies. |
| `browser_screenshot(tabId, fullPage?)` | PNG. |
| `browser_page_style(tabId, selector)` | Computed CSS. |
| `browser_element_get_state(tabId, selector)` | visibility/disabled/value/etc. |

### Act
| Tool | Purpose |
|------|---------|
| `browser_element_click(tabId, selector)` | Click. |
| `browser_element_fill(tabId, selector, value)` | Fill input. |
| `browser_dispatch_keys(tabId, keys: ["k"])` | Keyboard event. **Array required**, not bare string. |
| `browser_page_scroll(tabId, x?, y?)` | Scroll. |
| `browser_wait_for_selector(tabId, selector, timeoutMs?)` / `browser_page_wait_for(tabId, condition)` | Sync. |
| `browser_clipboard_read_text` / `browser_clipboard_write_text` | Page-side clipboard. |

### Bookmarks / history
| Tool | Purpose |
|------|---------|
| `browser_get_bookmarks` / `browser_add_bookmark(url, title?)` | Bookmark tree. |
| `browser_get_history(query?)` | History search. |

### CDP (escape hatch)
| Tool | Purpose |
|------|---------|
| `browser_cdp_evaluate(tabId, expression)` | Evaluate JS via CDP. Use when extension-side `evaluate_js` is blocked by MV3 CSP. |
| `browser_cdp_input_mouse(tabId, x, y, type)` | Mouse at coordinates. For canvas / WebGL / shadow DOM that intercepts CSS clicks. |
| `browser_cdp_input_keys` | Lower-level keyboard. |
| `browser_cdp_list_network_requests(tabId)` / `browser_cdp_get_response_body(tabId, requestId)` | Network inspection. |
| `browser_cdp_list_console_messages(tabId)` | Console. |
| `browser_cdp_upload_file(tabId, selector, path)` | File-input upload. |

### Identity (perception, cross-platform)
| Tool | Purpose |
|------|---------|
| `get_browser_url` | URL of user's frontmost tab. NOT the agent's working tab. |
| `get_browser_tabs` | Same scope. |

## Workflow

```
get_browser_url (perception) → browser_tab_list → browser_page_analyze
  → dom_query / get_selected_text / extract_content (read)
  → element_fill / element_click / dispatch_keys (act)
  → wait_for_selector → dom_query (verify)
```

## Constraints

- **MV3 CSP blocks `Function`-from-string.** `browser_evaluate_js` returns "evaluate_js requires Function-from-string, which MV3 + page CSP forbid" on most production pages. Use `browser_dom_query` / `browser_dispatch_keys` first; fall back to `browser_cdp_evaluate`.
- **`browser_dispatch_keys` requires array.** `keys: "k"` errors "keys array required". Use `keys: ["k"]`.
- **Tab ids change** when user closes/reopens. Re-list after any nav that may spawn a new tab.
- **Audio/video gesture-gated.** YouTube auto-plays muted; `dispatch_keys ["k"]` toggles play, `["m"]` toggles mute.

## Browser Use vs Computer Use on the same browser

| Want | Use |
|------|-----|
| Click DOM element by selector | Browser Use |
| Click webpage `<button>` inside content | Browser Use |
| Pause/play media without focus | Browser Use (`dispatch_keys ["k"]`) |
| Click window control (close/minimise/zoom) | Computer Use |
| Click Arc command palette / sidebar / tab strip | Computer Use (native, not DOM) |
| Read article body | `browser_page_extract_content` |
| Read window title | Computer Use `get_app_state` |

DOM target → Browser Use beats Computer Use on precision, background-safety, reproducibility.

