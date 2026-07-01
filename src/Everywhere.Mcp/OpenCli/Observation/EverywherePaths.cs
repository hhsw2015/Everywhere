namespace Everywhere.Mcp.OpenCli.Observation;

/// <summary>
/// SPEC §2.3 — every persistent artifact lives under
/// <c>~/.everywhere/</c>. Callers never pass paths in; the store derives
/// them here.
/// </summary>
public static class EverywherePaths
{
    private static string BaseDir => _override ?? DefaultBaseDir();
    private static string? _override;

    /// <summary>Test hook — do not use from production code.</summary>
    public static IDisposable OverrideBaseForTest(string absPath)
    {
        _override = absPath;
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => _override = null;
    }

    public static string CapturesDir()
    {
        var p = Path.Combine(BaseDir, "captures");
        Directory.CreateDirectory(p);
        return p;
    }

    public static string ExtractionRulesPath()
    {
        Directory.CreateDirectory(BaseDir);
        return Path.Combine(BaseDir, "extraction-rules.json");
    }

    public static string SitesDir()
    {
        var p = Path.Combine(BaseDir, "sites");
        Directory.CreateDirectory(p);
        return p;
    }

    public static string AdaptersDir()
    {
        var p = Path.Combine(BaseDir, "adapters");
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>Root <c>~/.everywhere/</c>.</summary>
    public static string Root
    {
        get
        {
            Directory.CreateDirectory(BaseDir);
            return BaseDir;
        }
    }

    private static string DefaultBaseDir()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".everywhere");
}
