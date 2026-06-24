// Line-for-line port of OCCU AccessibilitySnapshot.swift L1682-1693
// (sanitizeText). Used everywhere the snapshot emits user-visible
// strings — labels, descriptions, row text, placeholders.

namespace Everywhere.Mcp.Snapshot;

internal static class SnapshotTextUtil
{
    public const int DefaultCharacterLimit = 240;

    /// <summary>
    /// Collapse newlines to literal "\n", trim whitespace at both
    /// ends, optionally truncate with an ellipsis. Mirrors OCCU
    /// sanitizeText so an agent reading our output sees the same
    /// shape it would from upstream.
    /// </summary>
    public static string Sanitize(string? value, int? characterLimit = DefaultCharacterLimit)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // Trim *first* (so leading/trailing newlines are dropped, not
        // escaped to literal "\n"), then escape interior newlines.
        var collapsed = value.Trim().Replace("\r\n", "\\n").Replace("\n", "\\n");
        if (characterLimit is int limit && limit >= 0 && collapsed.Length > limit)
        {
            // Don't split a UTF-16 surrogate pair — emoji / supplementary-
            // plane CJK are common in accessibility labels and an orphan
            // high-surrogate would render as a replacement char.
            if (limit > 0 && char.IsHighSurrogate(collapsed[limit - 1])) limit--;
            return collapsed[..limit] + "...";
        }
        return collapsed;
    }
}
