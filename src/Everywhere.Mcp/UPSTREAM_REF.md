# Upstream reference

Source: https://github.com/iFurySt/open-codex-computer-use
Pinned commit: `9f1e550cb2ea788d611a421835e1563fef669cd4`
Last synced: 2026-06-26

## How OCCU is integrated (current)

On macOS, the eight Computer Use tools (`list_apps`, `get_app_state`,
`click`, `perform_secondary_action`, `scroll`, `drag`, `type_text`,
`press_key`, `set_value`) are routed through a Swift dylib that wraps
the vendored OpenComputerUseKit. Layout:

```
.NET MCP tool (src/Everywhere.Mcp/Tools/*.cs)
    ↓ resolve IAxBridgeBackend
OccuAxBridgeBackend (src/Everywhere.Mac/AxBridge/OccuAxBridgeBackend.cs)
    ↓ OccuTool wrapper
LibAxHelper P/Invoke (src/Everywhere.Mac/AxBridge/LibAxHelper.cs)
    ↓ libAxHelper.dylib (built from src/Everywhere.Mac.AxHelper/)
AxBridge.swift @_cdecl shim
    ↓ same-process Swift call
OpenComputerUseKit (3rd/open-codex-computer-use vendored as a Git submodule)
    ↓
macOS HIServices / AppKit / ScreenCaptureKit
```

Goal: zero behavioural divergence from OCCU on macOS. We ship the
upstream Swift module verbatim; only the AxBridge.swift shim and the
.NET P/Invoke layer are ours.

## What is NOT a port anymore

The earlier C# 1:1 ports of OCCU's Swift sources have been retired.
ElementClickDispatcher.cs, ClickHeuristics.cs and the per-tool C#
fallback branches were deleted in v0.9.138. AccessibilitySnapshot,
ComputerUseService, InputSimulation are no longer mirrored — each MCP
tool calls into the dylib directly.

The remaining C# code that *resembles* OCCU work
(`AXUIElement.cs`, `MacInputSimulator.cs`, `KeyMapping.cs`,
`ElementIndexer.cs`, `SnapshotRenderer.cs`, etc.) is ONLY used by
**perception tools** (`pick_element`, `read_pick`, `read_whiteboard`,
`get_focused_context`, `get_app_context`, `screenshot`, ...). OCCU
does not expose perception primitives, so there is nothing to bridge
there — those C# implementations stay.

## Files we own (do not auto-port from upstream)

- `src/Everywhere.Mac/AxBridge/*.cs` — P/Invoke + result parsing
- `src/Everywhere.Mac.AxHelper/Sources/AxHelper/AxBridge.swift` — C-ABI shim
- `src/Everywhere.Mac.AxHelper/Tests/AxHelperTests/*.swift` — bridge unit tests
- `src/Everywhere.Mcp/Tools/*Tool.cs` — thin tool wrappers that resolve `IAxBridgeBackend` and forward
- `src/Everywhere.Core/Interop/IAxBridgeBackend.cs` — backend contract

## Files vendored from upstream (do not edit)

Everything under `3rd/open-codex-computer-use/` is a pinned submodule.
Do NOT commit changes inside the submodule — they vanish on the next
`git submodule update`. Real downstream fixes live as patch files
under `3rd/everywhere-patches/` and are reapplied at build time by the
`ApplyAxHelperPatches` MSBuild target in `Everywhere.Mac.csproj`.

### Active patches

| File | Touches | Why |
|------|---------|-----|
| `0001-cursor-isFlipped.patch` | `SoftwareCursorOverlay.swift` | Overrides `SoftwareCursorView.isFlipped` to `true` (Avalonia host renders cursor upside-down without it), and removes the original `clampTipPosition` margin that kept the entire 126x126 sprite inside `visibleFrame` — that margin yanked the tip ~20px back near right edges (visible after Calculator's history sidebar opens). |
| `0003-cursor-offscreen-fallback.patch` | `ComputerUseService.swift` | `screenStatePointToAppKitGlobalPoint` requires the point to be strictly contained by some screen frame; misses fall through with no y-flip, dropping the cursor far below the target. The patch falls back to the nearest screen by squared-distance so a point a few pixels past the bezel still converts cleanly. |

Patches are idempotent: `git apply` is run with a check pass first; if
the patch is already applied (or the upstream changed) the build skips
the apply step and continues. Failure surfaces as a build warning, not
a hard error.

### Upgrading vendored OCCU

1. `cd 3rd/open-codex-computer-use && git fetch && git checkout <new-sha>`
2. Bump the pinned SHA at the top of this file (`Last synced` and `Pinned commit`).
3. `cd src/Everywhere.Mac.AxHelper && swift test` — the patches reapply
   automatically through the MSBuild target on a real `dotnet build`,
   but for a clean swift-only test run patch them manually:
   ```sh
   for p in 3rd/everywhere-patches/*.patch; do
     git -C 3rd/open-codex-computer-use apply "$p"
   done
   swift test
   git -C 3rd/open-codex-computer-use checkout -- packages/
   ```
4. `dotnet build` to rebuild the dylib + .NET layer.
5. Smoke-test with `EVERYWHERE_USE_OCCU` enabled (default on macOS).
6. If a patch no longer applies cleanly, regenerate it against the
   new upstream rather than forcing the old hunks: build OCCU at the
   new SHA with the same fix re-applied, then capture the diff with
   `git diff > 3rd/everywhere-patches/000N-...patch`.
7. Update the SKILL doc only if upstream changed tool names / JSON shape.

## Tool surface contract

Tool names and `text` content of MCP responses stay byte-compatible
with OCCU's CLI output (`open-computer-use call <tool>`). Existing
agent configs and LLM prompts assume the same shape. Verified by ad-hoc
diff against Calculator / TextEdit / Finder snapshots; any drift goes
through a SKILL doc note and a versioned compatibility flag — never a
silent rename.

### Intentional deviation: screenshot content stripped from tool responses

OCCU's `snapshotResult` (ComputerUseService.swift L1684-1689) attaches a
PNG screenshot as `content[1]` on every `get_app_state` / post-action
response. The text in `content[0]` is byte-identical to ours; the
difference is purely in the image attachment.

Everywhere intentionally drops `content[1]`:

- The cloud LLMs Everywhere routes through don't all accept image
  content blocks; `text` is the lowest common denominator.
- Image base64 is heavy (>30 KB per Calculator-sized window, much
  larger for full-screen apps) and bloats every tool turn. Tokens
  per snapshot would 5-10x.
- Users / LLMs that genuinely want a screenshot already have a
  cross-platform `screenshot` perception tool (`Everywhere.Mcp/Tools/
  ScreenshotTool.cs`) that returns PNG on demand. Coupling the
  screenshot to every snapshot is the wrong default for our deployment.

Implementation: `Everywhere.Core.Interop.IAxBridgeBackend.GetAppState`
returns `(string Text, bool IsError)` — the bridge surface deliberately
exposes only `content[0].text`. `OccuTool.Parse` walks the result's
content array, takes the first `type:"text"` block, and discards the
rest. Do NOT "fix" this by re-attaching the image; it's a design choice.

## Cross-platform status

OCCU has Windows (Go + PowerShell) and Linux (Go + Python) backends
under `apps/OpenComputerUseWindows` and `apps/OpenComputerUseLinux`.
Everywhere does NOT bridge to these yet; the Mac-only Swift dylib is
the only IAxBridgeBackend implementation. Win/Linux builds therefore
return `OccuRequired` for the eight automation tools but keep all
perception tools working. See `docs/skills/everywhere-computer-use/SKILL.md`
for the user-facing message.
