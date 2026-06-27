using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Everywhere.Common;
using Everywhere.Interop;
using Serilog;

namespace Everywhere.Views.Annotation;

/// <summary>
/// Annotation overlay (B1, v0.9.170):
/// - Collapsed state: a ➕ badge at the top-right of the pinned element.
/// - Expanded state: a 320×100 popover with the same ➕ in the corner
///   and a textarea filling the rest. Clicking ➕ toggles between the
///   two; blur or Esc collapses back; the saved body is exposed via
///   <see cref="CommittedText"/> for the host to forward to AnnotationStash.
///
/// The expanded state widens the window and lengthens AutoHideAfter, so
/// once the user clicks ➕ the badge stops fighting them by disappearing
/// mid-thought. Save semantics (commit-to-stash, close) ship in B2.
/// </summary>
public class AnnotationOverlayWindow : Window
{
    private const int BadgeSize = 32;
    private const int ExpandedWidth = 320;
    private const int ExpandedHeight = 110;

    // Offset from the anchor element's top-right corner so the badge
    // sits just outside the highlight, not on top of it.
    private const int OffsetX = 6;
    private const int OffsetY = -6;

    // ponytail: no auto-hide. Pin-induced ➕ stays until user dismisses
    // (Esc), commits (Cmd+Enter), or fires SnapshotContext. Disappearing
    // mid-thought was the user's #1 complaint — "我写了文字就不能消失".

    private readonly Border _root;
    private readonly Button _plusButton;
    private readonly TextBox _textBox;

    private bool _expanded;
    private PixelPoint _collapsedOrigin;

    /// <summary>The body the user committed when the popover collapses.
    /// Cleared on each ShowFor so a fresh pin doesn't inherit stale text.</summary>
    public string? CommittedText { get; private set; }

    public event EventHandler<string>? Committed;

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
        // Focusable=true so the textarea can receive keyboard / IME
        // input once expanded. The collapsed badge still doesn't steal
        // focus because ShowActivated=false and the window helper marks
        // it non-focusable at the AppKit level until we open the popover.
        Focusable = true;

        var windowHelper = ServiceLocator.Resolve<IWindowHelper>();
        windowHelper.SetFocusable(this, false);
        windowHelper.SetHitTestVisible(this, true);

        // ➕ button — gradient fill matching the mockup, gradient
        // resource lives in App.axaml as AssistantBackgroundBrush.
        _plusButton = new Button
        {
            Width = BadgeSize,
            Height = BadgeSize,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#AC45F1"), 0),
                    new GradientStop(Color.Parse("#7A7EF4"), 0.5),
                    new GradientStop(Color.Parse("#3DC6F8"), 1),
                },
            },
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(BadgeSize / 2.0),
            FontSize = 18,
            FontWeight = FontWeight.Medium,
            Padding = new Thickness(0),
            Content = "+",
        };
        _plusButton.Click += OnPlusClicked;

        _textBox = new TextBox
        {
            Watermark = "写点注释… (Esc 收起)",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(10, 10, BadgeSize + 14, 10),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsVisible = false,
        };
        // Tunnel-route KeyDown at the WINDOW level so Esc / Cmd+Enter fire
        // BEFORE the TextBox bubble-stage handlers consume them (AcceptsReturn
        // makes the TextBox eat plain Enter; on some Avalonia/macOS combos it
        // also eats Cmd+Enter before our bubble-stage handler runs).
        AddHandler(KeyDownEvent, OnTextBoxKeyDown, RoutingStrategies.Tunnel);
        _textBox.LostFocus += OnTextBoxLostFocus;

        _root = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1B, 0x1B, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0, OffsetY = 4, Blur = 14, Spread = 0,
                Color = Color.FromArgb(0x55, 0, 0, 0),
            }),
            Child = new Panel
            {
                Children = { _textBox, _plusButton },
            },
        };

        Content = _root;
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
    /// bounding rect, expressed in screen pixels. Resets to collapsed
    /// state on each call.
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

        Collapse();
        CommittedText = null;
        _textBox.Text = string.Empty;
        ResetBadgeVisual();

        var screenX = rect.Right + OffsetX;
        var screenY = rect.Y + OffsetY;
        _collapsedOrigin = new PixelPoint(screenX, screenY);
        Position = _collapsedOrigin;

        Show();
    }

    private void OnPlusClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Committed (✓) is a terminal state — click is a no-op. The body
        // is already visible via tooltip on hover. Re-edit is intentionally
        // unavailable: the only way to revise a note is to clear the batch
        // (SnapshotContext) and re-pin. Keeps the state machine simple and
        // avoids stash-mutation paths that don't exist yet.
        if (CommittedText is not null) return;

        if (_expanded) Collapse();
        else Expand();
    }

    private void Expand()
    {
        if (_expanded) return;
        _expanded = true;

        // Resize and re-anchor so the badge stays where it was; the
        // popover grows down-and-left from that corner.
        Width = ExpandedWidth;
        Height = ExpandedHeight;
        Position = new PixelPoint(
            _collapsedOrigin.X - (ExpandedWidth - BadgeSize),
            _collapsedOrigin.Y);

        _textBox.IsVisible = true;

        // Take keyboard focus so the textarea can receive typing & IME
        // input. Activate the app (briefly) so macOS routes IME / 语音
        // input to us — same trick the chat window already uses.
        var windowHelper = ServiceLocator.Resolve<IWindowHelper>();
        windowHelper.SetFocusable(this, true);
        Activate();
        _textBox.Focus();
    }

    private void Collapse()
    {
        if (!_expanded)
        {
            // Even when starting collapsed we reset visibility so a
            // freshly-shown overlay never inherits the expanded layout.
            _textBox.IsVisible = false;
            Width = BadgeSize;
            Height = BadgeSize;
            return;
        }
        _expanded = false;

        var text = (_textBox.Text ?? string.Empty).Trim();
        if (text.Length > 0)
        {
            CommittedText = text;
            Committed?.Invoke(this, text);
            // Annotated state: badge turns into a green ✓ so the user
            // can see WHERE they annotated (the whole point per the
            // user's "得知道我是在哪里标注的" feedback).
            MarkAnnotated();
        }

        _textBox.IsVisible = false;
        Width = BadgeSize;
        Height = BadgeSize;
        Position = _collapsedOrigin;

        var windowHelper = ServiceLocator.Resolve<IWindowHelper>();
        windowHelper.SetFocusable(this, false);
    }

    private void MarkAnnotated()
    {
        _plusButton.Content = "✓";
        _plusButton.Background = new SolidColorBrush(Color.Parse("#3DC68C"));
        ToolTip.SetTip(_plusButton, CommittedText);
    }

    private void ResetBadgeVisual()
    {
        _plusButton.Content = "+";
        _plusButton.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#AC45F1"), 0),
                new GradientStop(Color.Parse("#7A7EF4"), 0.5),
                new GradientStop(Color.Parse("#3DC6F8"), 1),
            },
        };
        ToolTip.SetTip(_plusButton, null);
    }

    private void OnTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Collapse();
        }
        // Cmd+Enter also collapses (commits) — same gesture as ChatGPT /
        // Slack / iMessage send-current-message.
        else if (e.Key == Key.Enter
                 && (e.KeyModifiers & KeyModifiers.Meta) != 0)
        {
            e.Handled = true;
            Collapse();
        }
    }

    private void OnTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        // Blur = commit-or-collapse. User feedback: 鼠标点击文本框之外, 应该
        //自动收起. If there's text → commit (turns into ✓). If empty → just
        // collapse back to ➕ (no stash insert).
        if (_expanded) Collapse();
    }

}
