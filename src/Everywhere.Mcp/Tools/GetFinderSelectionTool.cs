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
        "Return the user's current Finder selection with absolute POSIX paths as JSON. " +
        "Status field: \"ok\" (data returned, possibly empty) / \"not_supported\" / " +
        "\"permission_denied\" (user must grant Apple Events to Finder once via " +
        "System Settings → Privacy & Security → Automation).")]
    public static CallToolResult GetFinderSelection(IFinderReader reader)
    {
        try
        {
            var r = reader.GetSelection();
            var statusStr = r.Status switch
            {
                FinderStatus.Ok => "ok",
                FinderStatus.PermissionDenied => "permission_denied",
                _ => "not_supported",
            };

            if (r.Status != FinderStatus.Ok || r.Selection is null)
            {
                return Json(new
                {
                    status = statusStr,
                    error = r.ErrorMessage,
                    selected = false,
                    count = 0,
                    files = Array.Empty<object>(),
                });
            }

            var sel = r.Selection;
            return Json(new
            {
                status = statusStr,
                selected = sel.Files.Count > 0,
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
            Content = [new TextContentBlock
            {
                Text = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                }),
            }],
        };
}
