namespace Everywhere.Mcp.OpenCli.Observation;

/// <summary>
/// SPEC §2.5 — new self-expand tools are hidden unless
/// <c>EVERYWHERE_MCP_SELFEXPAND=1</c> OR the session has already called
/// <c>activate_domain</c> for a domain that includes them. Phase 6 adds
/// per-session activation; until then, the env var is the only switch.
/// </summary>
public static class SelfExpandGate
{
    private static bool? _testOverride;

    public static bool Enabled
    {
        get
        {
            if (_testOverride.HasValue) return _testOverride.Value;
            return Environment.GetEnvironmentVariable("EVERYWHERE_MCP_SELFEXPAND") == "1"
                   || Environment.GetEnvironmentVariable("EVERYWHERE_MCP_FULL") == "1";
        }
    }

    /// <summary>Test hook — production paths never touch this.</summary>
    public static IDisposable EnableForTest()
    {
        _testOverride = true;
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => _testOverride = null;
    }
}
