using System.Collections.Immutable;

namespace Everywhere.Interop;

/// <summary>
/// What kind of perception channel the annotation is anchored to. Lets
/// the LLM tell at a glance whether the user pointed (pin), framed
/// (whiteboard), highlighted (selected), or harvested (linkrect).
/// </summary>
public enum AnnotationSource
{
    Pin,
    Whiteboard,
    Selected,
    LinkRect,
}

/// <summary>
/// One annotation: a user note attached to a perception anchor.
/// </summary>
/// <param name="Source">Which perception channel produced this anchor.</param>
/// <param name="Body">The free-form note the user typed (or dictated).</param>
/// <param name="AnchorRef">
/// Opaque identifier the source-specific stash can use to look the anchor
/// back up (e.g. element_index for pin, region id for whiteboard). May be
/// null when the source uses the latest-only model (e.g. selected text is
/// stamped at capture time).
/// </param>
/// <param name="AnchorLabel">
/// Short human-readable description of what the annotation is attached
/// to, displayed to the LLM verbatim (e.g. "AXButton \"Submit\"",
/// "region 210x110 circle in Notes"). Resolved at <see cref="AnnotationStash.Add"/>
/// time and frozen — we do NOT re-resolve later, so the label survives even
/// if the underlying source stash expires.
/// </param>
/// <param name="CapturedAtUtc">UTC time the annotation was authored.</param>
public sealed record AnnotationItem(
    AnnotationSource Source,
    string Body,
    string? AnchorRef,
    string AnchorLabel,
    DateTimeOffset CapturedAtUtc);

/// <summary>
/// Multi-entry append-and-drain buffer holding user annotations queued
/// for the next SnapshotContext send. Unlike <see cref="PickStash"/>
/// which holds a single slot the agent Takes, the annotation flow is
/// "user accumulates N notes across pins/whiteboard/selection, then
/// ships them all in one shot". Drained on each SnapshotContext capture.
///
/// TTL exists so a long-forgotten note doesn't leak into a future
/// conversation if the user never sends. Each add bumps its own expiry
/// independently; expired entries are silently dropped on the next read.
/// </summary>
public sealed class AnnotationStash
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly List<Entry> _entries = new();

    public AnnotationStash() : this(TimeProvider.System) { }

    public AnnotationStash(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Raised after a successful <see cref="Add"/>. Lets the UI float
    /// the tray when there's at least one queued annotation.
    /// </summary>
    public event Action<AnnotationItem>? Added;

    /// <summary>
    /// Raised when an annotation is removed via <see cref="Remove"/> or
    /// the stash is drained. Lets the UI hide the tray when empty.
    /// </summary>
    public event Action? Changed;

    public void Add(AnnotationItem item, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var expiry = _clock.GetUtcNow() + (ttl ?? DefaultTtl);
        lock (_gate)
        {
            _entries.Add(new Entry(item, expiry));
        }
        Added?.Invoke(item);
        Changed?.Invoke();
    }

    /// <summary>
    /// Read the live (non-expired) entries without consuming them. The
    /// list is materialised so callers can iterate without holding the
    /// lock.
    /// </summary>
    public ImmutableArray<AnnotationItem> Peek()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            PruneExpired(now);
            if (_entries.Count == 0) return ImmutableArray<AnnotationItem>.Empty;
            var builder = ImmutableArray.CreateBuilder<AnnotationItem>(_entries.Count);
            foreach (var e in _entries) builder.Add(e.Item);
            return builder.ToImmutable();
        }
    }

    /// <summary>
    /// Atomically read all live entries and clear the stash. Called by
    /// the SnapshotContext capture path so a single send consumes
    /// everything queued so far.
    /// </summary>
    public ImmutableArray<AnnotationItem> Drain()
    {
        var now = _clock.GetUtcNow();
        ImmutableArray<AnnotationItem> result;
        bool hadEntries;
        lock (_gate)
        {
            PruneExpired(now);
            if (_entries.Count == 0) return ImmutableArray<AnnotationItem>.Empty;
            var builder = ImmutableArray.CreateBuilder<AnnotationItem>(_entries.Count);
            foreach (var e in _entries) builder.Add(e.Item);
            result = builder.ToImmutable();
            _entries.Clear();
            hadEntries = true;
        }
        if (hadEntries) Changed?.Invoke();
        return result;
    }

    public bool Remove(int index)
    {
        bool removed;
        lock (_gate)
        {
            if (index < 0 || index >= _entries.Count) return false;
            _entries.RemoveAt(index);
            removed = true;
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void Clear()
    {
        bool hadEntries;
        lock (_gate)
        {
            hadEntries = _entries.Count > 0;
            _entries.Clear();
        }
        if (hadEntries) Changed?.Invoke();
    }

    public int Count
    {
        get
        {
            var now = _clock.GetUtcNow();
            lock (_gate)
            {
                PruneExpired(now);
                return _entries.Count;
            }
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        // Caller holds _gate.
        _entries.RemoveAll(e => e.ExpiresAt <= now);
    }

    private readonly record struct Entry(AnnotationItem Item, DateTimeOffset ExpiresAt);
}
