namespace Everywhere.Mcp.Input;

/// <summary>
/// Read-only access to the system clipboard. Platform projects supply real impl; the
/// stdio fallback returns null so the tool reports an empty clipboard rather than
/// throwing.
/// </summary>
public interface IClipboardReader
{
    /// <summary>Returns the current clipboard text, or null if none / not text.</summary>
    string? GetText();
}

internal sealed class NullClipboardReader : IClipboardReader
{
    public string? GetText() => null;
}

/// <summary>
/// Write-side counterpart for clipboard mutation. Platform projects supply real
/// impl; the stdio fallback no-ops so tools degrade gracefully on non-macOS hosts.
/// </summary>
public interface IClipboardWriter
{
    /// <summary>Replace the clipboard contents with the given text.</summary>
    void SetText(string text);
    /// <summary>Clear the clipboard.</summary>
    void Clear();
}

internal sealed class NullClipboardWriter : IClipboardWriter
{
    public void SetText(string text) { }
    public void Clear() { }
}
