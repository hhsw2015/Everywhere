using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Everywhere.Interop;

namespace Everywhere.Views.Whiteboard;

/// <summary>
/// Full-screen transparent overlay capturing whiteboard strokes.
///
/// UX: hotkey opens this; user draws any number of strokes (mouse-down /
/// drag / up = one stroke). Release of the hotkey, or Enter / clicking the
/// "Done" button, commits. Esc cancels.
///
/// Strokes are captured in screen-pixel coordinates so the parser/snapper
/// outputs match a11y BoundingRectangle directly.
/// </summary>
public sealed class WhiteboardOverlay : ScreenSelectionTransparentWindow
{
    private readonly PixelRect _screenBounds;
    private readonly double _scale;
    private readonly Canvas _drawingCanvas;
    private readonly TaskCompletionSource<WhiteboardCaptureResult> _result = new();
    private readonly List<List<Point>> _strokes = [];
    private readonly List<List<double>> _strokeTimestamps = [];
    private readonly DateTimeOffset _epoch = DateTimeOffset.UtcNow;

    private List<Point>? _activeStrokeRaw;
    private List<double>? _activeStrokeTs;
    private Polyline? _activeStrokePolyline;
    private bool _committed;

    public WhiteboardOverlay(PixelRect screenBounds)
    {
        _screenBounds = screenBounds;

        // Transparent first; if the platform refuses, fall back to a barely
        // visible tint instead of a full opaque window. Some compositors
        // (and Avalonia on certain macOS GPUs) silently fall back to opaque
        // when only Transparent is hinted, which is what was making the
        // whole screen go white.
        Background = new SolidColorBrush(Colors.Black, 0.05);
        Cursor = new Cursor(StandardCursorType.Cross);
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
        ];
        SystemDecorations = SystemDecorations.None;

        _drawingCanvas = new Canvas
        {
            Background = Brushes.Transparent,
        };

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1A1A1A"), 0.85),
            BorderBrush = new SolidColorBrush(Color.Parse("#FFFFCC00")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 24, 24, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 13,
                Text = "Whiteboard — draw gestures (○ ─ → ✗). Enter = send, Esc = cancel.",
            },
        };

        Content = new Panel
        {
            Children =
            {
                _drawingCanvas,
                hint,
            },
        };

        SetPlacement(screenBounds, out _scale);

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;
        Closed += (_, _) => CompleteIfPending(canceled: true);
    }

    public Task<WhiteboardCaptureResult> ResultTask => _result.Task;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        _activeStrokeRaw = [p];
        _activeStrokeTs = [Elapsed()];
        _activeStrokePolyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.Parse("#FFE53935")),
            StrokeThickness = 3,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            Points = { p },
        };
        _drawingCanvas.Children.Add(_activeStrokePolyline);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeStrokeRaw is null || _activeStrokePolyline is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (_activeStrokeRaw.Count > 0)
        {
            var last = _activeStrokeRaw[^1];
            // skip pixels < 1 device-pixel apart to keep stroke compact
            var dx = last.X - p.X; var dy = last.Y - p.Y;
            if (dx * dx + dy * dy < 1.0) return;
        }
        _activeStrokeRaw.Add(p);
        _activeStrokeTs!.Add(Elapsed());
        _activeStrokePolyline.Points.Add(p);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_activeStrokeRaw is null) return;
        if (_activeStrokeRaw.Count >= 2)
        {
            _strokes.Add(_activeStrokeRaw);
            _strokeTimestamps.Add(_activeStrokeTs!);
        }
        else if (_activeStrokePolyline is not null)
        {
            // single-point clicks: drop the visual too
            _drawingCanvas.Children.Remove(_activeStrokePolyline);
        }
        _activeStrokeRaw = null;
        _activeStrokeTs = null;
        _activeStrokePolyline = null;
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
                Commit();
                e.Handled = true;
                break;
            case Key.Z when (e.KeyModifiers & KeyModifiers.Meta) != 0
                          || (e.KeyModifiers & KeyModifiers.Control) != 0:
                UndoLastStroke();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Programmatic commit (used by hotkey-release or "Done" button).
    /// Safe to call multiple times.
    /// </summary>
    public void Commit()
    {
        // Convert client-space points back to screen pixels
        var converted = new List<(IReadOnlyList<(double X, double Y, double T)> Points, int _)>();
        for (var i = 0; i < _strokes.Count; i++)
        {
            var pts = _strokes[i];
            var ts = _strokeTimestamps[i];
            var screenPts = new (double X, double Y, double T)[pts.Count];
            for (var j = 0; j < pts.Count; j++)
            {
                screenPts[j] = (
                    _screenBounds.X + pts[j].X * _scale,
                    _screenBounds.Y + pts[j].Y * _scale,
                    ts[j]);
            }
            converted.Add((screenPts, i));
        }
        var strokes = converted.Select(c => c.Points).ToArray();
        CompleteIfPending(canceled: false, strokes: strokes);
        Dispatcher.UIThread.Post(Close);
    }

    public bool HasStrokes => _strokes.Count > 0;

    private void UndoLastStroke()
    {
        if (_strokes.Count == 0) return;
        _strokes.RemoveAt(_strokes.Count - 1);
        _strokeTimestamps.RemoveAt(_strokeTimestamps.Count - 1);
        // The last child of the canvas is the most recent committed stroke
        // (active stroke is null at the time Undo is allowed).
        if (_drawingCanvas.Children.Count > 0)
        {
            _drawingCanvas.Children.RemoveAt(_drawingCanvas.Children.Count - 1);
        }
    }

    private double Elapsed() => (DateTimeOffset.UtcNow - _epoch).TotalMilliseconds;

    private void CompleteIfPending(bool canceled,
                                    IReadOnlyList<IReadOnlyList<(double X, double Y, double T)>>? strokes = null)
    {
        if (_committed) return;
        _committed = true;
        _result.TrySetResult(new WhiteboardCaptureResult(canceled, strokes ?? []));
    }
}

public sealed record WhiteboardCaptureResult(
    bool Canceled,
    IReadOnlyList<IReadOnlyList<(double X, double Y, double T)>> Strokes);
