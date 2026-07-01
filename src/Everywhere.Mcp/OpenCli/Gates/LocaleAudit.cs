using System.Text.RegularExpressions;

namespace Everywhere.Mcp.OpenCli.Gates;

/// <summary>SPEC §Phase 4 G8 — warn on hard-coded aria-label strings (locale-fragile).</summary>
public static class LocaleAudit
{
    private static readonly Regex AriaLabelLiteral = new(
        @"aria-label\s*=\s*['""]([^'""]{2,})['""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static GateResult Check(string source)
    {
        var r = GateResult.Empty();
        foreach (var (line, text) in AdapterSourceScan.Lines(source))
        {
            var m = AriaLabelLiteral.Match(text);
            if (m.Success)
            {
                r.Warnings.Add(new GateFinding("G8", "LOCALE_HARDCODED_STRING",
                    "hardcoded aria-label string will break under locale swap — extract to a config with fallbacks",
                    line, text.Trim()));
            }
        }
        return r;
    }
}
