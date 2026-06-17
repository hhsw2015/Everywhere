using System.ComponentModel;
using System.Text.Json;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetFinderSelectionTool
{
    [McpServerTool(Name = "get_finder_selection", ReadOnly = true)]
    [Description(
        "Return the user's current Finder selection with absolute POSIX paths as JSON " +
        "{\"selected\": bool, \"count\": int, \"current_folder\": string|null, " +
        "\"files\": [{\"path\": str, \"name\": str, \"is_dir\": bool}, ...]}. " +
        "Use when the user references files/folders they have selected in Finder. " +
        "On macOS this requires the user to grant Apple Events to Finder once.")]
    public static CallToolResult GetFinderSelection(IFinderReader reader)
    {
        try
        {
            var sel = reader.GetSelection();
            if (sel is null || sel.Files.Count == 0)
            {
                return Json(new
                {
                    selected = false,
                    count = 0,
                    current_folder = sel?.CurrentFolder,
                    files = Array.Empty<object>(),
                });
            }
            return Json(new
            {
                selected = true,
                count = sel.Files.Count,
                current_folder = sel.CurrentFolder,
                files = sel.Files.Select(f => new
                {
                    path = f.Path,
                    name = f.Name,
                    is_dir = f.IsDirectory,
                }).ToArray(),
            });
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "get_finder_selection");
        }
    }

    private static CallToolResult Json(object payload) =>
        new()
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
        };
}
