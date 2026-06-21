using System.Diagnostics;
using System.Globalization;
using System.IO;
using Avalonia;
using Everywhere.Interop.Whiteboard;
using Microsoft.Extensions.Logging;

namespace Everywhere.Linux.Interop;

/// <summary>
/// tesseract subprocess-backed OCR. tesseract is a system package
/// (apt install tesseract-ocr-eng tesseract-ocr-chi-sim). If not
/// installed, recognize() returns empty list — the hybrid slicer
/// falls back to leaf-bbox proportional slicing automatically, so
/// no language pack is required for whiteboard to work.
///
/// We use tesseract's TSV output (one row per word with bbox), then
/// group words by line (tesseract supplies line_num column).
/// </summary>
public sealed class LinuxTesseractOcrEngine : IOcrEngine
{
    private readonly ILogger<LinuxTesseractOcrEngine> _logger;
    private readonly bool _available;

    public LinuxTesseractOcrEngine(ILogger<LinuxTesseractOcrEngine> logger)
    {
        _logger = logger;
        _available = ProbeTesseract();
        if (!_available)
        {
            _logger.LogInformation(
                "tesseract not found on PATH; whiteboard will fall back to leaf-bbox slicing. " +
                "Install with: apt install tesseract-ocr-eng tesseract-ocr-chi-sim");
        }
    }

    public IReadOnlyList<OcrLine> Recognize(
        Stream pngStream,
        PixelPoint originPx,
        OcrQuality quality = OcrQuality.Fast,
        IReadOnlyList<string>? languages = null)
    {
        if (!_available || pngStream is null) return [];
        var tmpPng = Path.Combine(Path.GetTempPath(), $"ev-ocr-{Guid.NewGuid():N}.png");
        try
        {
            if (pngStream.CanSeek) pngStream.Position = 0;
            using (var fs = File.Create(tmpPng)) pngStream.CopyTo(fs);

            var lang = ResolveLangArg(languages);
            // --psm 6: assume a single uniform block of text. Good for the
            // typical whiteboard case (a code block / paragraph).
            // tesseract has no Fast/Accurate split — its default LSTM
            // engine is the only option in modern versions, so OcrQuality
            // is accepted but has no effect here.
            var psi = new ProcessStartInfo("tesseract")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // ArgumentList: per-OS escaping, no shell-quoting bugs.
            psi.ArgumentList.Add(tmpPng);
            psi.ArgumentList.Add("-");
            psi.ArgumentList.Add("--psm");
            psi.ArgumentList.Add("6");
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add(lang);
            psi.ArgumentList.Add("tsv");
            using var p = Process.Start(psi);
            if (p is null) return [];
            // Drain stderr concurrently to avoid pipe-buffer deadlock when
            // tesseract emits warnings — stderr is small for the success
            // path but unbounded on missing-language errors.
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            string tsv = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return [];
            }
            if (p.ExitCode != 0) return [];

            return ParseTsv(tsv, originPx);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "tesseract OCR failed");
            return [];
        }
        finally
        {
            try { if (File.Exists(tmpPng)) File.Delete(tmpPng); } catch { /* ignore */ }
        }
    }

    private static bool ProbeTesseract()
    {
        try
        {
            // --version writes to stderr historically; redirect both and
            // discard so this never deadlocks on a slow disk / SSD warmup.
            using var p = Process.Start(new ProcessStartInfo("tesseract", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            p.ErrorDataReceived += (_, _) => { };
            p.OutputDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            return p.WaitForExit(2000) && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveLangArg(IReadOnlyList<string>? bcp47)
    {
        if (bcp47 is not { Count: > 0 }) return "eng+chi_sim";
        // BCP-47 -> tesseract code (best-effort). Unknown tags drop.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "eng",     ["en-US"] = "eng",   ["en-GB"] = "eng",
            ["zh"] = "chi_sim", ["zh-Hans"] = "chi_sim", ["zh-CN"] = "chi_sim",
            ["zh-Hant"] = "chi_tra", ["zh-TW"] = "chi_tra", ["zh-HK"] = "chi_tra",
            ["ja"] = "jpn",
            ["ko"] = "kor",
            ["fr"] = "fra",
            ["de"] = "deu",
            ["es"] = "spa",
            ["it"] = "ita",
            ["pt"] = "por",
            ["ru"] = "rus",
        };
        var langs = new List<string>();
        foreach (var t in bcp47)
        {
            if (map.TryGetValue(t, out var ts) && !langs.Contains(ts))
                langs.Add(ts);
        }
        return langs.Count == 0 ? "eng" : string.Join('+', langs);
    }

    private static IReadOnlyList<OcrLine> ParseTsv(string tsv, PixelPoint originPx)
    {
        // TSV columns: level page_num block_num par_num line_num word_num
        //              left top width height conf text
        // Use InvariantCulture: tesseract emits '.' decimals, but
        // current-culture parsing fails silently on de-DE/fr-FR/etc.
        var inv = CultureInfo.InvariantCulture;
        var byLine = new Dictionary<(int blk, int par, int ln), List<(int x, int y, int w, int h, string text, double conf)>>();
        using var sr = new StringReader(tsv);
        string? line;
        bool firstHeader = true;
        while ((line = sr.ReadLine()) is not null)
        {
            if (firstHeader) { firstHeader = false; continue; }
            var cols = line.Split('\t');
            if (cols.Length < 12) continue;
            if (!int.TryParse(cols[0], NumberStyles.Integer, inv, out var level)) continue;
            if (level != 5) continue;  // word level
            var text = cols[11];
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!int.TryParse(cols[2], NumberStyles.Integer, inv, out var blk)) continue;
            if (!int.TryParse(cols[3], NumberStyles.Integer, inv, out var par)) continue;
            if (!int.TryParse(cols[4], NumberStyles.Integer, inv, out var ln)) continue;
            if (!int.TryParse(cols[6], NumberStyles.Integer, inv, out var x)) continue;
            if (!int.TryParse(cols[7], NumberStyles.Integer, inv, out var y)) continue;
            if (!int.TryParse(cols[8], NumberStyles.Integer, inv, out var w)) continue;
            if (!int.TryParse(cols[9], NumberStyles.Integer, inv, out var h)) continue;
            double.TryParse(cols[10], NumberStyles.Float, inv, out var conf);
            // tesseract emits -1 for non-words; clamp so the average
            // doesn't go negative.
            if (conf < 0) conf = 0;
            var key = (blk, par, ln);
            if (!byLine.TryGetValue(key, out var list))
            {
                list = new();
                byLine[key] = list;
            }
            list.Add((x, y, w, h, text, conf / 100.0));
        }

        var result = new List<OcrLine>(byLine.Count);
        foreach (var (_, words) in byLine)
        {
            if (words.Count == 0) continue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            double confSum = 0;
            var textParts = new List<string>(words.Count);
            foreach (var w in words.OrderBy(w => w.x))
            {
                if (w.x < minX) minX = w.x;
                if (w.y < minY) minY = w.y;
                if (w.x + w.w > maxX) maxX = w.x + w.w;
                if (w.y + w.h > maxY) maxY = w.y + w.h;
                confSum += w.conf;
                textParts.Add(w.text);
            }
            result.Add(new OcrLine(
                Text: string.Join(' ', textParts),
                Bounds: new PixelRect(
                    minX + originPx.X,
                    minY + originPx.Y,
                    Math.Max(1, maxX - minX),
                    Math.Max(1, maxY - minY)),
                Confidence: confSum / words.Count));
        }
        result.Sort((a, b) => a.Bounds.Y.CompareTo(b.Bounds.Y));
        return result;
    }
}
