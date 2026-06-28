using System.ComponentModel;
using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class DocReadXlsxTool
{
    [McpServerTool(Name = "doc_read_xlsx", ReadOnly = true)]
    [Description(
        "Extract sheet contents from a .xlsx file as CSV-ish text (one sheet per stanza). " +
        "Mirrors the output shape of `xlsx2csv` so it can be compared against that golden. " +
        "Returns {text, metadata:{sheets, truncated, source}}.")]
    public static CallToolResult DocReadXlsx(string path)
    {
        try
        {
            if (!File.Exists(path)) return DocReaderResult.NotFound(path);

            var sb = new StringBuilder();
            int sheetCount = 0;
            using (var wb = new XLWorkbook(path))
            {
                foreach (var sheet in wb.Worksheets)
                {
                    sheetCount++;
                    var range = sheet.RangeUsed();
                    if (range is null) continue;
                    var rows = range.RowsUsed();
                    foreach (var row in rows)
                    {
                        var cells = row.Cells(1, range.LastColumn().ColumnNumber()).Select(FormatCell);
                        sb.AppendLine(string.Join(",", cells));
                    }
                }
            }

            return DocReaderResult.Build(sb.ToString(), new Dictionary<string, object?>
            {
                ["sheets"] = sheetCount,
                ["source"] = path,
            });
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "doc_read_xlsx");
        }
    }

    private static string FormatCell(IXLCell cell)
    {
        var v = cell.IsEmpty() ? string.Empty : cell.GetFormattedString();
        if (v.IndexOfAny([',', '"', '\n', '\r']) >= 0)
        {
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }
        return v;
    }
}
