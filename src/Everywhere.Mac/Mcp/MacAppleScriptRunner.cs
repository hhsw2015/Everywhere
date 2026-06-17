using System.Diagnostics;
using Everywhere.Mcp.Snapshot;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS AppleScript runner via <c>/usr/bin/osascript -e &lt;source&gt;</c>. Cheap and
/// avoids NSAppleScript / Carbon AESendMessage ceremony. 5-second timeout per call.
/// </summary>
public sealed class MacAppleScriptRunner : IAppleScriptRunner
{
    public string? Run(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    ArgumentList = { "-e", source },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            if (!p.Start()) return null;
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            if (p.ExitCode != 0) return null;
            var output = p.StandardOutput.ReadToEnd().TrimEnd('\n');
            return output.Length == 0 ? null : output;
        }
        catch
        {
            return null;
        }
    }
}
