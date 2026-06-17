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
