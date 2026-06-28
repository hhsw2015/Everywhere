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
        "Each file entry carries mime (best-effort from extension, e.g. application/pdf) " +
        "and kind_hint (one of: pdf/docx/xlsx/pptx/epub/html/image/text/folder/unknown). " +
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
                    mime = MimeFromExtension(f.Name, f.IsDirectory),
                    kind_hint = KindHintFromExtension(f.Name, f.IsDirectory),
                }).ToArray(),
            });
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "get_finder_selection");
        }
    }

    public static string KindHintFromExtension(string name, bool isDir)
    {
        if (isDir) return "folder";
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "pdf",
            ".docx" or ".doc" => "docx",
            ".xlsx" or ".xls" => "xlsx",
            ".pptx" or ".ppt" => "pptx",
            ".epub" => "epub",
            ".html" or ".htm" => "html",
            ".txt" or ".md" or ".rst" or ".log" => "text",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff" or ".heic" => "image",
            _ => "unknown",
        };
    }

    public static string MimeFromExtension(string name, bool isDir)
    {
        if (isDir) return "inode/directory";
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".doc" => "application/msword",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".xls" => "application/vnd.ms-excel",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".epub" => "application/epub+zip",
            ".html" or ".htm" => "text/html",
            ".txt" or ".log" => "text/plain",
            ".md" => "text/markdown",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".heic" => "image/heic",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
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
