# Troubleshooting

## Sanity checks

```sh
lsof -nP -iTCP:7878 -sTCP:LISTEN                                        # MCP server up?
ls /Applications/Everywhere.app/Contents/MonoBundle/libAxHelper.dylib   # OCCU dylib bundled?
```

## App not found

```
list_apps                       # exact process keys
get_app_context(app_hint)       # fuzzy match
```

App must be running with a visible (non-minimized) window.

## Empty / missing snapshot

- Window minimised / on different Space / occluded.
- Accessibility permission missing (System Settings → Privacy → Accessibility → Everywhere on).
- Screen Recording permission missing (only required for screenshot tools).
- Some Electron / WebView apps report empty until focused — use `perform_secondary_action(idx="0", action="AXRaise")`.

## Truncated text

`get_app_state(app, show_full_text=true)`. Per-node only — does not change tree depth or include images.

## Element action fails

1. Re-snap (`get_app_state`) — indices expire.
2. Check element's `Secondary Actions:` list, pick the right AXAction.
3. SwiftUI gesture button refusing AXPress → coordinate `click` at element center.
4. set_value refused → click + `super+a` + `BackSpace` + `type_text`.

## Browser Use errors

- `evaluate_js requires Function-from-string, which MV3 + page CSP forbid` → Use `browser_dom_query` / `browser_dispatch_keys`, or `browser_cdp_evaluate`.
- `keys array required` → `keys: ["k"]` not `keys: "k"`.
- Tab id stale → re-`browser_tab_list`.
- Extension not installed / not granted → tool returns clear failure; surface to user, do not silently fall through to Computer Use.

## Cursor renders off-target

OCCU's cursor overlay is shipped via 2 downstream patches in `3rd/everywhere-patches/`:
- `0001-cursor-isFlipped.patch` — y-flip + drop sprite-bounds clamp (tip stays glued to target near screen edges).
- `0003-cursor-offscreen-fallback.patch` — `screenStatePointToAppKitGlobalPoint` falls back to nearest screen when point is past the bezel.

If cursor visibly drifts in a new app, capture screen-state vs AppKit numbers before patching further.

## "Tool only available on macOS"

Computer Use needs the OCCU Swift backend. macOS only in this build. Browser Use + Perception still work cross-platform.

OCCU upstream has Windows (UI Automation) and Linux (AT-SPI2) backends in its npm CLI; not yet wired here.

## EVERYWHERE_USE_OCCU=0

Kill switch. Set `0` / `false` / `off` / `no` to disable OCCU registration; automation tools then hard-error. Diagnostic only — don't ship enabled.

## Permission / safety

- Don't bypass macOS TCC prompts.
- Don't enable `EVERYWHERE_ALLOW_GLOBAL_POINTER_FALLBACKS=1`.
- Don't interact with password managers / banking / sensitive surfaces unless user explicitly asks.
- Pause before send / delete / submit / approve / purchase / upload.
