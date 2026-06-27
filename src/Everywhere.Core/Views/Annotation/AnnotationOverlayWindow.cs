using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Interop;
using Serilog;

namespace Everywhere.Views.Annotation;

/// <summary>
/// Visual spike (v0.9.167): a 28×28 red square pinned to the top-right
/// of the just-pinned element. Verifies that we can correctly resolve
/// AX bounds, position an Avalonia transparent overlay there, and
/// keep it visible without stealing focus. Once the position math is
/// confirmed it'll be expanded into the real ➕ + textarea popover.
/// </summary>
public class AnnotationOverlayWindow : Window
{
    // Small badge size — same idea as the mockup's 28×28 plus button.
    private const int BadgeSize = 28;
    // Inset from the anchor element's top-right so the badge doesn't
    // occlude the anchor itself. Negative X offset means the badge
    // sits slightly outside the right edge.
    private const int OffsetX = 6;
    private const int OffsetY = -6;

    private static readonly TimeSpan AutoHideAfter = TimeSpan.FromSeconds(6);

    private DispatcherTimer? _autoHideTimer;

    public AnnotationOverlayWindow()
    {
        Width = BadgeSize;
        Height = BadgeSize;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = null;
        Topmost = true;
        Focusable = false;

        // Hit-testable so the user can click the badge. The
        // VisualElementOverlayWindow used for outlines deliberately
        // sets this false — we are NOT that, we are interactive.
        IsHitTestVisible = true;

        var windowHelper = ServiceLocator.Resolve<IWindowHelper>();
        windowHelper.SetFocusable(this, false);
        windowHelper.SetHitTestVisible(this, true);

        Content = new Border
        {
            Width = BadgeSize,
            Height = BadgeSize,
            CornerRadius = new CornerRadius(BadgeSize / 2.0),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0x38, 0x5C)), // Airbnb rausch, debug fill
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is not WindowCloseReason.ApplicationShutdown and not WindowCloseReason.OSShutdown)
        {
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// Position the overlay at the top-right of the given AX element
    /// bounding rect, expressed in screen pixels. Hides the window if
    /// the rect is empty or off-screen.
    /// </summary>
    public async void ShowFor(IVisualElement element)
    {
        PixelRect rect;
        try
        {
            rect = await Task.Run(() => element.BoundingRectangle).WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            Log.Logger.ForContext<AnnotationOverlayWindow>().Warning(ex, "Failed to resolve AX bounds for annotation overlay");
            Hide();
            return;
        }

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            Hide();
            return;
        }

        // Anchor top-right of the element rect, with a small outward
        // offset so the badge sits at the corner of the highlight.
        var screenX = rect.Right + OffsetX;
        var screenY = rect.Y + OffsetY;
        Position = new PixelPoint(screenX, screenY);

        Show();
        RestartAutoHide();
    }

    /// <summary>
    /// (spike) the badge has no interactive content yet, so a stale
    /// pin would leave it floating forever (the user reported "切了
    /// 窗口红点还在"). Auto-hide after a few seconds — once we expand
    /// to the ➕→textarea popover this gets cancelled the moment the
    /// user hovers / clicks the badge.
    /// </summary>
    private void RestartAutoHide()
    {
        _autoHideTimer?.Stop();
        _autoHideTimer ??= new DispatcherTimer { Interval = AutoHideAfter };
        _autoHideTimer.Tick -= OnAutoHideTick;
        _autoHideTimer.Tick += OnAutoHideTick;
        _autoHideTimer.Start();
    }

    private void OnAutoHideTick(object? sender, EventArgs e)
    {
        _autoHideTimer?.Stop();
        Hide();
    }
}
