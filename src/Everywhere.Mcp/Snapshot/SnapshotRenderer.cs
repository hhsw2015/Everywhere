using System.Text;
using Everywhere.Interop;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Renders the indexed tree into the indented text shape expected by upstream
/// <c>get_app_state</c> consumers. Format:
/// <code>
/// [42] Button "Submit" (bounds=0,0,100,30) [enabled, focused]
///   [43] Image (bounds=10,5,16,16)
/// </code>
/// Indentation is two spaces per depth level; per-node text is truncated at
/// <see cref="UpstreamConstants.SnapshotTextDefaultCharacterLimit"/> chars unless
/// <c>showFullText=true</c>.
/// </summary>
public static class SnapshotRenderer
{
    public static string Render(IReadOnlyList<ElementIndexer.IndexedNode> nodes, bool showFullText)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var sb = new StringBuilder();
        foreach (var node in nodes)
        {
            sb.Append(' ', node.Depth * 2);
            sb.Append('[').Append(node.Index).Append("] ");
            sb.Append(node.Element.Type);

            var name = node.Element.Name;
            if (!string.IsNullOrEmpty(name))
            {
                sb.Append(' ').Append('"').Append(EscapeQuotes(name)).Append('"');
            }

            var text = node.Element.GetText(maxLength: showFullText ? -1 : UpstreamConstants.SnapshotTextDefaultCharacterLimit);
            if (!string.IsNullOrEmpty(text) && text != name)
            {
                sb.Append(" text=\"").Append(EscapeQuotes(text)).Append('"');
            }

            var bounds = node.Element.BoundingRectangle;
            sb.Append(" (bounds=").Append(bounds.X).Append(',').Append(bounds.Y)
              .Append(',').Append(bounds.Width).Append(',').Append(bounds.Height).Append(')');

            var states = node.Element.States;
            if (states != VisualElementStates.None)
            {
                sb.Append(" [").Append(states.ToString().Replace(", ", ",")).Append(']');
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeQuotes(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("\"", "\\\"")
         .Replace("\r", "\\r")
         .Replace("\n", "\\n")
         .Replace("\t", "\\t");
}
