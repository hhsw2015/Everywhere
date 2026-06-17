using System.Diagnostics;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Resolves a stable per-process key used to scope <see cref="SessionStore"/> snapshots.
/// Matches upstream behavior: bundle id (mac), exe path (win), WM_CLASS (linux); falls back
/// to lowercase process name when the OS-specific signal is unavailable.
/// </summary>
public static class AppKey
{
    public static string FromProcessId(int processId)
    {
        if (processId <= 0)
        {
            return "unknown";
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            return string.IsNullOrEmpty(name) ? processId.ToString() : name.ToLowerInvariant();
        }
        catch
        {
            return processId.ToString();
        }
    }

    public static bool MatchesQuery(string appKey, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        return appKey.Equals(query, StringComparison.OrdinalIgnoreCase)
               || appKey.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
