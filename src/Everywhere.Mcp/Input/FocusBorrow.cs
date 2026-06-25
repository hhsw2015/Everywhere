// Mirrors: packages/OpenComputerUseKit/Sources/OpenComputerUseKit/InputSimulation.swift
// Upstream: iFurySt/open-codex-computer-use@<sha-pinned-in-UPSTREAM_REF.md>

using Everywhere.Mcp.Snapshot;

namespace Everywhere.Mcp.Input;

/// <summary>
/// Per-process serialization point for "borrow foreground for one input event then restore".
/// Spec §7: a11y raise → 120ms → activate → 250ms → caller's input ops → restore on Dispose.
/// One holder at a time; concurrent <see cref="Acquire"/> calls queue, with a 5s wait timeout.
/// </summary>
public sealed class FocusBorrow
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IFocusBackend _backend;

    public FocusBorrow(IFocusBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
    }

    public IDisposable Acquire(nint targetWindow, bool requireFocus = true, int processId = 0)
    {
        if (!requireFocus)
        {
            return NoopHandle.Instance;
        }

        if (!_gate.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("FocusBorrow contention exceeded 5s.");
        }

        var prev = _backend.GetForegroundWindow();
        try
        {
            // 1:1 OCCU prepareAppForGlobalPointerInput
            // (InputSimulation.swift L48-56):
            //   raiseAppWindowViaAccessibility(pid)
            //   Thread.sleep(0.12)
            //   app.runningApplication.activate([])
            //   Thread.sleep(0.25)
            // OCCU runs the full sequence every time — no
            // "skip if already foreground" early-exit. SwiftUI gestures
            // need the activate-after-raise pump even when the app
            // already looks frontmost; skipping it was masking races on
            // Calculator. Match OCCU literally.
            _backend.TryAxRaise(targetWindow);
            Thread.Sleep(UpstreamConstants.FocusAxRaiseDelay);
            if (processId > 0)
            {
                _backend.ActivateProcess(processId);
            }
            else
            {
                _backend.Activate(targetWindow);
            }
            Thread.Sleep(UpstreamConstants.FocusActivateDelay);
        }
        catch
        {
            _gate.Release();
            throw;
        }

        return new Handle(_gate, _backend, prev);
    }

    private sealed class Handle(SemaphoreSlim gate, IFocusBackend backend, nint prev) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                if (prev != 0)
                {
                    backend.Activate(prev);
                }
            }
            catch
            {
                // ponytail: restore is best-effort, log channel reserved for the host's logger.
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private sealed class NoopHandle : IDisposable
    {
        public static readonly NoopHandle Instance = new();
        public void Dispose() { }
    }
}

public interface IFocusBackend
{
    nint GetForegroundWindow();
    bool TryAxRaise(nint window);
    void Activate(nint window);

    /// <summary>
    /// Optional fast-path: when the caller knows the target process id, the backend can
    /// raise the app via OS-specific APIs (NSRunningApplication.activate on macOS,
    /// SetForegroundWindow + AttachThreadInput on Windows). Default falls through to the
    /// handle-based path for backends that haven't implemented this yet.
    /// </summary>
    void ActivateProcess(int processId) { }
}
