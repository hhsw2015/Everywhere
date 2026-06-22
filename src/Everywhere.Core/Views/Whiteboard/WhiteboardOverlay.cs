using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Everywhere.Views.Whiteboard;

/// <summary>
/// Whiteboard overlay — same approach as <see cref="ScreenSelectionMaskWindow"/>:
/// inherits <see cref="ScreenSelectionTransparentWindow"/>, uses Background =
/// ImageBrush(screenshot) so the user visually sees the screen, then a Panel
/// of children for the dim layer, the drawing canvas, and a hint label.
/// </summary>
public sealed class WhiteboardOverlay : ScreenSelectionTransparentWindow
{
    private readonly PixelRect _screenBounds;
    private readonly double _scale;
    private readonly Canvas _drawingCanvas;
    private readonly Border _dimBorder;
    private readonly TaskCompletionSource<WhiteboardCaptureResult> _result = new();
    private readonly List<List<Point>> _strokes = [];
    private readonly List<List<double>> _strokeTimestamps = [];
    private readonly DateTimeOffset _epoch = DateTimeOffset.UtcNow;

    private List<Point>? _activeStrokeRaw;
    private List<double>? _activeStrokeTs;
    private Avalonia.Controls.Shapes.Path? _activePath;
    private bool _committed;
    private Bitmap? _ownedBackground;
    private KeyModifiers _enterKeyModifiers;

    public WhiteboardOverlay(PixelRect screenBounds, Bitmap? backgroundImage = null,
                              IReadOnlyList<string>? stashedSummaries = null)
    {
        _screenBounds = screenBounds;
        _ownedBackground = backgroundImage;

        _dimBorder = new Border
        {
            // Dim layer that signals 'overlay active'; its Background is
            // assigned below. IsHitTestVisible=false so pointer events
            // fall through to the canvas behind it.
            IsHitTestVisible = false,
        };

        _drawingCanvas = new Canvas
        {
            Background = Brushes.Transparent,
            // Canvas defaults to 0×0 measure when it has no children, so it
            // would never render polylines drawn into it after-the-fact.
            // Stretch to fill the parent Panel.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var hintLines = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical };
        if (stashedSummaries is { Count: > 0 })
        {
            hintLines.Children.Add(new TextBlock
            {
                Foreground = new SolidColorBrush(Color.Parse("#FFFFCC00")),
                FontSize = 13,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                Text = $"Continuing session — {stashedSummaries.Count} stashed:",
            });
            const int MaxStashedSummariesShown = 8;
            for (var i = 0; i < stashedSummaries.Count && i < MaxStashedSummariesShown; i++)
            {
                hintLines.Children.Add(new TextBlock
                {
                    Foreground = Brushes.White,
                    FontSize = 12,
                    Margin = new Thickness(0, 1, 0, 0),
                    Text = $"  {i + 1}. {stashedSummaries[i]}",
                });
            }
            if (stashedSummaries.Count > MaxStashedSummariesShown)
            {
                hintLines.Children.Add(new TextBlock
                {
                    Foreground = new SolidColorBrush(Color.Parse("#FFAAAAAA")),
                    FontSize = 11,
                    Text = $"  + {stashedSummaries.Count - MaxStashedSummariesShown} more",
                });
            }
            hintLines.Children.Add(new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
                Text = "Enter = send all  |  Shift+Enter = add more  |  Esc = cancel this batch",
            });
        }
        else
        {
            hintLines.Children.Add(new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 13,
                Text = "Whiteboard — draw gestures (○ ─ → ✗).",
            });
            hintLines.Children.Add(new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                Text = "Enter = send  |  Shift+Enter = save & continue across screens  |  Esc = cancel",
            });
        }
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1A1A1A"), 0.92),
            BorderBrush = new SolidColorBrush(Color.Parse("#FFFFCC00")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 24, 24, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = hintLines,
        };

        Content = new Panel
        {
            Children =
            {
                _dimBorder,
                _drawingCanvas,
                hint,
            },
        };

        SetPlacement(screenBounds, out _scale);

        // Real transparency — base class already wires Transparent +
        // TransparencyLevelHint = Transparent. The user draws directly on
        // top of the live screen content, no static screenshot underneath.
        // Eliminates the 'frame shrink / screen jolt' caused by ImageBrush
        // re-rasterization vs the OS's native rendering pipeline. The dim
        // border below provides a subtle 'overlay active' visual cue.
        Background = Brushes.Transparent;
        _dimBorder.Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0));
        // Transparent overlay no longer renders any constructor-supplied
        // bitmap; dispose to avoid leaking a caller-passed Bitmap.
        try { backgroundImage?.Dispose(); } catch { /* swallow */ }
        _ownedBackground = null;

        Opened += (_, _) => Focus();
        KeyDown += OnKeyDown;

        // AddHandler with handledEventsToo=true so we receive pointer events
        // even if a child marks them Handled. Routing on macOS Avalonia is
        // unreliable for both bubbling and class-handler overrides; this
        // listens at the canvas with both Tunnel and Bubble strategies, so
        // we get the event no matter which way it travels.
        _drawingCanvas.AddHandler(
            InputElement.PointerPressedEvent,
            OnCanvasPointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);
        _drawingCanvas.AddHandler(
            InputElement.PointerMovedEvent,
            OnCanvasPointerMoved,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);
        _drawingCanvas.AddHandler(
            InputElement.PointerReleasedEvent,
            OnCanvasPointerReleased,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);
        Closed += (_, _) =>
        {
            CompleteIfPending(canceled: true);
            Background = Brushes.Black;
            var owned = _ownedBackground;
            _ownedBackground = null;
            try { owned?.Dispose(); }
            catch { /* swallow */ }
        };
    }

    public Task<WhiteboardCaptureResult> ResultTask => _result.Task;

    /// <summary>
    /// Replace the dim background with the captured screenshot AFTER the
    /// overlay has opened. Caller passes the bitmap once their async
    /// capture pipeline returns; ownership transfers to the overlay (it
    /// will dispose on Closed).
    /// </summary>
    public void SetBackgroundLater(Bitmap bitmap)
    {
        // Transparent overlay does NOT render screenshots. Dispose the
        // incoming bitmap immediately so the API doesn't accumulate
        // unrendered bitmaps in memory across repeated calls.
        if (_committed) { bitmap.Dispose(); return; }
        Dispatcher.UIThread.Post(() =>
        {
            // Dispose any prior held bitmap (defensive — there should be
            // none, but a stale path may have set one).
            var prior = _ownedBackground;
            _ownedBackground = null;
            try { prior?.Dispose(); } catch { /* swallow */ }
            bitmap.Dispose();
        });
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_drawingCanvas).Properties.IsLeftButtonPressed) return;
        // Defensive: a previous stroke may not have been released cleanly
        // (capture is intentionally not used, so PointerReleased is not
        // guaranteed when window deactivates / focus is lost mid-drag).
        // Drop any orphaned in-progress path before starting a new one.
        if (_activePath is not null)
        {
            _drawingCanvas.Children.Remove(_activePath);
            _activePath = null;
        }
        _activeStrokeRaw = null;
        _activeStrokeTs = null;
        var p = e.GetPosition(_drawingCanvas);
        _activeStrokeRaw = [p];
        _activeStrokeTs = [Elapsed()];
        _activePath = new Avalonia.Controls.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.Parse("#FFE53935")),
            StrokeThickness = 4,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Data = BuildGeometry(_activeStrokeRaw),
        };
        _drawingCanvas.Children.Add(_activePath);
        e.Handled = true;
    }

    private static Geometry BuildGeometry(IReadOnlyList<Point> pts)
    {
        if (pts.Count == 0) return new StreamGeometry();
        var sg = new StreamGeometry();
        using var ctx = sg.Open();
        if (pts.Count == 1)
        {
            // Zero-length figure with PenLineCap.Round renders a filled dot
            // of diameter == StrokeThickness — visually consistent with
            // the round caps at every stroke endpoint.
            ctx.BeginFigure(pts[0], false);
            ctx.LineTo(pts[0]);
            ctx.EndFigure(false);
        }
        else
        {
            ctx.BeginFigure(pts[0], false);
            for (var i = 1; i < pts.Count; i++) ctx.LineTo(pts[i]);
            ctx.EndFigure(false);
        }
        return sg;
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeStrokeRaw is null || _activePath is null) return;
        if (!e.GetCurrentPoint(_drawingCanvas).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(_drawingCanvas);
        if (_activeStrokeRaw.Count > 0)
        {
            var last = _activeStrokeRaw[^1];
            var dx = last.X - p.X; var dy = last.Y - p.Y;
            if (dx * dx + dy * dy < 1.0) return;
        }
        _activeStrokeRaw.Add(p);
        _activeStrokeTs!.Add(Elapsed());
        // Reassign Data triggers a rebuild + render — the reliable
        // refresh path on macOS Avalonia (Polyline.Points mutation does not).
        _activePath.Data = BuildGeometry(_activeStrokeRaw);
        e.Handled = true;
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_activeStrokeRaw is null) return;
        if (_activeStrokeRaw.Count >= 2)
        {
            _strokes.Add(_activeStrokeRaw);
            _strokeTimestamps.Add(_activeStrokeTs!);
        }
        else if (_activePath is not null)
        {
            _drawingCanvas.Children.Remove(_activePath);
        }
        _activeStrokeRaw = null;
        _activeStrokeTs = null;
        _activePath = null;
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CompleteIfPending(canceled: true);
                Dispatcher.UIThread.Post(Close);
                e.Handled = true;
                break;
            case Key.Enter:
                // Shift+Enter = keep session open across screens (Append),
                // plain Enter = commit and switch to chat (Set + jump).
                // Capture the raw modifier set so the orchestrator can log
                // diagnostics when Shift+Enter does NOT make it through —
                // some macOS keyboard layouts swallow Shift on Enter.
                _enterKeyModifiers = e.KeyModifiers;
                Commit(continueSession: (e.KeyModifiers & KeyModifiers.Shift) != 0);
                e.Handled = true;
                break;
            case Key.Z when (e.KeyModifiers & KeyModifiers.Meta) != 0
                          || (e.KeyModifiers & KeyModifiers.Control) != 0:
                UndoLastStroke();
                e.Handled = true;
                break;
        }
    }

    public void Commit(bool continueSession = false)
    {
        // Coordinate convention used downstream (snapper / OCR):
        //   a11y BoundingRectangle is exposed as PixelRect but its values
        //   come straight from NSPoint, i.e. they are in DIP/points on
        //   macOS — NOT physical pixels. We must therefore emit stroke
        //   points in the SAME coordinate space.
        //
        //   _screenBounds itself is in DIP (NSScreenVisualElement returns
        //   NSScreen.Frame values without scaling), and canvas points are
        //   already in DIP (Avalonia layout space). So the conversion is
        //   just: screen_point = _screenBounds.origin + canvas_point.
        //   Multiplying by DesktopScaling was the long-standing bug —
        //   it pushed stroke y into physical-pixel space and the snapper
        //   then could not match it against the (DIP-valued) a11y bboxes.
        // Use the WINDOW'S ACTUAL Position, not screenBounds origin. macOS
        // LSUIElement apps cannot cover the menu bar, so even when we
        // request screenBounds.Position = (0,0), the OS clips the window
        // to (0, 30). Canvas y=0 corresponds to screen y=Position.Y, not
        // 0. Diagnostic confirmed Position=(0,30) for a screen origin
        // (0,0) request, producing a 30px upward error in stroke y.
        var origin = Position;
        var converted = new List<IReadOnlyList<(double X, double Y, double T)>>();
        for (var i = 0; i < _strokes.Count; i++)
        {
            var pts = _strokes[i];
            var ts = _strokeTimestamps[i];
            var screenPts = new (double X, double Y, double T)[pts.Count];
            for (var j = 0; j < pts.Count; j++)
            {
                screenPts[j] = (
                    origin.X + pts[j].X,
                    origin.Y + pts[j].Y,
                    ts[j]);
            }
            converted.Add(screenPts);
        }
        CompleteIfPending(canceled: false, strokes: converted,
                          windowPos: Position, screenBounds: _screenBounds,
                          continueSession: continueSession);
        Dispatcher.UIThread.Post(Close);
    }

    public bool HasStrokes => _strokes.Count > 0;

    private void UndoLastStroke()
    {
        if (_strokes.Count == 0) return;
        _strokes.RemoveAt(_strokes.Count - 1);
        _strokeTimestamps.RemoveAt(_strokeTimestamps.Count - 1);
        if (_drawingCanvas.Children.Count > 0)
        {
            _drawingCanvas.Children.RemoveAt(_drawingCanvas.Children.Count - 1);
        }
    }

    private double Elapsed() => (DateTimeOffset.UtcNow - _epoch).TotalMilliseconds;

    private void CompleteIfPending(bool canceled,
                                    IReadOnlyList<IReadOnlyList<(double X, double Y, double T)>>? strokes = null,
                                    Avalonia.PixelPoint? windowPos = null,
                                    Avalonia.PixelRect? screenBounds = null,
                                    bool continueSession = false)
    {
        if (_committed) return;
        _committed = true;
        // Always populate diagnostic fields, even on cancel paths, so
        // callers can distinguish 'not measured' from a real (0,0) origin.
        _result.TrySetResult(new WhiteboardCaptureResult(
            canceled, strokes ?? [],
            WindowPosition: windowPos ?? Position,
            ScreenBounds: screenBounds ?? _screenBounds,
            ContinueSession: continueSession,
            EnterKeyModifiers: _enterKeyModifiers));
    }
}

public sealed record WhiteboardCaptureResult(
    bool Canceled,
    IReadOnlyList<IReadOnlyList<(double X, double Y, double T)>> Strokes,
    /// <summary>Window's actual Position when overlay was open. For diagnosing
    /// canvas/screen coordinate mismatches.</summary>
    Avalonia.PixelPoint WindowPosition = default,
    /// <summary>screenBounds passed to the overlay (the source-of-truth for
    /// stroke coord conversion).</summary>
    Avalonia.PixelRect ScreenBounds = default,
    /// <summary>True when the user pressed Shift+Enter — keep the session
    /// open across screens. Orchestrator should Append (not Set) and
    /// must NOT switch to the chat window; the user will press the
    /// Whiteboard hotkey again after scrolling.</summary>
    bool ContinueSession = false,
    /// <summary>Raw KeyModifiers observed when the user pressed Enter,
    /// for diagnosing cases where Shift+Enter is reported without the
    /// expected Shift bit.</summary>
    Avalonia.Input.KeyModifiers EnterKeyModifiers = default);
