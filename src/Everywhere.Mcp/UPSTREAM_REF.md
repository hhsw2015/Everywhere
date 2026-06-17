# Upstream reference

Source: https://github.com/iFurySt/open-codex-computer-use

Pinned commit: `<sha-to-be-pinned>`
Last synced: 2026-06-17

This project is a strict superset of the upstream Computer Use tools.
Tool names, JSON schemas, and behavioral contracts must remain byte-compatible
so existing client configs and test corpora apply unchanged.

## File mapping

| Upstream | Mirror |
|----------|--------|
| packages/OpenComputerUseKit/Sources/OpenComputerUseKit/InputSimulation.swift | Input/Mac/MacInputSimulator.cs |
| packages/OpenComputerUseKit/Sources/OpenComputerUseKit/KeyMapping.swift | Input/KeyMapping.cs |
| packages/OpenComputerUseKit/Sources/OpenComputerUseKit/AccessibilitySnapshot.swift | Snapshot/AppSnapshot.cs |
| packages/OpenComputerUseKit/Sources/OpenComputerUseKit/Tools/* | Tools/* |
| packages/OpenComputerUseKit/Sources/OpenComputerUseKit/MCP/Server.swift | Server/EverywhereMcpServer.cs |

When porting any function, prepend a header comment:

```
// Upstream: iFurySt/open-codex-computer-use@<sha>
//   <relative-path>:<line>
```

## Tunables (mirrored verbatim)

```
accessibilityTreeMaxNodeCount   = 1200
accessibilityTreeMaxDepth       = 64
screenshotResultMaxPNGBytes     = 900_000
screenshotResultMaxDimension    = 1280
screenshotResultMinScale        = 0.25
snapshotTextDefaultCharacterLimit = 500
windowVisibilityRecoveryDelay   = 0.7s
maxKeyboardUnicodeChunkLength   = 64
focusActivateDelay              = 0.25s
focusAxRaiseDelay               = 0.12s
```

## Resync procedure

1. Bump the pinned SHA above.
2. Re-run `tools/upstream-tools-list-snapshot.sh` to refresh
   `tests/Everywhere.Mcp.Tests/Fixtures/upstream-tools-list.json`.
3. Re-run `tests/Everywhere.Mcp.Tests` (`dotnet test`).
4. Update file-mapping table for any added / renamed upstream files.
