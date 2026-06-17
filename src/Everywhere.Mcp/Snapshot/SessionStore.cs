using System.Collections.Concurrent;
using Everywhere.Interop;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Per-server lookup of the most recent <see cref="AppSession"/> per <c>appKey</c>. Concurrent
/// because multiple MCP tool calls may resolve element indices in parallel; a snapshot is
/// replaced atomically by <see cref="Issue(string, IReadOnlyDictionary{int, IVisualElement}, nint)"/>.
/// </summary>
public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, AppSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private int _epochCounter;

    public AppSession Issue(
        string appKey,
        IReadOnlyDictionary<int, IVisualElement> elementsByIndex,
        nint windowHandle)
    {
        ArgumentNullException.ThrowIfNull(appKey);
        ArgumentNullException.ThrowIfNull(elementsByIndex);

        var epoch = Interlocked.Increment(ref _epochCounter);
        var session = new AppSession
        {
            Epoch = epoch,
            AppKey = appKey,
            WindowHandle = windowHandle,
            CapturedAtUtc = DateTime.UtcNow,
            ElementsByIndex = elementsByIndex,
        };

        _sessions[appKey] = session;
        return session;
    }

    public AppSession? GetCurrent(string appKey) =>
        _sessions.TryGetValue(appKey, out var s) ? s : null;

    public IEnumerable<string> GetKeys() => _sessions.Keys;

    /// <summary>
    /// Resolves an element index against <i>any</i> active session — useful when a tool
    /// receives an <c>element_index</c> without knowing its source app. Returns null if
    /// the index has been retired (i.e., its app has been re-snapshotted since).
    /// </summary>
    public (AppSession Session, IVisualElement Element)? ResolveAcrossSessions(int elementIndex)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.ElementsByIndex.TryGetValue(elementIndex, out var element))
            {
                return (session, element);
            }
        }

        return null;
    }

    public void Clear() => _sessions.Clear();
}
