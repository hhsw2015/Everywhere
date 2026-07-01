using System.Text.RegularExpressions;

namespace Everywhere.Mcp.OpenCli.Generator;

/// <summary>
/// SPEC §6 — per-invocation Restricted HostShim policies. Actual host
/// wiring lives in the OpenCLI runtime; this file exposes the decision
/// helpers as a pure API so unit tests can verify the rules in isolation.
/// </summary>
public static class RestrictedHostPolicy
{
    /// <summary>fs read is allowed under <c>~/.everywhere/</c> or <c>&lt;repo&gt;/3rd/opencli/</c>.</summary>
    public static bool AllowFsRead(string path, string everywhereRoot, string opencliRoot)
    {
        var canon = SafeGetFullPath(path);
        if (canon is null) return false;
        return UnderRoot(canon, everywhereRoot) || UnderRoot(canon, opencliRoot);
    }

    /// <summary>fs write is only allowed under <c>~/.everywhere/</c>.</summary>
    public static bool AllowFsWrite(string path, string everywhereRoot)
    {
        var canon = SafeGetFullPath(path);
        return canon is not null && UnderRoot(canon, everywhereRoot);
    }

    /// <summary>fetch is allowed only to hosts equal to or a subdomain of <paramref name="adapterDomain"/>.</summary>
    public static bool AllowFetch(string url, string adapterDomain)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != "https") return false;
        if (string.IsNullOrEmpty(adapterDomain)) return false;
        var host = u.Host;
        return host.Equals(adapterDomain, StringComparison.OrdinalIgnoreCase)
               || host.EndsWith("." + adapterDomain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>page.cdp(Runtime.evaluate, ...) is blocked; other CDP calls allowed.</summary>
    public static bool AllowCdp(string method) => !method.Equals("Runtime.evaluate", StringComparison.Ordinal);

    /// <summary>child_process.* is always blocked for local adapters.</summary>
    public static bool AllowChildProcess() => false;

    private static string? SafeGetFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    private static bool UnderRoot(string canonPath, string root)
    {
        var canonRoot = SafeGetFullPath(root);
        if (canonRoot is null) return false;
        return canonPath.StartsWith(canonRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || canonPath == canonRoot;
    }
}
