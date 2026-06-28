using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Everywhere.Common;
using Everywhere.Interop;
using Serilog;

namespace Everywhere.Views.Annotation;

/// <summary>
/// Annotation-specific outline window. Drawn ONCE at pin time from a
/// pre-resolved PixelRect, with no AX follow-up. Decoupled from
/// <see cref="VisualElementOverlayWindow"/> on purpose — that one's
/// fast-path + WeakReference + 0×0-hide behaviour kept making the
/// annotation outline disappear immediately after pin. Here the rect
/// goes in, the border draws, that's it.
/// </summary>
public sealed class AnnotationOutlineWindow : Window
{
    public AnnotationOutlineWindow()
    {
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = null;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#AC45F1")),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
        };

        var windowHelper = ServiceLocator.Resolve<IWindowHelper>();
        windowHelper.SetFocusable(this, false);
        windowHelper.SetHitTestVisible(this, false);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is not WindowCloseReason.ApplicationShutdown and not WindowCloseReason.OSShutdown)
        {
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    public void ShowAt(PixelRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        try
        {
            ApplyRect(rect);
            Show();
        }
        catch (Exception ex)
        {
            Log.Logger.ForContext<AnnotationOutlineWindow>().Warning(ex, "AnnotationOutlineWindow.ShowAt failed");
        }
    }

    public void MoveTo(PixelRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        try
        {
            ApplyRect(rect);
            if (!IsVisible) Show();
        }
        catch (Exception ex)
        {
            Log.Logger.ForContext<AnnotationOutlineWindow>().Debug(ex, "AnnotationOutlineWindow.MoveTo failed");
        }
    }

    private void ApplyRect(PixelRect rect)
    {
        Position = new PixelPoint(rect.X, rect.Y);
        var scaling = DesktopScaling > 0 ? DesktopScaling : 1.0;
        Width = rect.Width / scaling;
        Height = rect.Height / scaling;
    }
}
