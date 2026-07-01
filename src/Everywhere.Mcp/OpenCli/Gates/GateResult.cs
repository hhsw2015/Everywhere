namespace Everywhere.Mcp.OpenCli.Gates;

public sealed record GateFinding(string Gate, string Code, string Message, int? Line = null, string? Snippet = null);

public sealed record GateResult(List<GateFinding> Errors, List<GateFinding> Warnings)
{
    public bool Ok => Errors.Count == 0;

    public static GateResult Empty() => new([], []);
}
