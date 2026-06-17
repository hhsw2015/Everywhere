namespace Everywhere.Mcp.Input;

/// <summary>
/// Returns seconds since the user last touched any input device. Lets agents
/// decide whether to interrupt the user with a notification.
/// </summary>
public interface IIdleTimeReader
{
    double GetIdleSeconds();
}

internal sealed class NullIdleTimeReader : IIdleTimeReader
{
    public double GetIdleSeconds() => 0;
}
