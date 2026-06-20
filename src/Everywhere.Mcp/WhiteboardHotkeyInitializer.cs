using Avalonia;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Interop.Whiteboard;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Whiteboard;
using Everywhere.Utilities;
using Everywhere.Views.Whiteboard;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp;

/// <summary>
/// Wires the user-configurable Whiteboard hotkey: pressing it opens a full-screen
/// transparent overlay where the user draws gestures (circle / underline / arrow / X)
/// over the current screen. Releasing the gesture (Enter or click "Done") runs
/// <see cref="WhiteboardParser"/> + <see cref="AnnotationSnapper"/> against the
/// focused window's a11y tree, fills <see cref="WhiteboardStash"/> with the
/// resulting regions, and triggers <see cref="ContextStashWriter"/> so the
/// agent's next prompt sees the whiteboard in its context envelope.
///
/// On Esc / overlay close-without-strokes, no stash is produced.
///
/// Lives in Everywhere.Mcp because the writer + stash + snapper are MCP-side
/// concerns. The overlay itself is in Everywhere.Core/Views/Whiteboard.
/// </summary>
public sealed class WhiteboardHotkeyInitializer : IAsyncInitializer
{
    private readonly Settings _settings;
    private readonly IShortcutListener _shortcutListener;
    private readonly IVisualElementContext _visualContext;
    private readonly WhiteboardStash _whiteboardStash;
    private readonly ContextStashWriter _contextWriter;
    private readonly ILogger<WhiteboardHotkeyInitializer> _logger;
    private readonly Lock _syncLock = new();

    private volatile WhiteboardOverlay? _activeOverlay;

    public WhiteboardHotkeyInitializer(
        Settings settings,
        IShortcutListener shortcutListener,
        IVisualElementContext visualContext,
        WhiteboardStash whiteboardStash,
        ContextStashWriter contextWriter,
        ILogger<WhiteboardHotkeyInitializer> logger)
    {
        _settings = settings;
        _shortcutListener = shortcutListener;
        _visualContext = visualContext;
        _whiteboardStash = whiteboardStash;
        _contextWriter = contextWriter;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        InitializeShortcut(_settings.Shortcut.Whiteboard);
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
        if (!shortcut.IsValid) return;
        try
        {
            slot = _shortcutListener.Register(shortcut, OnHotkey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register Whiteboard shortcut {Shortcut}", shortcut);
        }
    }

    private void OnHotkey()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (_activeOverlay is not null)
                {
                    _activeOverlay.Commit();
                    return;
                }
                await OpenOverlayAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Whiteboard hotkey handler failed");
            }
        });
    }

    private async Task OpenOverlayAsync()
    {
        // Capture the focused element BEFORE the overlay steals focus, so
        // we have a stable a11y tree to snap against on commit.
        IVisualElement? focusedRoot;
        PixelRect screen;
        try
        {
            var focused = _visualContext.FocusedElement;
            focusedRoot = focused?.Root() ?? focused;
            screen = focusedRoot?.BoundingRectangle
                     ?? _visualContext.Screens.FirstOrDefault()?.BoundingRectangle
                     ?? new PixelRect(0, 0, 1920, 1080);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Whiteboard could not pre-capture focused root; using screen fallback");
            focusedRoot = null;
            screen = new PixelRect(0, 0, 1920, 1080);
        }

        var overlay = new WhiteboardOverlay(screen);
        _activeOverlay = overlay;
        try
        {
            overlay.Show();
            overlay.Activate();
            var result = await overlay.ResultTask;
            if (result.Canceled || result.Strokes.Count == 0)
            {
                _logger.LogDebug("Whiteboard cancelled or empty; nothing to stash");
                return;
            }

            var strokes = result.Strokes
                .Select(pts => new Stroke(pts
                    .Select(p => new StrokePoint(p.X, p.Y, p.T)).ToList()))
                .ToList();

            // Re-pull the focused root if we don't have one (overlay closed
            // before pre-capture succeeded — rare).
            focusedRoot ??= _visualContext.FocusedElement?.Root()
                            ?? _visualContext.FocusedElement;
            if (focusedRoot is null)
            {
                _logger.LogWarning("Whiteboard committed but no focused root for snap");
                return;
            }

            var annotations = WhiteboardParser.Parse(strokes);
            var regions = new List<WhiteboardRegion>(annotations.Count);
            foreach (var ann in annotations)
            {
                var snap = AnnotationSnapper.Snap(ann, focusedRoot, strokes);
                if (snap.Rejected || snap.Leaves.Count == 0)
                {
                    _logger.LogInformation("Whiteboard region rejected ({Kind}): {Reason}",
                        ann.Kind, string.IsNullOrEmpty(snap.RejectReason) ? "no leaves" : snap.RejectReason);
                    continue;
                }
                regions.Add(new WhiteboardRegion(ann.Kind, snap.Rect, snap.Leaves, snap.Confidence));
            }

            if (regions.Count == 0)
            {
                _logger.LogInformation("Whiteboard produced no usable regions; nothing to stash");
                return;
            }

            _whiteboardStash.Set(regions);
            _logger.LogInformation("Whiteboard stash filled with {Count} region(s)", regions.Count);

            try
            {
                await _contextWriter.CaptureAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Whiteboard: ContextStashWriter.CaptureAsync failed");
            }
        }
        finally
        {
            _activeOverlay = null;
        }
    }
}

internal static class WhiteboardElementExtensions
{
    public static IVisualElement Root(this IVisualElement element)
    {
        var current = element;
        while (current.Parent is { } p) current = p;
        return current;
    }
}
