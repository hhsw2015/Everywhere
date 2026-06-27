using Avalonia.Controls;
using Everywhere.Common;
using Everywhere.Interop;
using Serilog;
using ZLinq;

namespace Everywhere.Views;

public class VisualElementOverlayWindow : Window
{
    private WeakReference<IVisualElement>? _visualElement;

    public VisualElementOverlayWindow()
    {
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        IsHitTestVisible = false;
        Background = null;
        Focusable = false;
        Topmost = true;

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

    /// <summary>
    /// When true, <see cref="UpdateForVisualElement"/> always re-queries
    /// bounds and re-shows, even if the same element is passed in again.
    /// Set by callers that need continuous tracking (annotation overlay
    /// following a scrolling element). Default off preserves the existing
    /// fast-path for ChatInputArea / picker debugger.
    /// </summary>
    public bool AlwaysRefresh { get; set; }

    // Re-entrancy guard: with AlwaysRefresh on, a 250ms timer can fire
    // a second update before the previous AX query (up to 1s) returns.
    // Without the guard, stale continuations clobber fresh ones — Hide()
    // races Show(), Position is overwritten with old bounds.
    private int _updating;

    public async void UpdateForVisualElement(IVisualElement? element)
    {
        if (element is null)
        {
            _visualElement = null;
            Hide();
            return;
        }

        if (AlwaysRefresh && Interlocked.CompareExchange(ref _updating, 1, 0) != 0) return;

        try
        {
            if (!AlwaysRefresh
                && _visualElement?.TryGetTarget(out var existingElement) is true
                && Equals(existingElement, element))
            {
                return; // same element, fast path
            }

            if (_visualElement is null)
            {
                _visualElement = new WeakReference<IVisualElement>(element);
            }
            else
            {
                _visualElement.SetTarget(element);
            }

            PixelRect boundingRectangle;
            try
            {
                boundingRectangle = await Task.Run(() => element.BoundingRectangle).WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
                if (!AlwaysRefresh) _visualElement = null;
                return;
            }
            catch (Exception ex)
            {
                if (!AlwaysRefresh) _visualElement = null;
                Log.Logger.ForContext<VisualElementOverlayWindow>().Error(ex, "Failed to update OverlayWindow for visual element.");
                if (!AlwaysRefresh) Hide();
                return;
            }

            if (boundingRectangle.Width <= 0 || boundingRectangle.Height <= 0)
            {
                // AlwaysRefresh callers (annotation outline) want to keep
                // the last-known frame visible when the element scrolls
                // off-screen — losing the anchor visual is worse than a
                // stale rect. Default callers (chat input, picker
                // debugger) still hide so they don't paint over nothing.
                if (!AlwaysRefresh)
                {
                    _visualElement = null;
                    Hide();
                }
                return;
            }

            var screenBounds = Screens.All
                .AsValueEnumerable()
                .Select(s => s.Bounds)
                .Aggregate((a, b) => a.Union(b));

            var x = Math.Clamp(boundingRectangle.X, screenBounds.X, screenBounds.Right);
            var y = Math.Clamp(boundingRectangle.Y, screenBounds.Y, screenBounds.Bottom);
            var right = Math.Min(boundingRectangle.Right, screenBounds.Right);
            var bottom = Math.Min(boundingRectangle.Bottom, screenBounds.Bottom);
            var width = right - x;
            var height = bottom - y;

            if (width <= 0 || height <= 0)
            {
                if (!AlwaysRefresh)
                {
                    _visualElement = null;
                    Hide();
                }
                return;
            }

            Position = new PixelPoint(x, y);

            var scaling = DesktopScaling;
            Width = width / scaling;
            Height = height / scaling;

            Show();
        }
        catch (Exception ex)
        {
            // Window disposed mid-update during DisposeAsync, AX permission
            // revoked, etc. Don't let async-void take down the process.
            Log.Logger.ForContext<VisualElementOverlayWindow>().Debug(ex, "OverlayWindow update suppressed");
        }
        finally
        {
            if (AlwaysRefresh) Interlocked.Exchange(ref _updating, 0);
        }
    }
}