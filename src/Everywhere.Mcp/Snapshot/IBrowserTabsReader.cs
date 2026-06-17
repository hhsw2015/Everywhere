namespace Everywhere.Mcp.Snapshot;

public sealed record BrowserTab(string Title, string Url, bool IsActive);

public enum BrowserTabsStatus
{
    /// <summary>App isn't a recognised browser; nothing to read.</summary>
    NotSupported,
    /// <summary>App is supported but permission denied / scripting blocked.</summary>
    PermissionDenied,
    /// <summary>Tabs returned successfully (possibly empty).</summary>
    Ok,
}

public sealed record BrowserTabsResult(BrowserTabsStatus Status, IReadOnlyList<BrowserTab> Tabs, string? ErrorMessage = null);

/// <summary>
/// Returns all tabs of a browser app (not just the focused one). macOS uses
/// per-browser AppleScript dictionaries (Safari / Chrome / Arc / Brave / Edge).
/// </summary>
public interface IBrowserTabsReader
{
    BrowserTabsResult GetTabs(string appKey);
}

internal sealed class NullBrowserTabsReader : IBrowserTabsReader
{
    public BrowserTabsResult GetTabs(string appKey) => new(BrowserTabsStatus.NotSupported, []);
}
