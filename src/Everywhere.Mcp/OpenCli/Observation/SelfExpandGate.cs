namespace Everywhere.Mcp.OpenCli.Observation;

/// <summary>
/// SPEC §2.5 — kill switch for self-expand tools. Default ON since
/// v0.9.302; Phase 6 tier gate + <c>activate_domain</c> handle token
/// budget precisely. Set <c>EVERYWHERE_MCP_SELFEXPAND=0</c> to force
/// every self-expand tool to return SELFEXPAND_DISABLED (emergency
/// rollback without redeploying).
/// </summary>
public static class SelfExpandGate
{
    private static bool? _testOverride;

    public static bool Enabled
    {
        get
        {
            if (_testOverride.HasValue) return _testOverride.Value;
            // Explicit opt-out only. Any other value (including unset) → enabled.
            return Environment.GetEnvironmentVariable("EVERYWHERE_MCP_SELFEXPAND") != "0";
        }
    }

    /// <summary>Test hook — production paths never touch this.</summary>
    public static IDisposable EnableForTest()
    {
        _testOverride = true;
        return new Restore();
    }

    /// <summary>Test hook — force-off (mirror EnableForTest for symmetric flow).</summary>
    public static IDisposable DisableForTest()
    {
        _testOverride = false;
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => _testOverride = null;
    }
}
