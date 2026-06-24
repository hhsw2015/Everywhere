using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Port of OCCU AccessibilitySnapshot.swift L1499-1525 —
/// shouldMergeTextOnlySiblings + isSiblingCounterText +
/// isStandaloneTimeRangeText.
///
/// Successive AXStaticText siblings are noise when they're a
/// short paragraph that breaks across line boundaries; OCCU
/// merges them into a single rendered line. But certain shapes
/// must NOT be merged because they carry meaning per node:
///   - "1 / 12"  counters (X of N)
///   - "09:00 - 10:30"  time ranges
///   - "日期" + "时间" header pair (matches OCCU verbatim)
///   - more than 8 siblings in one merge group
///   - total length > 220 characters
/// </summary>
internal static class SnapshotMergeRules
{
    private static readonly Regex CounterPattern =
        new(@"^\d+\s*/\s*\d+$", RegexOptions.Compiled);
    private static readonly Regex TimeRangePattern =
        new(@"^\d{1,2}:\d{2}\s*-\s*\d{1,2}:\d{2}$", RegexOptions.Compiled);

    public static bool ShouldMerge(IReadOnlyList<string> texts)
    {
        if (texts is null || texts.Count == 0) return false;

        var hasDate = false;
        var hasTime = false;
        var totalLen = 0;
        foreach (var t in texts)
        {
            if (t == "日期") hasDate = true;
            if (t == "时间") hasTime = true;
            if (CounterPattern.IsMatch(t)) return false;
            if (TimeRangePattern.IsMatch(t)) return false;
            totalLen += t.Length;
        }
        if (hasDate && hasTime) return false;
        return texts.Count <= 8 && totalLen <= 220;
    }
}
