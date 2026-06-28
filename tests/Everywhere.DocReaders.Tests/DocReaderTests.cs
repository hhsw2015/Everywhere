using System.Text.Json;
using Everywhere.Mcp.Tools;
using ModelContextProtocol.Protocol;

namespace Everywhere.DocReaders.Tests;

public class DocReaderTests
{
    private const double Threshold = 0.92;

    public static IEnumerable<TestCaseData> PdfCases() => CasesFor("*.pdf");
    public static IEnumerable<TestCaseData> DocxCases() => CasesFor("*.docx");
    public static IEnumerable<TestCaseData> XlsxCases() => CasesFor("*.xlsx");
    public static IEnumerable<TestCaseData> PptxCases() => CasesFor("*.pptx");
    public static IEnumerable<TestCaseData> EpubCases() => CasesFor("*.epub");
    public static IEnumerable<TestCaseData> HtmlCases() => CasesFor("*.html").Concat(CasesFor("*.htm"));
    public static IEnumerable<TestCaseData> TxtCases() => CasesFor("*.txt").Concat(CasesFor("*.md"));

    [TestCaseSource(nameof(PdfCases))]
    public void ReadPdf_MatchesGolden(string path) =>
        AssertSimilar(path, DocReadPdfTool.DocReadPdf(path));

    [TestCaseSource(nameof(DocxCases))]
    public void ReadDocx_MatchesGolden(string path) =>
        AssertSimilar(path, DocReadDocxTool.DocReadDocx(path));

    [TestCaseSource(nameof(XlsxCases))]
    public void ReadXlsx_MatchesGolden(string path) =>
        AssertSimilar(path, DocReadXlsxTool.DocReadXlsx(path));

    [TestCaseSource(nameof(PptxCases))]
    public void ReadPptx_MatchesGolden(string path) =>
        AssertSimilar(path, DocReadPptxTool.DocReadPptx(path));

    [TestCaseSource(nameof(EpubCases))]
    public void ReadEpub_MatchesGolden(string path) =>
        AssertSimilar(path, DocReadEpubTool.DocReadEpub(path));

    [TestCaseSource(nameof(HtmlCases))]
    public void ReadHtml_MatchesGolden(string path) =>
        AssertSimilar(path, DocReadHtmlTool.DocReadHtml(path));

    [TestCaseSource(nameof(TxtCases))]
    public void ReadTxt_MatchesGolden(string path) =>
        AssertSimilar(path, DocReadTxtTool.DocReadTxt(path));

    private static IEnumerable<TestCaseData> CasesFor(string pattern)
    {
        var dir = CorpusLocator.CorpusDir;
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, pattern))
        {
            // Skip golden files themselves.
            if (f.EndsWith(".golden.txt", StringComparison.Ordinal)) continue;
            yield return new TestCaseData(f).SetName($"{Path.GetFileName(f)}");
        }
    }

    private static void AssertSimilar(string path, CallToolResult result)
    {
        var goldenPath = path + ".golden.txt";
        if (!File.Exists(goldenPath))
        {
            Assert.Inconclusive($"no golden for {path}");
            return;
        }

        var text = ExtractText(result);
        // Force UTF-8 so a Windows runner with a non-UTF-8 ANSI code page does
        // not silently misdecode the goldens and flip Jaccard near 0.92.
        var golden = File.ReadAllText(goldenPath, System.Text.Encoding.UTF8);
        var sim = Similarity.NormalizedTokenJaccard(text, golden);

        if (Exemptions.ByFileName.Contains(Path.GetFileName(path)))
        {
            // Exempt: assert the reader produced something rather than the >=0.92 bar.
            Assert.That(text, Is.Not.Empty, $"exempt file but reader returned no text: {path}");
            return;
        }

        Assert.That(sim, Is.GreaterThanOrEqualTo(Threshold),
            $"sim={sim:F3} on {path}\n{Similarity.Diff(text, golden, 1000)}");
    }

    private static string ExtractText(CallToolResult result)
    {
        var first = result.Content?.FirstOrDefault();
        var raw = (first as TextContentBlock)?.Text ?? string.Empty;
        if (result.IsError == true)
        {
            Assert.Fail($"tool returned IsError: {raw}");
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("text", out var t)) return t.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // fall through
        }

        return raw;
    }
}
