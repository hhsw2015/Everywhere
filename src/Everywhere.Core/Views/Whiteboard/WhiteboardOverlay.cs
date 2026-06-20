using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Everywhere.Views.Whiteboard;

/// <summary>
/// Full-screen overlay capturing whiteboard strokes.
///
/// Layout: a single Image showing the captured screenshot (full-window,
/// dimmed via Opacity), a Canvas on TOP receiving pointer events and
/// rendering strokes, and a top-right hint label. Pointer events go to
/// the Canvas because it's the topmost hit-testable element with a
/// transparent background; the Image is non-hit-testable so it doesn't
/// steal events.
/// </summary>
public sealed class WhiteboardOverlay : Window
{
    private readonly PixelRect _screenBounds;
    private double _scale = 1.0;
    private readonly Canvas _drawingCanvas;
    private readonly TaskCompletionSource<WhiteboardCaptureResult> _result = new();
    private readonly List<List<Point>> _strokes = [];
    private readonly List<List<double>> _strokeTimestamps = [];
    private readonly DateTimeOffset _epoch = DateTimeOffset.UtcNow;

    private List<Point>? _activeStrokeRaw;
    private List<double>? _activeStrokeTs;
    private Avalonia.Collections.AvaloniaList<Point>? _activePolylinePoints;
    private Polyline? _activeStrokePolyline;
    private bool _committed;
    private IDisposable? _ownedBackground;

    public WhiteboardOverlay(PixelRect screenBounds, Bitmap? backgroundImage = null)
    {
        _screenBounds = screenBounds;

        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        CanMaximize = false;
        CanMinimize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        BorderThickness = new Thickness(0);
        SizeToContent = SizeToContent.Manual;
        Background = Brushes.Black;     // base — masked by the screenshot Image

        _drawingCanvas = new Canvas
        {
            Background = Brushes.Transparent, // hit-test on but visually transparent
        };

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1A1A1A"), 0.92),
            BorderBrush = new SolidColorBrush(Color.Parse("#FFFFCC00")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 24, 24, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            IsHitTestVisible = false,    // never steal pointer events
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 13,
                Text = "Whiteboard — draw gestures (○ ─ → ✗). Enter = send, Esc = cancel.",
            },
        };

        var children = new Avalonia.Controls.Controls();

        if (backgroundImage is not null)
        {
            // Image as a child (not as Background ImageBrush) so we can
            // make it non-hit-testable individually. Slight dim via Opacity
            // tells the user the screen is "frozen".
            var img = new Image
            {
                Source = backgroundImage,
                Stretch = Stretch.Fill,    // Image at full window size
                Opacity = 0.85,             // 15% dim — content readable
                IsHitTestVisible = false,   // pointer events go to canvas
            };
            children.Add(img);
        }
        else
        {
            // No screenshot: dim solid backplate so the user knows the
            // overlay is active.
            children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)),
                IsHitTestVisible = false,
            });
        }

        children.Add(_drawingCanvas);
        children.Add(hint);
        Content = new Panel { Children = { } }.SetWith(children);

        Position = screenBounds.Position;
        _scale = DesktopScaling > 0 ? DesktopScaling : 1.0;
        Width = screenBounds.Width / _scale;
        Height = screenBounds.Height / _scale;

        Opened += (_, _) =>
        {
            var liveScale = DesktopScaling > 0 ? DesktopScaling : 1.0;
            if (Math.Abs(liveScale - _scale) > 0.01)
            {
                _scale = liveScale;
                Width = screenBounds.Width / _scale;
                Height = screenBounds.Height / _scale;
            }
            // Make sure we receive keyboard events the moment the window appears.
            Focus();
        };

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;
        Closed += (_, _) =>
        {
            CompleteIfPending(canceled: true);
            var owned = _ownedBackground;
            _ownedBackground = null;
            try { owned?.Dispose(); }
            catch { /* swallow — teardown must not throw */ }
        };

        _ownedBackground = backgroundImage;
    }

    public Task<WhiteboardCaptureResult> ResultTask => _result.Task;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
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
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
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
        // Force re-render on every platform — cheap, eliminates the macOS
        // refresh skip and there's no visible difference on Win/Linux.
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

internal static class PanelChildrenExt
{
    public static T SetWith<T>(this T panel, Avalonia.Controls.Controls children)
        where T : Panel
    {
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }
}

public sealed record WhiteboardCaptureResult(
    bool Canceled,
    IReadOnlyList<IReadOnlyList<(double X, double Y, double T)>> Strokes);
