using System.Diagnostics;
using Everywhere.Mcp.Snapshot;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS AppleScript runner via <c>/usr/bin/osascript -e &lt;source&gt;</c>. Cheap and
/// avoids NSAppleScript / Carbon AESendMessage ceremony. 5-second timeout per call.
/// On any failure (timeout, non-zero exit, missing Apple Events permission) the
/// stderr is captured and forwarded to <see cref="LastError"/> so callers can
/// distinguish "no data" from "permission not granted".
/// </summary>
public sealed class MacAppleScriptRunner : IAppleScriptRunner
{
    public string? LastError { get; private set; }

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
            if (!p.Start())
            {
                LastError = "failed to spawn osascript";
                return null;
            }
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch { }
                LastError = "osascript timed out (5s)";
                return null;
            }
            var stderr = p.StandardError.ReadToEnd().Trim();
            if (p.ExitCode != 0)
            {
                LastError = string.IsNullOrEmpty(stderr) ? $"exit {p.ExitCode}" : stderr;
                return null;
            }
            LastError = null;
            var output = p.StandardOutput.ReadToEnd().TrimEnd('\n');
            return output.Length == 0 ? null : output;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }
}
