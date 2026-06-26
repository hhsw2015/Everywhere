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
To pick up upstream fixes:

1. `cd 3rd/open-codex-computer-use && git fetch && git checkout <new-sha>`
2. Bump the pinned SHA at the top of this file.
3. From repo root: `cd src/Everywhere.Mac.AxHelper && swift test`
4. Run `dotnet build` to rebuild the dylib + .NET layer.
5. Smoke-test with `EVERYWHERE_USE_OCCU` enabled (default on macOS).
6. Update the SKILL doc only if upstream changed tool names / JSON shape.

## Tool surface contract

Tool names and JSON shapes must stay byte-compatible with OCCU's MCP
server output (`open-computer-use mcp`). Existing agent configs and
LLM prompts assume the same shape. Any deviation goes through a SKILL
doc note and a versioned compatibility flag — never a silent rename.

## Cross-platform status

OCCU has Windows (Go + PowerShell) and Linux (Go + Python) backends
under `apps/OpenComputerUseWindows` and `apps/OpenComputerUseLinux`.
Everywhere does NOT bridge to these yet; the Mac-only Swift dylib is
the only IAxBridgeBackend implementation. Win/Linux builds therefore
return `OccuRequired` for the eight automation tools but keep all
perception tools working. See `docs/skills/everywhere-computer-use/SKILL.md`
for the user-facing message.
