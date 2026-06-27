namespace Everywhere.Interop.Whiteboard;

/// <summary>
/// Cross-call buffer holding the regions a user just drew on the whiteboard.
/// Mirrors <see cref="PickStash"/> semantics: Take() consumes the slot so
/// the next agent call sees an empty stash, matching the "I drew this for
/// that one question" mental model.
///
/// Replacing an unread session is fine — the new whiteboard wins (user
/// changed their mind before the agent read it).
/// </summary>
public sealed class WhiteboardStash
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private Entry? _current;
    // Image bytes outlive the consumed regions: agent calls read_whiteboard
    // first (consumes regions, learns image_ids), then optionally calls
    // read_whiteboard_image(id) — at which point we need the bytes. Same
    // TTL as the regions, but doesn't go through Take().
    private Dictionary<string, byte[]>? _imageBytesById;
    private DateTimeOffset _imageBytesExpiresAtUtc;

    public WhiteboardStash() : this(TimeProvider.System) { }

    public WhiteboardStash(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Raised after a successful <see cref="Set"/>. Lets ContextStashWriter
    /// react to a fresh whiteboard without polling.
    /// </summary>
    public event Action<IReadOnlyList<WhiteboardRegion>>? Drawn;

    /// <summary>
    /// Raised when the user wipes the stash via ClearContextStash hotkey.
    /// Lets WhiteboardOverlayHost tear down outlines + ➕ badges so the
    /// visual state matches "no whiteboard pending".
    /// </summary>
    public event Action? Cleared;

    public void Set(IReadOnlyList<WhiteboardRegion> regions, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 0)
            throw new ArgumentException("At least one region required", nameof(regions));
        var expiry = _clock.GetUtcNow() + (ttl ?? DefaultTtl);
        // Snapshot the image bytes side-table from the regions so they
        // survive a Take() call. The agent typically does:
        //   read_whiteboard()        -> consumes regions, sees image ids
        //   read_whiteboard_image(id) -> needs the bytes
        var images = new Dictionary<string, byte[]>();
        MergeImageBytes(images, regions);
        lock (_gate)
        {
            _current = new Entry(regions, expiry);
            _imageBytesById = images;
            _imageBytesExpiresAtUtc = expiry;
        }
        Drawn?.Invoke(regions);
    }

    /// <summary>
    /// First-wins dedup of image_id → png bytes across N regions.
    /// Single source of truth used by both Set and Append paths so the
    /// dedup policy can never drift between them.
    /// </summary>
    private static void MergeImageBytes(Dictionary<string, byte[]> sink,
                                         IEnumerable<WhiteboardRegion> regions)
    {
        foreach (var r in regions)
        {
            foreach (var img in r.ImageLeaves)
            {
                if (img.PngBytes is { } b && !sink.ContainsKey(img.ImageId))
                    sink[img.ImageId] = b;
            }
        }
    }

    /// <summary>
    /// Append more regions to an existing in-progress session. Use when
    /// the user pressed Shift+Enter in the overlay (continue session)
    /// and then drew more regions in a later overlay invocation.
    ///
    /// If the existing entry is missing or expired, falls back to Set
    /// semantics — equivalent to starting a fresh session.
    ///
    /// Image bytes from the new regions are merged into the side-table
    /// keyed by image_id; the TTL is reset to DefaultTtl so the whole
    /// combined session lives for another 5 minutes from the most
    /// recent commit, not from the original first-commit time.
    /// </summary>
    public void Append(IReadOnlyList<WhiteboardRegion> moreRegions, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(moreRegions);
        if (moreRegions.Count == 0)
            throw new ArgumentException("At least one region required", nameof(moreRegions));
        var now = _clock.GetUtcNow();
        var expiry = now + (ttl ?? DefaultTtl);
        IReadOnlyList<WhiteboardRegion> combined;
        lock (_gate)
        {
            if (_current is null || _current.ExpiresAtUtc <= now)
            {
                // No fresh session to append to — start a new one in-line
                // (we can't call Set() while holding _gate without
                // re-entry, and the rest of the work is trivial enough
                // to keep here).
                var imagesNew = new Dictionary<string, byte[]>();
                MergeImageBytes(imagesNew, moreRegions);
                _current = new Entry(moreRegions, expiry);
                _imageBytesById = imagesNew;
                _imageBytesExpiresAtUtc = expiry;
                combined = moreRegions;
            }
            else
            {
                var list = new List<WhiteboardRegion>(_current.Regions.Count + moreRegions.Count);
                list.AddRange(_current.Regions);
                list.AddRange(moreRegions);
                // Wrap as IReadOnlyList so neither Peek subscribers nor a
                // future Drawn handler can mutate what the stash thinks
                // it's holding.
                _current = new Entry(list.AsReadOnly(), expiry);
                _imageBytesById ??= new Dictionary<string, byte[]>();
                // Same first-wins dedup policy as Set: an existing
                // image_id keeps its earliest bytes, so an unchanged
                // image on a redrawn region doesn't flip-flop.
                MergeImageBytes(_imageBytesById, moreRegions);
                _imageBytesExpiresAtUtc = expiry;
                combined = list;
            }
        }
        // Expose only the newly-appended slice to Drawn subscribers —
        // mirrors Set semantics (which raises just the regions handed in)
        // and avoids re-processing previously-stashed regions.
        Drawn?.Invoke(moreRegions);
    }

    /// <summary>
    /// Look up cropped image PNG bytes by the image_id surfaced in a
    /// previously consumed region's markdown. Returns null when expired
    /// or unknown. Does NOT consume the entry — agents may re-request
    /// the same image during a conversation; the TTL is the only bound.
    /// </summary>
    public byte[]? PeekImageBytes(string imageId)
    {
        if (string.IsNullOrEmpty(imageId)) return null;
        lock (_gate)
        {
            if (_imageBytesById is null) return null;
            if (_imageBytesExpiresAtUtc <= _clock.GetUtcNow())
            {
                _imageBytesById = null;
                return null;
            }
            return _imageBytesById.TryGetValue(imageId, out var b) ? b : null;
        }
    }

    /// <summary>
    /// Atomically reads and clears the slot. Returns null if empty or expired.
    /// </summary>
    public IReadOnlyList<WhiteboardRegion>? Take()
    {
        lock (_gate)
        {
            var entry = _current;
            _current = null;
            if (entry is null) return null;
            return entry.ExpiresAtUtc <= _clock.GetUtcNow() ? null : entry.Regions;
        }
    }

    /// <summary>
    /// Snapshots without consuming. Used for diagnostics / status indicators.
    /// </summary>
    public IReadOnlyList<WhiteboardRegion>? Peek()
    {
        lock (_gate)
        {
            if (_current is null) return null;
            return _current.ExpiresAtUtc <= _clock.GetUtcNow() ? null : _current.Regions;
        }
    }

    public bool HasFreshWhiteboard
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
            _imageBytesById = null;
        }
    }

    /// <summary>
    /// Like <see cref="Clear"/> but fires <see cref="Cleared"/> when there
    /// was something to clear. Used by ContextStashWriter.ClearStash so
    /// WhiteboardOverlayHost can drop its overlays.
    /// </summary>
    public void ClearWithEvent()
    {
        bool fire;
        lock (_gate)
        {
            fire = _current is not null;
            _current = null;
            _imageBytesById = null;
        }
        if (fire) Cleared?.Invoke();
    }

    private sealed record Entry(
        IReadOnlyList<WhiteboardRegion> Regions,
        DateTimeOffset ExpiresAtUtc);
}
