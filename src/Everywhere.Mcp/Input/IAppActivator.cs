namespace Everywhere.Mcp.Input;

/// <summary>
/// Brings a target app to the foreground after a context capture, so the user
/// can keep typing into the agent surface without manually switching windows.
/// Caller passes either a bundle id (mac), exe basename (win), or wm class /
/// title fragment (linux); the impl decides how to resolve it.
/// </summary>
public interface IAppActivator
{
    /// <summary>Returns true if a matching window was found and raised.</summary>
    bool Activate(string appIdentifier);

    /// <summary>
    /// True iff the OS currently treats the app matching this identifier
    /// as frontmost. Gate for keyboard-injection so a launch phrase isn't
    /// typed into the previously-focused app while raise hasn't settled
    /// or another app has stolen focus back. Default impl returns false
    /// so platforms without frontmost-detection simply skip injection.
    /// </summary>
    bool IsFrontmost(string appIdentifier) => false;
}

internal sealed class NullAppActivator : IAppActivator
{
    public bool Activate(string appIdentifier) => false;
    public bool IsFrontmost(string appIdentifier) => false;
}
