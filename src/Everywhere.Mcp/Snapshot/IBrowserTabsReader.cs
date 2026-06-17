namespace Everywhere.Mcp.Snapshot;

public sealed record BrowserTab(string Title, string Url, bool IsActive);

/// <summary>
/// Returns all tabs of a browser app (not just the focused one). macOS uses
/// per-browser AppleScript dictionaries (Safari / Chrome / Arc / Brave / Edge);
/// returns null when the app isn't a recognised browser or scripting is denied.
/// </summary>
public interface IBrowserTabsReader
{
    IReadOnlyList<BrowserTab>? GetTabs(string appKey);
}

internal sealed class NullBrowserTabsReader : IBrowserTabsReader
{
    public IReadOnlyList<BrowserTab>? GetTabs(string appKey) => null;
}
