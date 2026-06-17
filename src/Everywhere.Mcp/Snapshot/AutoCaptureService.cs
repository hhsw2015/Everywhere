using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Auto-refresh the context stash when the user actively pins a UI element via
/// AgentPickElement. Selection / clipboard auto-capture were intentionally
/// removed — they fired on terminal cursor moves and any-app Cmd-C, leaking
/// noise into every Claude Code prompt. Only deliberate pin actions trigger
/// here. Manual SnapshotContext hotkey still works as an explicit override.
///
/// Gated on <see cref="McpServerSettings.AutoCaptureContext"/>: when the
/// toggle flips, we (de)register the pin handler. Stash file lifetime + Take
/// semantics are unchanged.
/// </summary>
public sealed class AutoCaptureService : IAsyncInitializer, IDisposable
{
    private readonly Settings _settings;
    private readonly ContextStashWriter _writer;
    private readonly PickStash _pickStash;
    private readonly ILogger<AutoCaptureService> _logger;
    private readonly Lock _gate = new();

    private Action<IVisualElement>? _pickHandler;
    private int _running;

    public AutoCaptureService(
        Settings settings,
        ContextStashWriter writer,
        PickStash pickStash,
        ILogger<AutoCaptureService> logger)
    {
        _settings = settings;
        _writer = writer;
        _pickStash = pickStash;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        Apply(_settings.McpServer.AutoCaptureContext);
        _settings.McpServer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(McpServerSettings.AutoCaptureContext))
            {
                Apply(_settings.McpServer.AutoCaptureContext);
            }
        };
        return Task.CompletedTask;
    }

    private void Apply(bool enabled)
    {
        using var _ = _gate.EnterScope();
        if (enabled) Start();
        else Stop();
    }

    private void Start()
    {
        if (_pickHandler is not null) return;
        _pickHandler = TryCapture;
        _pickStash.Pinned += _pickHandler;
    }

    private void Stop()
    {
        if (_pickHandler is { } h)
        {
            _pickStash.Pinned -= h;
            _pickHandler = null;
        }
    }

    private void TryCapture(IVisualElement element)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await _writer.CaptureAsync(element);
                _logger.LogDebug("Auto-captured context (pin)");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Auto-capture (pin) failed");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    public void Dispose()
    {
        using var _ = _gate.EnterScope();
        Stop();
    }
}
