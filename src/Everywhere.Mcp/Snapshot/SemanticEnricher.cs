using Everywhere.Mcp.Tools.Schemas;

namespace Everywhere.Mcp.Snapshot;

internal static class SemanticEnricher
{
    public static void Apply(AppStateResult target, IReadOnlyList<ElementIndexer.IndexedNode> nodes)
    {
        var sel = SemanticExtractor.ExtractSelected(nodes);
        if (sel.Count > 0) target.SelectedItems = sel;

        var foc = SemanticExtractor.ExtractFocused(nodes);
        if (foc.Count > 0) target.FocusedItems = foc;

        var path = SemanticExtractor.BuildFocusedPath(nodes);
        if (path.Count > 0) target.FocusedPath = path;
    }

    public static void Apply(FocusedContextResult target, IReadOnlyList<ElementIndexer.IndexedNode> nodes)
    {
        var sel = SemanticExtractor.ExtractSelected(nodes);
        if (sel.Count > 0) target.SelectedItems = sel;

        var foc = SemanticExtractor.ExtractFocused(nodes);
        if (foc.Count > 0) target.FocusedItems = foc;

        var path = SemanticExtractor.BuildFocusedPath(nodes);
        if (path.Count > 0) target.FocusedPath = path;
    }
}
