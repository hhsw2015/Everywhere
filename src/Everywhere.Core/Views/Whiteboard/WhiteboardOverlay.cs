using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Everywhere.Interop;

namespace Everywhere.Views.Whiteboard;

/// <summary>
/// Full-screen overlay capturing whiteboard strokes.
///
/// Approach: take a screenshot of the screen FIRST (caller responsibility),
/// then show that screenshot as the window background. The user draws on
/// top of the screenshot — it visually feels like drawing on the screen,
/// without depending on Avalonia macOS Transparent which falls back to
/// opaque on some GPUs.
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
    private Polyline? _activeStrokePolyline;
    private bool _committed;

    public WhiteboardOverlay(PixelRect screenBounds, IImage? backgroundImage = null)
    {
        _screenBounds = screenBounds;

        // Window chrome off
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        CanMaximize = false;
        CanMinimize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        BorderThickness = new Thickness(0);
        SizeToContent = SizeToContent.Manual;

        // Use the captured screen image as the window background. Falls back
        // to a solid dim tint (so the user sees SOMETHING and knows the
        // overlay is active) if no image is supplied.
        if (backgroundImage is not null)
        {
            Background = new ImageBrush(backgroundImage)
            {
                Stretch = Stretch.Fill,
            };
        }
        else
        {
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        }

        Cursor = new Cursor(StandardCursorType.Cross);

        _drawingCanvas = new Canvas
        {
            Background = Brushes.Transparent, // hit-test transparent
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
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 13,
                Text = "Whiteboard — draw gestures (○ ─ → ✗). Enter = send, Esc = cancel.",
            },
        };

        // Subtle dim overlay over the screenshot so it's clear the screen
        // is "frozen" but the content is still readable.
        var dim = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)),
        };

        Content = new Panel
        {
            Children =
            {
                dim,
                _drawingCanvas,
                hint,
            },
        };

        Position = screenBounds.Position;
        Width = screenBounds.Width;
        Height = screenBounds.Height;

        Opened += (_, _) =>
        {
            // After window is on screen, DesktopScaling is correct
            _scale = DesktopScaling > 0 ? DesktopScaling : 1.0;
            Width = screenBounds.Width / _scale;
            Height = screenBounds.Height / _scale;
        };

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
        var p = e.GetPosition(_drawingCanvas);
        _activeStrokeRaw = [p];
        _activeStrokeTs = [Elapsed()];
        var pts = new Avalonia.Collections.AvaloniaList<Point> { p };
        _activeStrokePolyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.Parse("#FFE53935")),
            StrokeThickness = 3,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            Points = pts,
            IsHitTestVisible = false,
        };
        _drawingCanvas.Children.Add(_activeStrokePolyline);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeStrokeRaw is null || _activeStrokePolyline is null) return;
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
        // Replace Points with new list — AvaloniaList Add doesn't always
        // invalidate the Polyline geometry on macOS; reassigning forces
        // re-render.
        var pts = new Avalonia.Collections.AvaloniaList<Point>(_activeStrokeRaw);
        _activeStrokePolyline.Points = pts;
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
