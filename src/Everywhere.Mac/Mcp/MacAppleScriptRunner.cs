using System.Diagnostics;
using Everywhere.Mcp.Snapshot;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS AppleScript runner via <c>/usr/bin/osascript -e &lt;source&gt;</c>. Reads
/// stdout/stderr asynchronously to avoid OS pipe-buffer deadlock when the script
/// emits more than ~64 KB. Distinguishes Apple Events permission denial (TCC -1743)
/// from generic failures.
/// </summary>
public sealed class MacAppleScriptRunner : IAppleScriptRunner
{
    // 15 s — accommodates browsers with hundreds of open tabs (Arc users routinely
    // hit 200+; the per-tab AppleScript dispatch is ~15 ms/tab on a warm machine).
    private const int TimeoutMs = 15000;

    public AppleScriptResult Run(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new AppleScriptResult(AppleScriptStatus.Failed, null, "empty script");

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
                return new AppleScriptResult(AppleScriptStatus.Failed, null, "failed to spawn osascript");

            // Drain pipes concurrently to defeat the 64KB buffer-deadlock case.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(TimeoutMs))
            {
                try { p.Kill(true); } catch { }
                try { p.WaitForExit(1000); } catch { }
                return new AppleScriptResult(AppleScriptStatus.Failed, null, $"osascript timed out ({TimeoutMs}ms)");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult().Trim();

            if (p.ExitCode != 0)
            {
                // -1743: Apple Events permission denial. Other negatives = scripting errors.
                var isPermission =
                    stderr.Contains("-1743", StringComparison.Ordinal) ||
                    stderr.Contains("not allowed assistive access", StringComparison.OrdinalIgnoreCase) ||
                    stderr.Contains("not authorized to send Apple events", StringComparison.OrdinalIgnoreCase);
                var status = isPermission ? AppleScriptStatus.PermissionDenied : AppleScriptStatus.Failed;
                return new AppleScriptResult(status, null, string.IsNullOrEmpty(stderr) ? $"exit {p.ExitCode}" : stderr);
            }

            return new AppleScriptResult(AppleScriptStatus.Ok, stdout.TrimEnd('\n'), null);
        }
        catch (Exception ex)
        {
            return new AppleScriptResult(AppleScriptStatus.Failed, null, ex.Message);
        }
    }
}
