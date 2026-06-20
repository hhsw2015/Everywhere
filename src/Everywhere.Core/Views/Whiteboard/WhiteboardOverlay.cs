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
    private Avalonia.Collections.AvaloniaList<Point>? _activePolylinePoints;
    private Polyline? _activeStrokePolyline;
    private bool _committed;
    private Bitmap? _ownedBackground;

    public WhiteboardOverlay(PixelRect screenBounds, Bitmap? backgroundImage = null)
    {
        _screenBounds = screenBounds;
        _ownedBackground = backgroundImage;

        _dimBorder = new Border
        {
            Background = Brushes.Black,
            Opacity = 0.20,            // 20% dim — content readable
            IsHitTestVisible = false,  // pointer events go to canvas
        };

        _drawingCanvas = new Canvas
        {
            Background = Brushes.Transparent,
        };

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
                _dimBorder,
                _drawingCanvas,
                hint,
            },
        };

        SetPlacement(screenBounds, out _scale);

        // ImageBrush mode matches ScreenSelectionMaskWindow.SetImage — uses
        // the captured screenshot as the window's actual Background.
        if (backgroundImage is not null)
        {
            Background = new ImageBrush(backgroundImage);
        }

        Opened += (_, _) => Focus();

        // Pointer handlers on the CANVAS, not the Window — non-canvas children
        // (dim Border, hint) have IsHitTestVisible=false, so the canvas is the
        // pointer target, but Avalonia delivers PointerPressed to the leaf,
        // not necessarily bubbling up to Window in time for us to receive it.
        // Subscribing directly on the canvas guarantees we get the events.
        _drawingCanvas.PointerPressed += OnPointerPressed;
        _drawingCanvas.PointerMoved += OnPointerMoved;
        _drawingCanvas.PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;
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

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_drawingCanvas).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(_drawingCanvas);
        _activeStrokeRaw = [p];
        _activeStrokeTs = [Elapsed()];
        _activePolylinePoints = new Avalonia.Collections.AvaloniaList<Point> { p };
        _activeStrokePolyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.Parse("#FFE53935")),
            StrokeThickness = 4,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            Points = _activePolylinePoints,
            IsHitTestVisible = false,
        };
        _drawingCanvas.Children.Add(_activeStrokePolyline);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeStrokeRaw is null
            || _activeStrokePolyline is null
            || _activePolylinePoints is null) return;
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
        _activePolylinePoints.Add(p);
        _activeStrokePolyline.InvalidateVisual();
        e.Handled = true;
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
            _drawingCanvas.Children.Remove(_activeStrokePolyline);
        }
        _activeStrokeRaw = null;
        _activeStrokeTs = null;
        _activeStrokePolyline = null;
        _activePolylinePoints = null;
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

    public void Commit()
    {
        var converted = new List<IReadOnlyList<(double X, double Y, double T)>>();
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
            converted.Add(screenPts);
        }
        CompleteIfPending(canceled: false, strokes: converted);
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
