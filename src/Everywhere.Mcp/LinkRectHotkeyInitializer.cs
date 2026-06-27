using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Utilities;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp;

/// <summary>
/// linkclump-plus style hyperlink harvest. Hotkey opens the LinkRect picker;
/// on release, every Hyperlink element whose bounds intersected the drag
/// rectangle is dropped into the agent-state snapshot via ContextStashWriter.
/// Same delivery channel as a single-element pick — no HTTP, no service wire.
/// </summary>
public sealed class LinkRectHotkeyInitializer : IAsyncInitializer
{
    private readonly Settings _settings;
    private readonly IShortcutListener _shortcutListener;
    private readonly IVisualElementContext _visualContext;
    private readonly ContextStashWriter _contextWriter;
    private readonly LinkRectStash _linkRectStash;
    private readonly ILogger<LinkRectHotkeyInitializer> _logger;
    private readonly Lock _syncLock = new();

    // Reentrancy guard — picker session opens on UI thread but the harvest
    // walk can take 100s of ms across many app trees, and a second hotkey
    // press during that window would race-open a second picker.
    private int _opening;

    public LinkRectHotkeyInitializer(
        Settings settings,
        IShortcutListener shortcutListener,
        IVisualElementContext visualContext,
        ContextStashWriter contextWriter,
        LinkRectStash linkRectStash,
        ILogger<LinkRectHotkeyInitializer> logger)
    {
        _settings = settings;
        _shortcutListener = shortcutListener;
        _visualContext = visualContext;
        _contextWriter = contextWriter;
        _linkRectStash = linkRectStash;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        InitializeShortcut(_settings.Shortcut.LinkRect);
        return Task.CompletedTask;
    }

    private void InitializeShortcut(CompositeKeyboardShortcut shortcut)
    {
        IDisposable? mainSubscription = null;
        IDisposable? alternativeSubscription = null;

        shortcut.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(CompositeKeyboardShortcut.IsEnabled):
                {
                    if (shortcut.IsEnabled) RegisterAll();
                    else
                    {
                        using var _0 = _syncLock.EnterScope();
                        DisposeHelper.DisposeToDefault(ref mainSubscription);
                        DisposeHelper.DisposeToDefault(ref alternativeSubscription);
                    }
                    break;
                }
                case nameof(CompositeKeyboardShortcut.Main) when shortcut.IsEnabled:
                    RegisterOne(shortcut.Main, ref mainSubscription);
                    break;
                case nameof(CompositeKeyboardShortcut.Alternative) when shortcut.IsEnabled:
                    RegisterOne(shortcut.Alternative, ref alternativeSubscription);
                    break;
            }
        };

        if (shortcut.IsEnabled) RegisterAll();

        void RegisterAll()
        {
            if (shortcut.Main.IsValid) RegisterOne(shortcut.Main, ref mainSubscription);
            if (shortcut.Alternative.IsValid) RegisterOne(shortcut.Alternative, ref alternativeSubscription);
        }
    }

    private void RegisterOne(KeyboardShortcut shortcut, ref IDisposable? slot)
    {
        using var _0 = _syncLock.EnterScope();
        DisposeHelper.DisposeToDefault(ref slot);
        if (!shortcut.IsValid)
        {
            _logger.LogInformation("LinkRect shortcut not yet bound; waiting for user input");
            return;
        }
        try
        {
            slot = _shortcutListener.Register(shortcut, OnHotkey);
            _logger.LogInformation("LinkRect shortcut registered: {Shortcut}", shortcut);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register LinkRect shortcut {Shortcut}", shortcut);
        }
    }

    private void OnHotkey()
    {
        _logger.LogInformation("LinkRect hotkey fired");
        Dispatcher.UIThread.Post(async () =>
        {
            if (Interlocked.CompareExchange(ref _opening, 1, 0) != 0)
            {
                _logger.LogInformation("LinkRect hotkey ignored: picker already opening");
                return;
            }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // Pin-style UX: paint the outline + ➕ the moment the
                // user releases the drag. The harvest task continues
                // populating links in the background; SnapshotContext
                // sees both rect and links once both are ready.
                Action<Avalonia.PixelRect> rectCommitted = rect =>
                {
                    _logger.LogInformation(
                        "LinkRect rectCommitted callback fired: rect=({X},{Y},{W}x{H})",
                        rect.X, rect.Y, rect.Width, rect.Height);
                    try { _linkRectStash.SetRect(rect); }
                    catch (Exception ex) { _logger.LogWarning(ex, "LinkRectStash.SetRect failed"); }
                };
                var result = await _visualContext.HarvestLinksAsync(rectCommitted, CancellationToken.None);
                sw.Stop();
                var links = result.Links ?? Array.Empty<HarvestedLink>();
                _logger.LogInformation(
                    "LinkRect: harvest took {Ms}ms, returned {Count} link(s) canceled={Canceled}",
                    sw.ElapsedMilliseconds, links.Count, result.Canceled);
                if (result.Canceled)
                {
                    // User pressed Esc / right-click — keep the user in
                    // their current app, no agent activation.
                    return;
                }
                if (links.Count == 0)
                {
                    _logger.LogInformation("LinkRect: rect produced no navigable hyperlinks");
                    // Bring the agent app to front anyway so users get
                    // visible feedback when their rect only covered
                    // javascript: anchors (most row-click sites). Without
                    // this the hotkey looks broken when AX simply had
                    // nothing navigable to give.
                    _contextWriter.ActivateAgent();
                    return;
                }
                // Per-link Debug rows are gated on logger level — building
                // the strings for hundreds of anchors is wasted work when
                // logger is at Info and the message will be discarded.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var cap = Math.Min(links.Count, 50);
                    for (var i = 0; i < cap; i++)
                    {
                        var h = links[i];
                        _logger.LogDebug("LinkRect[{I}] bbox=({X},{Y},{W}x{H}) title=\"{T}\" url={U}",
                            i, h.Bounds.X, h.Bounds.Y, h.Bounds.Width, h.Bounds.Height,
                            h.Title, h.Url);
                    }
                    if (links.Count > cap)
                        _logger.LogDebug("LinkRect: {Extra} additional links omitted from debug log", links.Count - cap);
                }
                // Per the v0.9.183 UX-consistency pass, LinkRect harvests
                // land in LinkRectStash and wait for SnapshotContext to
                // ship — same model as Pin / Whiteboard. The user gets a
                // ➕ overlay over the rect and can annotate before
                // shipping. CaptureLinksAsync's old immediate-ship path
                // is unused now but kept on the writer for the moment in
                // case external callers depend on it.
                // Append (not Set) — the SetRect call from rectCommitted
                // already created the entry and showed the overlay; we
                // just fill in the link list once harvest finishes.
                _linkRectStash.AppendLinks(links);
                _logger.LogInformation("LinkRect harvest done: {Count} links appended (deferred ship)", links.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LinkRect hotkey handler failed");
            }
            finally
            {
                Interlocked.Exchange(ref _opening, 0);
            }
        });
    }
}
