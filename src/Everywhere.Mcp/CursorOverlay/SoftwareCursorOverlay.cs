// Behavioral port of:
//   packages/OpenComputerUseKit/Sources/OpenComputerUseKit/SoftwareCursorOverlay.swift
// from iFurySt/open-codex-computer-use.
//
// The Swift original is an NSPanel + NSView pair with Core Animation
// timers, configureOrdering against arbitrary NSWindow target windows,
// and CGWindowListCopyWindowInfo hit-testing for stacking — APIs that
// only exist on AppKit. We keep the public surface identical
// (MoveCursor / PulseClick / Settle / Reset) and replace the windowing
// layer with a full-screen, transparent, click-through Avalonia Window
// driven by Skia. The motion brain (CursorMotionModel.cs) and glyph
// drawing (CursorGlyphRenderer.cs) are line-by-line ports and are
// reused unchanged.
//
// We render the cursor at TipPosition in screen coordinates (Avalonia
// PixelPoint) using DispatcherTimer at ~60 Hz. The single-glyph canvas
// is a fixed 126×126 region centered on the current tip — same window
// size as the upstream CursorPanel.

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace Everywhere.Mcp.CursorOverlay;

public sealed class SoftwareCursorOverlay : IAsyncDisposable
{
    private const double WindowWidth = CursorGlyphMetrics.WindowWidth;
    private const double WindowHeight = CursorGlyphMetrics.WindowHeight;
    private static readonly Point TipAnchor = CursorGlyphMetrics.TipAnchor;
    private static readonly double RenderBaseHeading = CursorGlyphMetrics.TargetNeutralHeading;
    // Upstream: -1 because AppKit windows are y-up but the motion brain
    // is y-down. Avalonia is already y-down so 1 here.
    private const double RenderYAxisMultiplier = 1;
    private const double PostInteractionIdleTimeoutSeconds = 30;
    private const double IdleRotationAmplitude = 0.09;

    private CursorWindow? _window;
    private CursorVisualDynamicsState? _visualDynamicsState;
    private Point? _restingTipPosition;
    private Point? _displayedTipPosition;
    private DispatcherTimer? _idleTimer;
    private DispatcherTimer? _hideTimer;
    // ponytail: AnimateMove / AnimateClickPulse used to leak their
    // DispatcherTimer when a new MoveCursor / PulseClick landed mid-
    // animation — the prior tick handler kept firing and fought the
    // new one. Track them so the next call can Stop the old one.
    private DispatcherTimer? _moveTimer;
    private DispatcherTimer? _pulseTimer;
    // ponytail: optional NSWindow shim. Only the Mac backend has a
    // real implementation; Windows/null leaves the overlay as a plain
    // Topmost Avalonia window, which is the previous behavior.
    private readonly Everywhere.Interop.IWindowHelper? _windowHelper;
    private bool _nativeOverlayConfigured;

    public SoftwareCursorOverlay(Everywhere.Interop.IWindowHelper? windowHelper = null)
    {
        _windowHelper = windowHelper;
    }

    private double _idlePhase;
    private double _currentClickProgress;
    private double _currentRotation;
    private bool _disposed;
    private readonly object _gate = new();

    public bool IsEnabled { get; private set; } = true;

    public void Disable() { lock (_gate) { IsEnabled = false; Reset(); } }
    public void Enable()  { lock (_gate) IsEnabled = true; }

    /// <summary>
    /// Move the soft cursor and return a Task that completes when the
    /// spring animation has visibly converged on the target — mirrors
    /// OCCU's DispatchQueue.main.sync + synchronous moveCursor. Callers
    /// awaiting this can sequence a follow-up action (AX click, AX
    /// state change) so the cursor appears to "arrive, then act"
    /// instead of the target reacting before the cursor catches up.
    /// </summary>
    public Task MoveCursorAsync(Point target)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => MoveCursor(target, tcs));
        return tcs.Task;
    }

    public void MoveCursor(Point target) => MoveCursor(target, completion: null);

    private void MoveCursor(Point target, TaskCompletionSource<bool>? completion)
    {
        if (!IsEnabled || _disposed)
        {
            completion?.TrySetResult(false);
            return;
        }
        EnsureWindow();
        StopIdleAnimation();
        CancelPendingHide();
        var constrained = ClampTipPosition(target);
        var isFreshStart = _displayedTipPosition is null;
        var startPoint = _displayedTipPosition ?? DefaultInitialTipPosition();
        var now = NowSeconds();

        _window!.Show();
        if (!_nativeOverlayConfigured && _windowHelper is not null)
        {
            try { _windowHelper.ConfigureAsCursorOverlay(_window); _nativeOverlayConfigured = true; }
            catch { /* overlay is best-effort. */ }
        }
        ApplyPendingOrdering();
        if (isFreshStart)
        {
            _visualDynamicsState = CursorVisualDynamicsState.At(startPoint, now);
            PlaceCursor(InitialRenderState(startPoint), clickProgress: 0);
        }
        else
        {
            SeedVisualDynamicsIfNeeded(startPoint, now);
            var (state, render) = AdvanceVisualDynamics(startPoint, now);
            _visualDynamicsState = state;
            PlaceCursor(render, clickProgress: 0);
        }

        if (Distance(startPoint, constrained) > 2)
        {
            AnimateMove(startPoint, constrained, completion);
        }
        else
        {
            // No motion needed — cursor already at target. Signal
            // completion immediately so the awaiter can proceed.
            completion?.TrySetResult(true);
        }
    }

    public Task PulseClickAsync(Point target, int clickCount = 1, bool rightButton = false)
    {
        Dispatcher.UIThread.Post(() => PulseClick(target, clickCount, rightButton));
        return Task.CompletedTask;
    }

    public void PulseClick(Point target, int clickCount = 1, bool rightButton = false)
    {
        if (!IsEnabled || _disposed) return;
        EnsureWindow();
        var constrained = ClampTipPosition(target);
        var now = NowSeconds();
        SeedVisualDynamicsIfNeeded(constrained, now);
        _restingTipPosition = constrained;
        AnimateClickPulse(constrained, Math.Max(clickCount, 1), rightButton);
        StartIdleAnimation();
        ScheduleHide(PostInteractionIdleTimeoutSeconds);
    }

    public Task SettleAsync(Point target)
    {
        Dispatcher.UIThread.Post(() => Settle(target));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 1:1 OCCU configureOrdering (SoftwareCursorOverlay.swift
    /// L319-345). Lift the overlay above the click's target app
    /// front window so the cursor stays visible if that app uses a
    /// non-default window level (panels, fullscreen views, etc.).
    /// Caller passes targetProcessId from the click trace; null →
    /// floating + plain front-most.
    ///
    /// Stashes the requested target — actual NSWindow ordering runs
    /// from ApplyPendingOrdering() inside MoveCursor, AFTER Show().
    /// Calling order(.above, ...) on a hidden window is silently
    /// ignored on AppKit, so we have to defer until the panel is
    /// on-screen.
    /// </summary>
    public void RaiseAboveTarget(int? targetProcessId)
    {
        if (_disposed || _windowHelper is null) return;
        _pendingTargetProcessId = targetProcessId;
        _hasPendingOrdering = true;
    }

    private int? _pendingTargetProcessId;
    private bool _hasPendingOrdering;

    private void ApplyPendingOrdering()
    {
        if (!_hasPendingOrdering || _windowHelper is null || _window is null) return;
        _hasPendingOrdering = false;
        try { _windowHelper.RaiseOverlayAboveTarget(_window, _pendingTargetProcessId); }
        catch { /* best-effort. */ }
    }

    public void Settle(Point target)
    {
        if (!IsEnabled || _disposed) return;
        EnsureWindow();
        var constrained = ClampTipPosition(target);
        _restingTipPosition = constrained;
        var (state, render) = AdvanceVisualDynamics(constrained, NowSeconds());
        _visualDynamicsState = state;
        PlaceCursor(render, clickProgress: 0);
        StartIdleAnimation();
        ScheduleHide(PostInteractionIdleTimeoutSeconds);
    }

    public void Reset()
    {
        StopIdleAnimation();
        CancelPendingHide();
        _moveTimer?.Stop(); _moveTimer = null;
        System.Threading.Interlocked.Exchange(ref _activeMoveCompletion, null)?.TrySetResult(false);
        _pulseTimer?.Stop(); _pulseTimer = null;
        _displayedTipPosition = null;
        _restingTipPosition = null;
        _visualDynamicsState = null;
        _currentClickProgress = 0;
        if (Dispatcher.UIThread.CheckAccess())
            _window?.Hide();
        else
            Dispatcher.UIThread.Post(() => _window?.Hide());
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;
        var w = new CursorWindow();
        w.SetCanvasSize(WindowWidth, WindowHeight);
        _window = w;
    }

    private void PlaceCursor(CursorVisualRenderState render, double clickProgress)
    {
        _displayedTipPosition = render.TipPosition;
        _currentRotation = render.Rotation;
        _currentClickProgress = clickProgress;
        _window?.UpdateRender(render, clickProgress);
        // Origin = tip - tipAnchor (rotated by neutral heading already baked in).
        var origin = new PixelPoint(
            (int)Math.Round(render.TipPosition.X - TipAnchor.X),
            (int)Math.Round(render.TipPosition.Y - TipAnchor.Y));
        _window?.SetOrigin(origin);
    }

    private TaskCompletionSource<bool>? _activeMoveCompletion;

    private void AnimateMove(Point start, Point end) => AnimateMove(start, end, completion: null);

    private void AnimateMove(Point start, Point end, TaskCompletionSource<bool>? completion)
    {
        // Upstream uses calibrated travel duration + spring progress
        // animator. We do the same — same parameters, same math.
        // ponytail: cancel any in-flight Move timer first; OCCU runs
        // moveCursor synchronously on main, so it can't race itself.
        // Our async API can — without this, two Move calls in quick
        // succession had two timers fighting over PlaceCursor.
        // Unblock any awaiter still hanging on the prior animation
        // so dispatcher.AX-path doesn't leak its FocusBorrow gate.
        // Atomic swap: if Reset() races on a worker thread, we want
        // exactly one of (this AnimateMove, that Reset) to win the
        // signal-then-replace; without Interlocked the new completion
        // could be installed before the prior is signaled.
        var prior = System.Threading.Interlocked.Exchange(ref _activeMoveCompletion, completion);
        prior?.TrySetResult(false);
        _moveTimer?.Stop();
        _moveTimer = null;

        var candidates = HeadingDrivenCursorMotionModel.MakeCandidates(
            start, end, bounds: null, startForward: CurrentForwardVector(), endForward: RestingForwardVector());
        var bestN = HeadingDrivenCursorMotionModel.ChooseBestCandidate(candidates);
        if (bestN is null)
        {
            // No motion candidate — short-circuit the await so caller
            // isn't blocked.
            completion?.TrySetResult(false);
            System.Threading.Interlocked.CompareExchange(ref _activeMoveCompletion, null, completion);
            return;
        }
        var best = bestN.Value;
        var path = best.Path;
        var duration = OfficialCursorMotionModel.CalibratedTravelDuration(Distance(start, end), best.Measurement);
        var springTargetDuration = OfficialCursorMotionModel.CloseEnoughTimeValue;
        var startTime = NowSeconds();
        double progress = 0;
        var springState = default(CursorMotionSpringState);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 60) };
        _moveTimer = timer;
        timer.Tick += (_, _) =>
        {
            // OCR fix: previous timer.Stop() doesn't preempt a tick
            // already queued in the dispatcher; that stale tick would
            // otherwise overwrite the new animation's PlaceCursor.
            // CRITICAL: also SetResult on the superseded completion so
            // its awaiter unblocks — otherwise an already-awaited Move
            // (e.g. ElementClickDispatcher.AX path's GetResult()) sits
            // forever, holds FocusBorrow, and every subsequent
            // get_app_context times out with "contention exceeded 5s".
            if (!ReferenceEquals(_moveTimer, timer))
            {
                timer.Stop();
                completion?.TrySetResult(false);
                System.Threading.Interlocked.CompareExchange(ref _activeMoveCompletion, null, completion);
                return;
            }
            var elapsed = NowSeconds() - startTime;
            var normalizedElapsed = Math.Clamp(elapsed / Math.Max(duration, 0.001), 0, 1);
            var springTime = normalizedElapsed * springTargetDuration;
            (progress, springState) = CursorMotionProgressAnimator.AdvanceTo(
                progress, 1, springState, CursorMotionSpringConfiguration.Official, springTime);
            var sample = path.Sample(progress);
            var (vstate, vrender) = AdvanceVisualDynamics(sample.Point, NowSeconds());
            _visualDynamicsState = vstate;
            PlaceCursor(vrender, clickProgress: 0);
            if (normalizedElapsed >= 1 || CursorMotionProgressAnimator.IsCloseEnough(progress))
            {
                timer.Stop();
                if (ReferenceEquals(_moveTimer, timer)) _moveTimer = null;
                var (s2, r2) = AdvanceVisualDynamics(end, NowSeconds());
                _visualDynamicsState = s2;
                PlaceCursor(r2, clickProgress: 0);
                completion?.TrySetResult(true);
                System.Threading.Interlocked.CompareExchange(ref _activeMoveCompletion, null, completion);
            }
        };
        timer.Start();
    }

    private void AnimateClickPulse(Point point, int clickCount, bool rightButton)
    {
        // ponytail: cancel any pulse in flight (mirrors AnimateMove).
        _pulseTimer?.Stop();
        _pulseTimer = null;

        var pulseBias = rightButton ? 0.82 : 1.0;
        var pulseIndex = 0;
        const double duration = 0.16;
        // OCCU SoftwareCursorOverlay.swift:574-576 — between repeats.
        const double interPulsePause = 0.05;
        var pulseStart = NowSeconds();
        bool inPause = false;
        double pauseStart = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 60) };
        _pulseTimer = timer;
        timer.Tick += (_, _) =>
        {
            // OCR fix: same stale-tick guard as AnimateMove.
            if (!ReferenceEquals(_pulseTimer, timer)) { timer.Stop(); return; }
            if (inPause)
            {
                if (NowSeconds() - pauseStart < interPulsePause) return;
                inPause = false;
                pulseStart = NowSeconds();
            }
            var elapsed = NowSeconds() - pulseStart;
            var rawProgress = Math.Min(Math.Max(elapsed / duration, 0), 1);
            var clickProgress = Math.Sin(rawProgress * Math.PI) * pulseBias;
            var (vs, vr) = AdvanceVisualDynamics(point, NowSeconds());
            _visualDynamicsState = vs;
            PlaceCursor(vr, clickProgress);
            if (rawProgress >= 1)
            {
                pulseIndex++;
                if (pulseIndex >= clickCount)
                {
                    timer.Stop();
                    if (ReferenceEquals(_pulseTimer, timer)) _pulseTimer = null;
                    return;
                }
                inPause = true;
                pauseStart = NowSeconds();
            }
        };
        timer.Start();
    }

    private void StartIdleAnimation()
    {
        StopIdleAnimation();
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 60) };
        var phaseStart = NowSeconds();
        _idleTimer.Tick += (_, _) =>
        {
            if (_restingTipPosition is not { } resting) { StopIdleAnimation(); return; }
            _idlePhase = NowSeconds() - phaseStart;
            var idleAngleOffset = Math.Sin(_idlePhase * 0.8) * IdleRotationAmplitude;
            var (vs, vr) = AdvanceVisualDynamics(resting, NowSeconds(), idleAngleOffset: idleAngleOffset);
            _visualDynamicsState = vs;
            PlaceCursor(vr, clickProgress: 0);
        };
        _idleTimer.Start();
    }

    private void StopIdleAnimation()
    {
        _idleTimer?.Stop();
        _idleTimer = null;
        _idlePhase = 0;
    }

    private void ScheduleHide(double seconds)
    {
        CancelPendingHide();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _hideTimer.Tick += (_, _) => { CancelPendingHide(); Reset(); };
        _hideTimer.Start();
    }

    private void CancelPendingHide()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
    }

    private (CursorVisualDynamicsState, CursorVisualRenderState) AdvanceVisualDynamics(
        Point target, double now, double idleAngleOffset = 0)
    {
        SeedVisualDynamicsIfNeeded(target, now);
        var state = _visualDynamicsState!.Value;
        return CursorVisualDynamicsAnimator.Advance(
            state, target, targetTime: now,
            idleAngleOffset: idleAngleOffset, baseHeading: RenderBaseHeading,
            renderYAxisMultiplier: RenderYAxisMultiplier);
    }

    private void SeedVisualDynamicsIfNeeded(Point at, double now)
    {
        if (_visualDynamicsState is null)
            _visualDynamicsState = CursorVisualDynamicsState.At(at, now);
    }

    private static CursorVisualRenderState InitialRenderState(Point at)
        => new(TipPosition: at, Rotation: 0,
            CursorBodyOffset: new Vector(0, 0),
            FogOffset: new Vector(0, 0),
            FogOpacity: 0.12, FogScale: 1);

    private Vector CurrentForwardVector()
    {
        var renderRotation = _currentRotation;
        var angle = -RenderBaseHeading - renderRotation;
        return new Vector(Math.Cos(angle), Math.Sin(angle));
    }
    private static Vector RestingForwardVector()
    {
        var angle = -RenderBaseHeading;
        return new Vector(Math.Cos(angle), Math.Sin(angle));
    }

    /// <summary>
    /// 1:1 OCCU SoftwareCursorOverlay.swift:774-789 — clamp the
    /// requested tip position into the visible frame of the screen
    /// containing the point (or main, or first). Without this a click
    /// destined for a window on a secondary display whose
    /// AppKit/Avalonia coordinate space crosses screen boundaries
    /// would render the cursor off-edge.
    /// </summary>
    private Point ClampTipPosition(Point target)
    {
        if (_window?.Screens is not { } screens) return target;
        var pt = new PixelPoint((int)target.X, (int)target.Y);
        var screen = screens.ScreenFromPoint(pt) ?? screens.Primary ?? (screens.All.Count > 0 ? screens.All[0] : null);
        if (screen is null) return target;
        var wa = screen.WorkingArea;
        var x = Math.Min(Math.Max(target.X, wa.X), wa.X + wa.Width);
        var y = Math.Min(Math.Max(target.Y, wa.Y), wa.Y + wa.Height);
        return new Point(x, y);
    }
    private static Point DefaultInitialTipPosition() => new(TipAnchor.X, TipAnchor.Y);
    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double NowSeconds() => Environment.TickCount64 / 1000.0;

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        StopIdleAnimation();
        CancelPendingHide();
        if (Dispatcher.UIThread.CheckAccess()) _window?.Close();
        else Dispatcher.UIThread.Post(() => _window?.Close());
        _window = null;
        return ValueTask.CompletedTask;
    }
}

// ---------------------------------------------------------------------
// Avalonia Window — equivalent of Swift CursorPanel + SoftwareCursorView.
// Borderless + transparent + click-through + topmost + no taskbar.
// Custom-rendered via OnRender into a Skia drawing context that calls
// CursorGlyphRenderer.
// ---------------------------------------------------------------------
internal sealed class CursorWindow : Window
{
    private readonly CursorRenderControl _control;

    public CursorWindow()
    {
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Click-through — pointer events fall through to whatever is
        // behind. Avalonia exposes this per-platform; the safe portable
        // form is to mark every input as not hit-testable.
        IsHitTestVisible = false;
        _control = new CursorRenderControl { IsHitTestVisible = false };
        Content = _control;
    }

    public void SetCanvasSize(double w, double h)
    {
        Width = w;
        Height = h;
        _control.Width = w;
        _control.Height = h;
    }

    public void SetOrigin(PixelPoint origin) => Position = origin;

    public void UpdateRender(CursorVisualRenderState render, double clickProgress)
    {
        _control.State = new CursorGlyphRenderState(
            Rotation: render.Rotation,
            CursorBodyOffset: render.CursorBodyOffset,
            FogOffset: render.FogOffset,
            FogOpacity: render.FogOpacity,
            FogScale: render.FogScale,
            ClickProgress: clickProgress);
        _control.InvalidateVisual();
    }
}

internal sealed class CursorRenderControl : Control
{
    public CursorGlyphRenderState State { get; set; }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.Custom(new GlyphDrawOperation(new Rect(0, 0, Bounds.Width, Bounds.Height), State));
    }
}

internal sealed class GlyphDrawOperation : ICustomDrawOperation
{
    private readonly Rect _bounds;
    private readonly CursorGlyphRenderState _state;

    public GlyphDrawOperation(Rect bounds, CursorGlyphRenderState state)
    {
        _bounds = bounds;
        _state = state;
    }

    public Rect Bounds => _bounds;
    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (leaseFeature is null) return;
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;
        canvas.Save();
        CursorGlyphRenderer.Draw(canvas, _bounds, _state);
        canvas.Restore();
    }
}
