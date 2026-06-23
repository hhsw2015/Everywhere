using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Everywhere.Interop;

namespace Everywhere.Views;

/// <summary>
/// Base window class for screen selection windows.
/// </summary>
public abstract class ScreenSelectionWindow : Window
{
    protected ScreenSelectionWindow()
    {
        Topmost = true;
        CanResize = false;
        CanMaximize = false;
        CanMinimize = false;
        ShowInTaskbar = false;
        BorderThickness = new Thickness(0);
        WindowStartupLocation = WindowStartupLocation.Manual;
    }
}

/// <summary>
/// Transparent window used for screen selection.
/// Provides methods to set placement based on screen bounds.
/// </summary>
public class ScreenSelectionTransparentWindow : ScreenSelectionWindow
{
    protected ScreenSelectionTransparentWindow()
    {
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Cross);
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SystemDecorations = SystemDecorations.None;
        SizeToContent = SizeToContent.Manual;
    }

    /// <summary>
    /// Sets the window placement based on the specified screen bounds.
    /// </summary>
    /// <param name="screenBounds"></param>
    /// <param name="scale"></param>
    protected void SetPlacement(PixelRect screenBounds, out double scale)
    {
        Position = screenBounds.Position;
        scale = DesktopScaling; // we must set Position first to get the correct scaling factor
        Width = screenBounds.Width / scale;
        Height = screenBounds.Height / scale;
    }
}

/// <summary>
/// Mask window that displays the overlay during screen selection.
/// </summary>
public sealed class ScreenSelectionMaskWindow : ScreenSelectionTransparentWindow
{
    private readonly Border _maskBorder;
    private readonly Border _elementBoundsBorder;
    private readonly Canvas _capturedLinksCanvas;
    private readonly PixelRect _screenBounds;
    private readonly double _scale;

    public ScreenSelectionMaskWindow(PixelRect screenBounds)
    {
        Content = new Panel
        {
            IsHitTestVisible = false,
            Children =
            {
                (_maskBorder = new Border
                {
                    Background = Brushes.Black,
                    Opacity = 0.4
                }),
                (_elementBoundsBorder = new Border
                {
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                }),
                // Per-link highlight overlay used by LinkRect mode after a
                // capture; draws an aqua border around every harvested
                // anchor for ~700ms before the overlay closes.
                (_capturedLinksCanvas = new Canvas
                {
                    IsHitTestVisible = false,
                })
            }
        };

        _screenBounds = screenBounds;
        SetPlacement(screenBounds, out _scale);
    }

    public void SetImage(Bitmap? bitmap)
    {
        // We use an ImageBrush here instead of an Image control
        // to avoid issues with scaling and rearrangement.
        Background = new ImageBrush(bitmap);
    }

    public void SetMask(PixelRect rect)
    {
        var maskRect = rect.Translate(-(PixelVector)_screenBounds.Position).ToRect(_scale);
        if (maskRect.Width < 0 || maskRect.Height < 0)
        {
            // Sometimes the rect can be invalid due to DPI scaling and rounding, so we need to handle that case.
            maskRect = default;
        }

        _maskBorder.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(Bounds), new RectangleGeometry(maskRect));
        _elementBoundsBorder.Margin = new Thickness(maskRect.X, maskRect.Y, 0, 0);
        _elementBoundsBorder.Width = maskRect.Width;
        _elementBoundsBorder.Height = maskRect.Height;
    }

    // Reuse aqua brushes across all captured-link borders to avoid a
    // per-link allocation when the harvest returns dozens of anchors.
    private static readonly SolidColorBrush CapturedBorderBrush =
        new(Color.FromArgb(0xFF, 0x00, 0xC8, 0xFF));
    private static readonly SolidColorBrush CapturedFillBrush =
        new(Color.FromArgb(0x40, 0x00, 0xC8, 0xFF));

    /// <summary>
    /// Paint an aqua border around each captured anchor so the user sees
    /// exactly which links were collected. Pass an empty / null list to
    /// clear. Caller controls how long the highlight stays on screen.
    /// </summary>
    public void SetCapturedLinkRects(IReadOnlyList<HarvestedLink> links)
    {
        _capturedLinksCanvas.Children.Clear();
        if (links is null || links.Count == 0) return;
        foreach (var l in links)
        {
            var r = l.Bounds.Translate(-(PixelVector)_screenBounds.Position).ToRect(_scale);
            if (r.Width <= 0 || r.Height <= 0) continue;
            var box = new Border
            {
                BorderThickness = new Thickness(2),
                BorderBrush = CapturedBorderBrush,
                Background = CapturedFillBrush,
                Width = r.Width,
                Height = r.Height
            };
            Canvas.SetLeft(box, r.X);
            Canvas.SetTop(box, r.Y);
            _capturedLinksCanvas.Children.Add(box);
        }
    }
}

public sealed class ScreenSelectionToolTipWindow : ScreenSelectionWindow
{
    public ScreenSelectionToolTip ToolTip { get; }

    public ScreenSelectionToolTipWindow(IEnumerable<ScreenSelectionMode> allowedModes, ScreenSelectionMode mode)
    {
        Content = ToolTip = new ScreenSelectionToolTip(allowedModes)
        {
            Mode = mode
        };
        SizeToContent = SizeToContent.WidthAndHeight;
        SystemDecorations = SystemDecorations.BorderOnly;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaToDecorationsHint = true;
    }
}