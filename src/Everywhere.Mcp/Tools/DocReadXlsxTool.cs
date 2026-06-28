using System.ComponentModel;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
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
            using (var doc = SpreadsheetDocument.Open(path, false))
            {
                var wb = doc.WorkbookPart;
                if (wb is null) return DocReaderResult.Build(string.Empty, new Dictionary<string, object?>
                {
                    ["sheets"] = 0, ["source"] = path,
                });

                var sst = wb.SharedStringTablePart?.SharedStringTable;
                foreach (var sheet in wb.Workbook.Descendants<Sheet>())
                {
                    sheetCount++;
                    if (sheet.Id?.Value is null) continue;
                    var part = (WorksheetPart)wb.GetPartById(sheet.Id.Value);
                    var sheetData = part.Worksheet.Elements<SheetData>().FirstOrDefault();
                    if (sheetData is null) continue;

                    foreach (var row in sheetData.Elements<Row>())
                    {
                        var cells = row.Elements<Cell>().Select(c => FormatCell(c, sst));
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
            return ToolErrors.Error($"doc_read_xlsx: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string FormatCell(Cell cell, SharedStringTable? sst)
    {
        string raw;
        if (cell.DataType?.Value == CellValues.SharedString && cell.CellValue?.Text is { } idxStr && int.TryParse(idxStr, out var idx) && sst is not null && idx >= 0 && idx < sst.ChildElements.Count)
        {
            raw = sst.ChildElements[idx].InnerText;
        }
        else if (cell.DataType?.Value == CellValues.InlineString)
        {
            raw = cell.InnerText;
        }
        else
        {
            raw = cell.CellValue?.Text ?? string.Empty;
        }

        if (raw.IndexOfAny([',', '"', '\n', '\r']) >= 0)
        {
            return "\"" + raw.Replace("\"", "\"\"") + "\"";
        }
        return raw;
    }
}
