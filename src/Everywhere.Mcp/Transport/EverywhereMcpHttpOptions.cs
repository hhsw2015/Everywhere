namespace Everywhere.Mcp.Transport;

/// <summary>
/// Tunables for the in-process Kestrel listener. Defaults match SPEC §8.2:
/// port 7878, walk up to 10 fallback ports on bind conflict, enabled by default.
/// Override via the <c>EVERYWHERE_MCP_PORT</c> env var or the Settings &gt; MCP page.
/// </summary>
public sealed class EverywhereMcpHttpOptions
{
    public int Port { get; set; } = ResolveDefaultPort();
    public int MaxPortFallbacks { get; set; } = 10;
    public bool Enabled { get; set; } = true;

    private static int ResolveDefaultPort()
    {
        var fromEnv = Environment.GetEnvironmentVariable("EVERYWHERE_MCP_PORT");
        return int.TryParse(fromEnv, out var p) && p > 0 && p < 65536 ? p : 7878;
    }
}
