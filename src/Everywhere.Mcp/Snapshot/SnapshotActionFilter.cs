using System.Collections.Generic;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Single source of truth for OCCU "meaningful actions" filtering. Mac
/// AX path pre-filters via AXUIElement.IsMeaningful but other platforms
/// may surface raw AX verb names; both renderers route through here so
/// JSON output and tree_text output advertise exactly the same verbs.
///
/// Mirrors OCCU meaningfulActions whitelist
/// (ComputerUseService.swift L996-1010 + AccessibilitySnapshot.swift
/// L1619). Add new verbs here, not at call sites.
/// </summary>
internal static class SnapshotActionFilter
{
    public static readonly HashSet<string> MeaningfulVerbs = new(System.StringComparer.Ordinal)
    {
        "Press", "Confirm", "Open", "ShowMenu",
        "Increment", "Decrement", "Pick", "Cancel", "Delete", "Raise",
    };

    /// <summary>
    /// Strips the "AX" prefix and applies the meaningful-verbs whitelist.
    /// Returns null/empty entries skipped, deduped order-preserved list.
    /// </summary>
    public static List<string> Filter(IReadOnlyList<string>? raw)
    {
        var result = new List<string>(capacity: 4);
        if (raw is null || raw.Count == 0) return result;
        foreach (var v in raw)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            var stripped = v.StartsWith("AX", System.StringComparison.Ordinal) ? v[2..] : v;
            if (string.IsNullOrEmpty(stripped)) continue;
            if (!MeaningfulVerbs.Contains(stripped)) continue;
            if (result.Contains(stripped)) continue;
            result.Add(stripped);
        }
        return result;
    }
}
