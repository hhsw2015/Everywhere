namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Resolves the URL of a browser app's currently visible tab. macOS implementation
/// reads <c>AXURL</c> from the focused web area; non-Mac fallback returns null.
/// </summary>
public interface IBrowserUrlReader
{
    /// <summary>
    /// Returns the URL the browser app is currently displaying, or null if the resolved
    /// app isn't a known browser or AX exposes no URL.
    /// </summary>
    string? GetUrl(int processId);
}

internal sealed class NullBrowserUrlReader : IBrowserUrlReader
{
    public string? GetUrl(int processId) => null;
}
