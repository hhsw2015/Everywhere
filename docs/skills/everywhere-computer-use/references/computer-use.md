# Computer Use Reference

macOS native automation via OCCU Swift dylib. macOS only.

## Tools

| Tool | Args | Returns |
|------|------|---------|
| `list_apps` | – | running apps with at least one top-level window |
| `get_app_state` | `app`, `show_full_text?` | indexed a11y tree of the largest visible window |
| `click` | `app`, `element_index?` OR `x,y`, `click_count?`, `mouse_button?` | post-action snapshot |
| `perform_secondary_action` | `app`, `element_index`, `action` | post-action snapshot |
| `scroll` | `app`, `element_index`, `direction` (up/down/left/right), `pages?` | snapshot |
| `drag` | `app`, `from_x`, `from_y`, `to_x`, `to_y` | snapshot |
| `type_text` | `app`, `text` | snapshot |
| `press_key` | `app`, `key` (xdotool style: `Return`, `super+c`, `super+shift+n`, `KP_0`) | snapshot |
| `set_value` | `app`, `element_index`, `value` | snapshot |

## Workflow

```
list_apps → get_app_state(app) → use element_index in actions → re-snap if state changed
```

Indices reissue on every `get_app_state`. Re-snap before reusing.

## Picking targets

- `app` arg = process key from `list_apps`. For ambiguity prefer bundle id (`com.apple.Notes`).
- `get_app_context(app_hint)` (perception) does list+match+snapshot in one call when the name is fuzzy.

## Element vs coordinate

Prefer element-targeted. Coordinate `click(x,y)` only when nothing in the tree matches the target.

```
click(app, element_index="14")     # preferred
click(app, x=520, y=380)           # last resort
```

## Patterns

| Want | Do |
|------|-----|
| Right-click | `click(... mouse_button="right")` (uses AXShowMenu when exposed) |
| Double-click | `click(... click_count=2)` |
| Window action (raise / minimize) | `perform_secondary_action(app, element_index="0", action="AXRaise")` |
| AX action listed in element's "Secondary Actions:" | `perform_secondary_action(action="AXIncrement"\|"AXShowMenu"\|...)`; aliases: press/click→AXPress, context_menu/right_click→AXShowMenu |
| Replace text in editable | `set_value(app, element_index, value)` (do NOT type_text — selection state is fragile) |
| set_value refused (Stripe / Cloudflare / Electron) | click element + `press_key("super+a")` + `press_key("BackSpace")` + `type_text(value)` |
| Long body text | `get_app_state(app, show_full_text=true)` (default truncates 500 chars/node) |

## Foreground

Element clicks via PostToPid don't need foreground. `press_key` for system shortcuts (Cmd+Shift+N etc) sometimes does — bring app forward via `perform_secondary_action(idx="0", action="AXRaise")` if a key combo no-ops.

## No screenshot in `get_app_state`

Stripped on purpose (LLM token cost, image-block compatibility). Use `screenshot` (perception) when you need a picture.

## Errors

- "app not found" → `list_apps`, retry with exact name/bundle id
- "element_index N out of range" → indices expired, re-snap
- "OCCU AX backend not available" → on macOS check `EVERYWHERE_USE_OCCU` ≠ 0 + `libAxHelper.dylib` in `Contents/MonoBundle/`. No C# fallback exists.
