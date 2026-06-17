namespace Everywhere.Mcp.Snapshot;

public sealed record FinderSelection(
    IReadOnlyList<FinderItem> Files,
    string? CurrentFolder);

public sealed record FinderItem(string Path, string Name, bool IsDirectory);

/// <summary>
/// Returns the user's current Finder selection with absolute POSIX paths and the
/// containing folder. Distinct from generic <c>selected_items</c> which only knows
/// display names from a11y; here we get real filesystem paths via Apple Events.
/// </summary>
public interface IFinderReader
{
    FinderSelection? GetSelection();
}

internal sealed class NullFinderReader : IFinderReader
{
    public FinderSelection? GetSelection() => null;
}
