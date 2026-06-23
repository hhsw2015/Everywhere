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
        ILogger<LinkRectHotkeyInitializer> logger)
    {
        _settings = settings;
        _shortcutListener = shortcutListener;
        _visualContext = visualContext;
        _contextWriter = contextWriter;
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
                var harvested = await _visualContext.HarvestLinksAsync(CancellationToken.None);
                if (harvested is null || harvested.Count == 0)
                {
                    _logger.LogInformation("LinkRect: no hyperlinks harvested (rect empty or no AX links)");
                    return;
                }
                var pairs = new List<(string Title, string Url)>(harvested.Count);
                foreach (var h in harvested) pairs.Add((h.Title, h.Url));
                await _contextWriter.CaptureLinksAsync(pairs);
                _logger.LogInformation("LinkRect stash filled with {Count} links", harvested.Count);
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
