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

        // ponytail: GetForegroundWindow ran outside the try-catch, so any
        // exception there leaked the gate process-wide until restart.
        // Bring it inside.
        nint prev;
        try
        {
            prev = _backend.GetForegroundWindow();
            // 1:1 OCCU prepareAppForGlobalPointerInput
            // (InputSimulation.swift L48-56):
            //   if raiseAppWindowViaAccessibility(pid) { sleep 120ms; return }
            //   else { activate([.activateAllWindows]); sleep 250ms }
            // OCCU short-circuits after a successful AXRaise — the
            // activate-all-windows leg only runs as fallback when AX
            // refused. We previously ran BOTH legs unconditionally
            // (extra 250ms per click); match OCCU.
            if (_backend.TryAxRaise(targetWindow))
            {
                Thread.Sleep(UpstreamConstants.FocusAxRaiseDelay);
            }
            else
            {
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
            // ponytail: don't restore the previous foreground app. OCCU
            // doesn't (ComputerUseService.swift never calls back into
            // NSRunningApplication.activate after prepareAppForGlobalPointer-
            // Input), and on SwiftUI apps where AX no-ops force the
            // global event-tap path, restoring immediately after the
            // event post yanked focus away while the SwiftUI gesture
            // recognizer was still settling — middle clicks in a 5-tap
            // burst silently dropped. Leaving the target foreground is
            // the cost of accuracy; matches OCCU's behavior. `prev` and
            // `backend` are now unused on this path.
            _ = prev;
            _ = backend;
            gate.Release();
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
