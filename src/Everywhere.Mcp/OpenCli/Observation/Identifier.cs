using System.Text.RegularExpressions;

namespace Everywhere.Mcp.OpenCli.Observation;

/// <summary>
/// SPEC §2.3 — every caller-facing site/name/domain/subpath is validated
/// against this pattern before it can hit disk. `INVALID_IDENTIFIER` is
/// the canonical error code (§5).
/// </summary>
public static class Identifier
{
    private static readonly Regex Pattern = new(
        "^[a-z0-9][a-z0-9._-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const string PatternSource = "^[a-z0-9][a-z0-9._-]{0,63}$";

    public static bool IsValid(string? id) => id is not null && Pattern.IsMatch(id);

    /// <summary>Throws <see cref="InvalidIdentifierException"/> with the failing arg name.</summary>
    public static void Require(string argName, string? value)
    {
        if (!IsValid(value)) throw new InvalidIdentifierException(argName, value);
    }
}

public sealed class InvalidIdentifierException(string argName, string? value)
    : Exception($"INVALID_IDENTIFIER: '{argName}'='{value}' does not match {Identifier.PatternSource}")
{
    public string ArgName { get; } = argName;
    public string? BadValue { get; } = value;
}
