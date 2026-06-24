using Avalonia;
using Everywhere.Interop;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// Click-target post-processing rules ported from open-codex-computer-use's
/// ComputerUseService.swift. The agent picked element_index N; before we
/// actually invoke it we ask: does the target look like a small trailing
/// "side action" button (Done / 完成 / Archive) crammed next to a primary
/// row? Or a synthetic-text child inside an Electron app's row view?
/// In both cases the parent row is what the user (and the agent) almost
/// certainly meant. We redirect the click target one level up.
///
/// These are heuristics, not invariants — when in doubt, leave the target
/// alone. Each branch is intentionally narrow.
/// </summary>
internal static class ClickHeuristics
{
    private static readonly string[] SideActionLabels =
    [
        "done", "complete", "完成", "archive",
    ];

    /// <summary>
    /// Returns a different element to click when the original target looks
    /// like a synthetic side-action or an Electron-web descendant whose
    /// containing row carries the real Press. Otherwise returns the
    /// original element.
    /// </summary>
    public static IVisualElement RedirectIfNeeded(IVisualElement target, string? processName = null, string? bundleIdentifier = null)
    {
        // Web-row prefer: small clickable inside Lark/Feishu/Electron app
        // row → walk up at most 4 levels looking for a row-shaped ancestor
        // (TableRow / ListViewItem / Panel matching row geometry) that is
        // larger than us and clickable.
        if (IsElectronScopedWebApp(processName, bundleIdentifier))
        {
            var rowAncestor = FindContainingRowAncestor(target);
            if (rowAncestor is not null) return rowAncestor;
        }

        // Synthetic-side filter: target is a small trailing labelled button
        // sharing a parent with a primary action. Prefer the parent.
        if (IsLikelySyntheticSideAction(target))
        {
            var parent = target.Parent;
            if (parent is not null) return parent;
        }

        return target;
    }

    /// <summary>
    /// Mirrors OCCU isElectronScopedWebRowClickOptimizationTarget. Electron
    /// shells route every row through a synthetic-text DOM that AX
    /// surfaces as a flat child list — Press lives on the ancestor.
    /// </summary>
    private static bool IsElectronScopedWebApp(string? processName, string? bundleIdentifier)
    {
        var bid = bundleIdentifier?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(bid))
        {
            if (bid.StartsWith("com.electron.", StringComparison.Ordinal)) return true;
            if (bid.Contains(".electron.", StringComparison.Ordinal)) return true;
            if (bid.Contains("lark", StringComparison.Ordinal)) return true;
            if (bid.Contains("feishu", StringComparison.Ordinal)) return true;
        }

        var name = processName?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name)) return false;
        return name is "lark" or "feishu" or "飞书";
    }

    /// <summary>
    /// Walk up to 4 ancestors looking for one whose bounding rect
    /// contains us, is meaningfully wider, and looks row-shaped. OCCU's
    /// shouldPreferContainingWebRowAXClickCandidate distilled down to
    /// shapes we can check with the data IVisualElement actually
    /// exposes.
    /// </summary>
    private static IVisualElement? FindContainingRowAncestor(IVisualElement target)
    {
        var targetRect = target.BoundingRectangle;
        if (targetRect.Width <= 0 || targetRect.Height <= 0) return null;
        var targetCenter = new Point(targetRect.X + targetRect.Width / 2.0, targetRect.Y + targetRect.Height / 2.0);

        var cursor = target.Parent;
        for (var hop = 0; cursor is not null && hop < 4; hop++)
        {
            var rect = cursor.BoundingRectangle;
            if (rect.Width > 0 && rect.Height > 0
                && rect.Contains(new Avalonia.Platform.PixelPoint((int)targetCenter.X, (int)targetCenter.Y))
                && rect.Width >= targetRect.Width
                && rect.Height >= targetRect.Height
                && rect.Height <= Math.Max(targetRect.Height + 32, targetRect.Height * 2))
            {
                if (LooksLikeClickableRow(cursor)) return cursor;
            }
            cursor = cursor.Parent;
        }
        return null;
    }

    private static bool LooksLikeClickableRow(IVisualElement el) => el.Type switch
    {
        VisualElementType.TableRow
            or VisualElementType.ListViewItem
            or VisualElementType.TreeViewItem
            or VisualElementType.DataGridItem
            or VisualElementType.Button
            or VisualElementType.Panel => true,
        _ => false,
    };

    /// <summary>
    /// OCCU isLikelySyntheticSideActionCandidate distilled: this element is
    /// (a) labelled like a side-action ("Done"/"完成"/"Archive"/"Mark complete")
    /// AND (b) compact (small width + height) AND (c) the parent has at least
    /// one sibling button that is wider — the wider sibling is the primary
    /// action and we got the side one by mistake.
    /// </summary>
    private static bool IsLikelySyntheticSideAction(IVisualElement target)
    {
        var label = target.Name?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(label)) return false;

        var hasSideLabel = SideActionLabels.Any(l => label == l)
            || (label.Length <= 24 && (
                label.Contains("完成", StringComparison.Ordinal)
                || (label.Contains("mark", StringComparison.Ordinal)
                    && (label.Contains("done", StringComparison.Ordinal) || label.Contains("complete", StringComparison.Ordinal)))));

        if (!hasSideLabel) return false;

        var parent = target.Parent;
        if (parent is null) return false;
        var parentRect = parent.BoundingRectangle;
        var targetRect = target.BoundingRectangle;
        if (parentRect.Width <= 0 || targetRect.Width <= 0) return false;

        // Trailing band: right 22% (clamped 56..140 px) of the parent.
        var trailingBand = Math.Min(Math.Max(parentRect.Width * 0.22, 56), 140);
        var isTrailing = (targetRect.X + targetRect.Width / 2.0) >= (parentRect.X + parentRect.Width - trailingBand);

        var compactWidth = targetRect.Width <= Math.Max(88, parentRect.Width * 0.18);
        var compactHeight = targetRect.Height <= Math.Max(44, parentRect.Height * 1.2);
        if (!compactWidth || !compactHeight) return false;

        // Either trailing+compact OR has another wider primary-action sibling.
        if (isTrailing) return true;

        foreach (var sibling in parent.Children)
        {
            if (ReferenceEquals(sibling, target)) continue;
            if (sibling.Type != VisualElementType.Button) continue;
            var sRect = sibling.BoundingRectangle;
            if (sRect.Width > targetRect.Width * 1.4) return true;
        }
        return false;
    }
}
