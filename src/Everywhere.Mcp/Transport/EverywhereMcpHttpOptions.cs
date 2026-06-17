namespace Everywhere.Mcp.Transport;

/// <summary>
/// Tunables for the in-process Kestrel listener. Defaults match SPEC §8.2:
/// port 7878, walk up to 10 fallback ports on bind conflict, enabled by default.
/// Override via the <c>EVERYWHERE_MCP_PORT</c> env var or the Settings &gt; MCP page.
/// </summary>
public sealed class EverywhereMcpHttpOptions
{
    private int _port = ResolveDefaultPort();
    private int _maxPortFallbacks = 10;

    public int Port
    {
        get => _port;
        set
        {
            if (value <= 0 || value > 65_535)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Port must be in (0, 65535].");
            }
            _port = value;
        }
    }

    public int MaxPortFallbacks
    {
        get => _maxPortFallbacks;
        set
        {
            if (value < 0 || value > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxPortFallbacks must be in [0, 100].");
            }
            _maxPortFallbacks = value;
        }
    }

    public bool Enabled { get; set; } = true;

    private static int ResolveDefaultPort()
    {
        var fromEnv = Environment.GetEnvironmentVariable("EVERYWHERE_MCP_PORT");
        return int.TryParse(fromEnv, out var p) && p > 0 && p < 65_536 ? p : 7878;
    }
}
