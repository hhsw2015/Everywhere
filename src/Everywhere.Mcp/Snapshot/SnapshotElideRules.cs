using System.Collections.Generic;
using Everywhere.Interop;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Line-by-line port of OCCU AccessibilitySnapshot.swift L1354-1410:
/// shouldElideNode + shouldPreserveWebAreaGenericContainer +
/// traitsAreNonDescriptiveWrapperTraits.
///
/// Elision drops generic AXGroup / AXUnknown wrappers that contribute
/// no useful info (no title/label/value/id/actions, only wrapper-shape
/// traits) so the snapshot tree doesn't bloat into nested-empty-group
/// chains. Web areas are special-cased: AXGroup with children inside
/// an AXWebArea is preserved when childCount > 1 because DOM structure
/// is what the agent navigates by.
/// </summary>
internal static class SnapshotElideRules
{
    /// <summary>Returns true when this node should be skipped from
    /// rendered output (kept in the index map for parent lookups).</summary>
    public static bool ShouldElide(
        VisualElementType type,
        string? name,
        string? text,
        VisualElementStates states,
        IReadOnlyList<string>? actions,
        int childCount,
        int? webAreaDepth)
    {
        // Only generic wrappers participate; everything else stays.
        if (type is not VisualElementType.Panel and not VisualElementType.Unknown)
            return false;

        // Inside an AXWebArea, preserve any container with >1 children
        // so the DOM topology is readable (OCCU L1398).
        if (ShouldPreserveWebAreaGenericContainer(childCount, webAreaDepth))
            return false;

        var hasName = !string.IsNullOrEmpty(name);
        var hasText = !string.IsNullOrEmpty(text);
        var hasActions = actions is { Count: > 0 };
        var hasState = states != VisualElementStates.None;

        // Single-child wrapper with nothing identifying — collapse.
        if (childCount == 1 && !hasName && !hasText && !hasActions && AreWrapperTraits(states))
            return true;

        // Empty wrapper with nothing identifying — collapse.
        return !hasName && !hasText && !hasActions && !hasState;
    }

    public static bool ShouldPreserveWebAreaGenericContainer(int childCount, int? webAreaDepth)
        => childCount > 0 && webAreaDepth is not null && childCount > 1;

    /// <summary>OCCU traitsAreNonDescriptiveWrapperTraits — empty list
    /// or only the "settable/string" pair (a textfield-shaped wrapper).
    /// We approximate at the bit level: clear == wrapper, anything else
    /// counts as descriptive.</summary>
    private static bool AreWrapperTraits(VisualElementStates states)
        => states == VisualElementStates.None;
}
