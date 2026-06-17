namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Tunables mirrored verbatim from upstream <c>iFurySt/open-codex-computer-use</c>.
/// Do not adjust without re-validating against upstream fixture tests
/// (see <c>tests/Everywhere.Mcp.Tests/Snapshot</c>).
/// </summary>
public static class UpstreamConstants
{
    public const int AccessibilityTreeMaxNodeCount = 1200;
    public const int AccessibilityTreeMaxDepth = 64;
    public const int ScreenshotResultMaxPngBytes = 900_000;
    public const int ScreenshotResultMaxDimension = 1280;
    public const double ScreenshotResultMinScale = 0.25;
    public const int SnapshotTextDefaultCharacterLimit = 500;
    public static readonly TimeSpan WindowVisibilityRecoveryDelay = TimeSpan.FromMilliseconds(700);
    public const int MaxKeyboardUnicodeChunkLength = 64;
    public static readonly TimeSpan FocusActivateDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan FocusAxRaiseDelay = TimeSpan.FromMilliseconds(120);
}
