# Troubleshooting

## First checks

```sh
# Is the MCP server up?
lsof -nP -iTCP:7878 -sTCP:LISTEN

# Is the OCCU dylib bundled and loaded?
ls /Applications/Everywhere.app/Contents/MonoBundle/libAxHelper.dylib
```

If port 7878 isn't listening, Everywhere isn't running or its MCP transport failed to bind. Check stderr for `[occu] backend ...` lines on launch.

## App not found

```
list_apps                         ← see what process keys exist
get_app_context(app_hint)         ← fuzzy match; better than guessing the exact key
```

Confirm the target app is running and has a visible (not minimised) window. macOS hides apps with no top-level window from a11y enumeration.

## Empty / missing snapshot

Common causes:
- The app's window is minimized, hidden, on a different Space, or behind another opaque window
- macOS Accessibility permission is missing (System Settings → Privacy & Security → Accessibility → Everywhere on)
- macOS Screen Recording permission is missing (only required for screenshot tools)
- The app's a11y tree is genuinely empty (some Electron / browser web views report nothing until they get focus)

Ask the user to bring the window into a visible state when automation can't safely do so.

## Truncated text

`get_app_state` truncates per-node text at 500 characters by default. If a chat message, email body, or document paragraph ends with `...`:

```
get_app_state(app, show_full_text: true)
```

`show_full_text` only relaxes per-node text. It does not change tree depth, screenshot size, or permission requirements.

## Element action fails

If `click` / `set_value` / `perform_secondary_action` returns isError:

1. Re-run `get_app_state` — index may have expired after a navigation or modal change.
2. Check `Secondary Actions:` on the element row to see what AXActions it actually exposes; pick from there.
3. If the element is a SwiftUI gesture button that ignores AXPress, fall back to coordinate `click` at its centre.
4. For text inputs that reject `set_value`: focus the element, then `press_key("super+a")` + `press_key("BackSpace")` + `type_text(new_value)`.

## Cursor renders upside-down, off-target, or in the wrong spot

The soft cursor is OCCU's `SoftwareCursorOverlay` running inside our Avalonia-hosted .NET process. Three downstream patches keep it aligned (live in `3rd/everywhere-patches/`):

- **0001 isFlipped + clamp** — overrides `SoftwareCursorView.isFlipped` so the y axis matches our render surface, and removes OCCU's "keep entire 126x126 sprite inside visibleFrame" clamp so the tip sticks to the click target instead of being dragged ~20px back near screen edges.
- **0003 offscreen fallback** — when a click target lands a few pixels past the screen bezel (Calculator's history sidebar pushes its right column to x=1737 on a 1728-wide main screen), `screenStatePointToAppKitGlobalPoint`'s strict `contains` lookup misses and skips the y-flip; the patch falls back to the nearest screen so coordinates outside the rect still convert correctly.

If the cursor visibly drifts in a new app, suspect the same family of issues: AppKit coord-system assumptions, a screen-edge case OCCU's clamp didn't anticipate, or a layout where cached snapshot frames diverge from live AX bounds. Capture screen-state vs AppKit numbers before touching the patches.

## Tool only available on macOS

Computer Use requires the OCCU Swift backend. The vendored library is macOS-only. Windows and Linux builds currently return:

```
<tool_name>: native UI automation is only available on macOS in this build.
```

Perception, Browser Use, clipboard, browser-url, and screenshot tools still work on all platforms.

OCCU upstream does have Windows (UI Automation) and Linux (AT-SPI2) implementations packaged separately in its npm CLI. Adding cross-platform Computer Use to Everywhere is mostly a matter of wrapping that CLI as a stdio sub-process and registering an alternate `IAxBridgeBackend` for non-macOS hosts. Not yet wired in this build.

## EVERYWHERE_USE_OCCU=0

Setting this env to `0` / `false` / `off` / `no` disables OCCU registration. After that, all Computer Use tools hard-error with `OCCU AX backend not available`. This is a kill switch for diagnostic comparisons — you almost never want it set in normal use.

## Permission and safety

- Do not bypass macOS TCC prompts — let the user grant permission via the standard System Settings flow.
- Do not enable `EVERYWHERE_ALLOW_GLOBAL_POINTER_FALLBACKS=1` unless explicitly diagnosing pointer routing.
- Do not interact with password managers, banking apps, or other sensitive surfaces unless the user explicitly requests it.
- Pause and ask before submitting forms, sending messages, deleting files, purchasing, or approving anything externally visible.
