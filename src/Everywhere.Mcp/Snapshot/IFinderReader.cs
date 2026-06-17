namespace Everywhere.Mcp.Snapshot;

public sealed record FinderSelection(
    IReadOnlyList<FinderItem> Files,
    string? CurrentFolder);

public sealed record FinderItem(string Path, string Name, bool IsDirectory);

public enum FinderStatus
{
    Ok,
    PermissionDenied,
    NotSupported,
}

public sealed record FinderResult(FinderStatus Status, FinderSelection? Selection, string? ErrorMessage = null);

/// <summary>
/// Returns the user's current Finder selection with absolute POSIX paths and the
/// containing folder. Distinct from generic <c>selected_items</c> which only knows
/// display names from a11y; here we get real filesystem paths via Apple Events.
/// </summary>
public interface IFinderReader
{
    FinderResult GetSelection();
}

internal sealed class NullFinderReader : IFinderReader
{
    public FinderResult GetSelection() => new(FinderStatus.NotSupported, null);
}
