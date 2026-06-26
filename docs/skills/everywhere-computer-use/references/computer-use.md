# Computer Use Reference

The nine MCP tools that drive native macOS apps. Powered by the OpenComputerUseKit Swift bridge, dynamically loaded as `libAxHelper.dylib`. All requests run on the AppKit main thread; multi-second hangs from the previous .NET-only path no longer occur.

## Tool surface

| Tool | Purpose |
|------|---------|
| `list_apps` | Every running app with at least one top-level window. |
| `get_app_state(app, show_full_text?)` | Snapshot the largest visible window: indexed a11y tree. |
| `click(app, element_index? \| x?, y?, click_count?, mouse_button?)` | Click by element index (preferred) or screen coordinates. |
| `perform_secondary_action(app, element_index, action)` | Invoke any AXAction the element exposes (`AXPress`, `AXShowMenu`, `AXIncrement`, `AXRaise`, ...). Aliases supported: `press`/`click`→`AXPress`, `context_menu`/`right_click`→`AXShowMenu`. |
| `scroll(app, element_index, direction, pages?)` | Scroll an indexed scroll container. Direction: `up`/`down`/`left`/`right`. Default 1 page. |
| `drag(app, from_x, from_y, to_x, to_y)` | Press → drag → release in screen coordinates. |
| `type_text(app, text)` | Type literal text into the focused control. |
| `press_key(app, key)` | xdotool-style key spec: `a`, `Return`, `Tab`, `Escape`, `super+c`, `super+shift+n`, `KP_0`. |
| `set_value(app, element_index, value)` | Replace the text/value of an editable control via AX SetValue. |

## Core workflow

```
list_apps
  ↓ pick app name or bundle id
get_app_state(app)
  ↓ inspect indexed tree
click / set_value / scroll / press_key / type_text  (use the index)
  ↓
get_app_state(app)   ← re-snapshot after navigation or modal change
```

## Picking targets

- Use the `app` field from `list_apps` output (process key) as the `app` argument everywhere else.
- For ambiguity (`"Notes"` could mean Apple Notes vs another app), prefer the bundle identifier (`com.apple.Notes`).
- `get_app_context(app_hint)` (perception) is the better one-shot when the user gives an inexact name — it does list + match + snapshot.

## Element actions vs coordinate actions

Prefer element-targeted whenever the tree exposes the target:

```
click(app, element_index="14")            ← preferred
click(app, x=520, y=380)                  ← only when no index works
```

Coordinate clicks are sent via targeted `CGEventPostToPid` and don't bring the window to the foreground. Set `EVERYWHERE_ALLOW_GLOBAL_POINTER_FALLBACKS=1` only when diagnosing why a targeted post is being dropped (e.g. a SwiftUI gesture rejecting non-foreground events). Don't ship that env flag in normal use.

## Right-click and double-click

```
click(app, element_index="14", mouse_button="right")        ← context menu (uses AXShowMenu when available)
click(app, element_index="14", click_count=2)               ← double click
```

`mouse_button` defaults to `"left"`; `click_count` defaults to 1. Right-click on an element with no AXShowMenu falls back to a coordinate right-click at the element center.

## Secondary actions

`get_app_state` lists each element's available AXActions in the `Secondary Actions:` field. Pass any of them through `perform_secondary_action`:

```
perform_secondary_action(app, element_index="0", action="AXRaise")          ← bring window forward
perform_secondary_action(app, element_index="14", action="AXShowMenu")      ← open context menu
perform_secondary_action(app, element_index="9", action="AXIncrement")      ← step a slider/stepper
```

Common name aliases the bridge resolves: `press` / `click` → `AXPress`, `context_menu` / `right_click` → `AXShowMenu`.

## Replace vs type

`type_text` simulates keystrokes — selection state during typing is fragile and the new text often *appends* rather than *replaces* the highlighted range. For "clear and write fresh", use `set_value` on the editable element directly:

```
set_value(app, element_index="2", value="Replacement text")
```

Some web inputs (Stripe, Cloudflare, certain Electron password fields) reject scripted SetValue. There the fallback is: focus the element, then `press_key("super+a")`, then `press_key("BackSpace")`, then `type_text`. The bridge does not auto-fall-back — sequence those calls explicitly so the agent stays in control.

## Show-full-text

Tree text is truncated at 500 chars per node by default. Pass `show_full_text: true` when the task needs document body / email body / chat scrollback verbatim. The flag only relaxes per-node text; it does not change tree depth or screenshot size.

## Foreground vs background

Element-index actions don't require the window to be foreground; they go through targeted events. Some operations still need the app frontmost — typically system-level shortcuts (`Cmd+Shift+N` in Finder) and keyboard-only menu items. If a `press_key` no-ops while the app is in the background, activate first:

```
press_key(app, "super+shift+n")   ← may be ignored when app is not frontmost
# either bring the window forward via perform_secondary_action(... AXRaise)
# or use AppleScript / system activation outside this skill
```

## Errors

The bridge surfaces failures as `(text, isError=true)`. Common shapes:

- `app not found` → run `list_apps`, retry with the exact name/bundle id.
- `element_index <n> out of range` → re-snapshot, indices expire after each fresh `get_app_state`.
- `OCCU AX backend not available` → the dylib didn't load. On macOS, ensure `EVERYWHERE_USE_OCCU` is not set to `0` and that `libAxHelper.dylib` is in `Contents/MonoBundle/`. There is no longer a C# fallback for these tools.
