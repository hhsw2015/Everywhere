namespace Everywhere.Interop;

/// <summary>
/// Single-slot cross-call buffer holding the element a user just "pinned" for an AI agent
/// via the Agent Pick hotkey. The MCP <c>read_pick</c> tool consumes the slot (Take semantics)
/// so the next call sees an empty stash again, mirroring the user mental model
/// "I pinned this for that one question".
///
/// Pins expire after <see cref="DefaultTtl"/> so a forgotten pin doesn't leak into a future
/// agent conversation. Replacing an unread pin is fine — the new one wins.
/// </summary>
public sealed class PickStash
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private Entry? _current;

    public PickStash() : this(TimeProvider.System) { }

    public PickStash(TimeProvider clock)
    {
        _clock = clock;
    }

    public void Set(IVisualElement element, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        var expiry = _clock.GetUtcNow() + (ttl ?? DefaultTtl);
        lock (_gate)
        {
            _current = new Entry(element, expiry);
        }
    }

    /// <summary>
    /// Atomically reads and clears the slot. Returns null if empty or expired.
    /// </summary>
    public IVisualElement? Take()
    {
        lock (_gate)
        {
            var entry = _current;
            _current = null;
            if (entry is null) return null;
            return entry.ExpiresAtUtc <= _clock.GetUtcNow() ? null : entry.Element;
        }
    }

    /// <summary>
    /// Snapshots without consuming. Used for diagnostics / status indicators in the UI.
    /// </summary>
    public bool HasFreshPin
    {
        get
        {
            lock (_gate)
            {
                return _current is { } e && e.ExpiresAtUtc > _clock.GetUtcNow();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _current = null;
        }
    }

    private sealed record Entry(IVisualElement Element, DateTimeOffset ExpiresAtUtc);
}
