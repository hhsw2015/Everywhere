// Beyond-OCCU enhancement: a translucent border + corner badge framing
// the AX target window the agent is currently operating, so the user
// can see at a glance which app is being driven. OCCU has no such
// indicator — its target-window concept is only used internally for
// cursor z-order. We add a real visible affordance.
//
// Architecture parallels SoftwareCursorOverlay: one Avalonia transparent
// borderless click-through Window per indicator, custom Skia draw,
// auto-fade after a configurable display window.

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Everywhere.Interop;
using SkiaSharp;

namespace Everywhere.Mcp.CursorOverlay;

public sealed class TargetWindowIndicator : IAsyncDisposable
{
    private readonly IWindowHelper? _windowHelper;
    public TargetWindowIndicator(IWindowHelper? windowHelper = null)
    {
        _windowHelper = windowHelper;
    }

    private const double DefaultDisplaySeconds = 1.8;
    private const double FadeInSeconds = 0.18;
    private const double FadeOutSeconds = 0.55;
    // Palette (Everywhere brand: warm gradient #0081F7 → #FF75CA → #FFAE87).
    // Use the gradient on the border, glassy dark badge, white text.
    private static readonly SKColor StopBlue   = new(red: 0,   green: 129, blue: 247, alpha: 255);
    private static readonly SKColor StopPink   = new(red: 255, green: 117, blue: 202, alpha: 255);
    private static readonly SKColor StopPeach  = new(red: 255, green: 174, blue: 135, alpha: 255);
    private static readonly SKColor BadgeFill  = new(red: 18,  green: 18,  blue: 24,  alpha: 220);
    private static readonly SKColor BadgeStroke = new(red: 255, green: 255, blue: 255, alpha: 38);
    private static readonly SKColor BadgeText  = new(red: 255, green: 255, blue: 255, alpha: 248);
    private static readonly SKColor BadgeAccent = new(red: 0, green: 220, blue: 130, alpha: 255);

    private IndicatorWindow? _window;
    private DispatcherTimer? _fadeTimer;
    private DateTime _shownAt;
    private double _displaySeconds = DefaultDisplaySeconds;
    private bool _disposed;

    public bool IsEnabled { get; set; } = true;

    public Task ShowForAsync(PixelRect rect, string label = "🤖 Everywhere operating")
    {
        Dispatcher.UIThread.Post(() => ShowFor(rect, label));
        return Task.CompletedTask;
    }

    public void ShowFor(PixelRect rect, string label = "🤖 Everywhere operating")
    {
        if (!IsEnabled || _disposed) { Hide(); return; }
        // Tiny / off-screen / invalid frames: hide any prior indicator
        // instead of leaving stale chrome around the wrong window. OCR
        // pointed out the agent could be operating window B while the
        // border still wraps window A.
        if (rect.Width < 24 || rect.Height < 24) { Hide(); return; }
        EnsureWindow();
        _shownAt = DateTime.UtcNow;
        // Pad outward so the border doesn't clip the target window's own chrome.
        const int pad = 6;
        var padded = new PixelRect(rect.X - pad, rect.Y - pad, rect.Width + 2 * pad, rect.Height + 2 * pad);
        _window!.Position = new PixelPoint(padded.X, padded.Y);
        _window.Width = padded.Width;
        _window.Height = padded.Height;
        _window.UpdateContent(padded.Width, padded.Height, label, opacity: 1.0);
        _window.Show();
        StartFadeTimer();
    }

    public void Hide()
    {
        StopFadeTimer();
        if (Dispatcher.UIThread.CheckAccess()) _window?.Hide();
        else Dispatcher.UIThread.Post(() => _window?.Hide());
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;
        _window = new IndicatorWindow();
        // On Mac, IsHitTestVisible=false at the Avalonia layer doesn't
        // stop NSWindow from receiving / consuming pointer events. The
        // platform helper sets nativeWindow.IgnoresMouseEvents = true,
        // which is what actually makes the indicator click-through so
        // the agent's own click can pass through the border to the
        // target app underneath.
        if (_windowHelper is not null)
        {
            _window.Opened += (_, _) =>
            {
                try { _windowHelper.SetHitTestVisible(_window, false); }
                catch { /* best-effort */ }
            };
        }
    }

    private void StartFadeTimer()
    {
        StopFadeTimer();
        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 60) };
        _fadeTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - _shownAt).TotalSeconds;
            // Fade-in (cubic ease-out)
            if (elapsed < FadeInSeconds)
            {
                var t = elapsed / FadeInSeconds;
                var eased = 1 - Math.Pow(1 - t, 3);
                _window?.UpdateOpacity(eased);
                return;
            }
            // Hold
            if (elapsed < FadeInSeconds + _displaySeconds)
            {
                _window?.UpdateOpacity(1.0);
                return;
            }
            // Fade-out (cubic ease-in-out)
            var fade = (elapsed - FadeInSeconds - _displaySeconds) / FadeOutSeconds;
            if (fade >= 1)
            {
                Hide();
                return;
            }
            var fadeOpacity = fade < 0.5
                ? 1 - 4 * Math.Pow(fade, 3)
                : 1 - (1 - Math.Pow(-2 * fade + 2, 3) / 2);
            _window?.UpdateOpacity(Math.Max(0, Math.Min(1, fadeOpacity)));
        };
        _fadeTimer.Start();
    }

    private void StopFadeTimer()
    {
        _fadeTimer?.Stop();
        _fadeTimer = null;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        StopFadeTimer();
        if (Dispatcher.UIThread.CheckAccess()) _window?.Close();
        else Dispatcher.UIThread.Post(() => _window?.Close());
        _window = null;
        return ValueTask.CompletedTask;
    }

    // ----------------------------------------------------------------
    private sealed class IndicatorWindow : Window
    {
        private readonly IndicatorRenderControl _control;

        public IndicatorWindow()
        {
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            ShowInTaskbar = false;
            Topmost = true;
            CanResize = false;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            IsHitTestVisible = false;
            _control = new IndicatorRenderControl { IsHitTestVisible = false };
            Content = _control;
        }

        public void UpdateContent(int width, int height, string label, double opacity)
        {
            _control.Width = width;
            _control.Height = height;
            _control.Label = label;
            _control.Opacity = opacity;
            _control.InvalidateVisual();
        }

        public void UpdateOpacity(double opacity)
        {
            _control.Opacity = opacity;
            _control.InvalidateVisual();
        }
    }

    private sealed class IndicatorRenderControl : Control
    {
        public string Label { get; set; } = "";
        public new double Opacity { get; set; } = 1.0;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.Custom(new IndicatorDrawOperation(
                new Rect(0, 0, Bounds.Width, Bounds.Height), Label, Opacity));
        }
    }

    // Cached once, used per-Render to avoid re-allocating an SKTypeface
    // each frame (~60fps for ~2.5s per show = ~150 reuse).
    private static readonly SKTypeface _badgeTypeface =
        SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyle.Bold) ?? SKTypeface.Default;

    private sealed class IndicatorDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly string _label;
        private readonly double _opacity;

        public IndicatorDrawOperation(Rect bounds, string label, double opacity)
        {
            _bounds = bounds; _label = label; _opacity = Math.Clamp(opacity, 0, 1);
        }

        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null) return;
            using var s = lease.Lease();
            var canvas = s.SkCanvas;
            const float corner = 14f;
            const float pad = 4f;
            var w = (float)_bounds.Width;
            var h = (float)_bounds.Height;
            var inner = new SKRect(pad, pad, w - pad, h - pad);

            // 1) Wide soft outer halo, blue-pink gradient. Two passes
            //    of decreasing radius for a "spotlight" feel.
            using (var halo = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 22,
                IsAntialias = true,
                Color = ApplyAlpha(StopBlue, 0.10),
                ImageFilter = SKImageFilter.CreateBlur(18, 18),
            })
            {
                canvas.DrawRoundRect(inner, corner, corner, halo);
            }
            using (var halo2 = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 12,
                IsAntialias = true,
                Color = ApplyAlpha(StopPink, 0.18),
                ImageFilter = SKImageFilter.CreateBlur(8, 8),
            })
            {
                canvas.DrawRoundRect(inner, corner, corner, halo2);
            }

            // 2) Crisp gradient border — diagonal blue→pink→peach sweep.
            //    Use SKShader.CreateLinearGradient for the stroke shader.
            var gradStart = new SKPoint(0, 0);
            var gradEnd = new SKPoint(w, h);
            var grad = SKShader.CreateLinearGradient(
                gradStart, gradEnd,
                new[]
                {
                    ApplyAlpha(StopBlue, _opacity),
                    ApplyAlpha(StopPink, _opacity),
                    ApplyAlpha(StopPeach, _opacity),
                },
                new[] { 0f, 0.55f, 1f },
                SKShaderTileMode.Clamp);
            using (var border = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.4f,
                IsAntialias = true,
                Shader = grad,
            })
            {
                canvas.DrawRoundRect(inner, corner, corner, border);
            }

            // 3) Inner highlight ring — thin white sheen for the "glass" feel.
            using (var sheen = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.8f,
                IsAntialias = true,
                Color = new SKColor(255, 255, 255, (byte)(40 * _opacity)),
            })
            {
                var sheenRect = new SKRect(inner.Left + 1.2f, inner.Top + 1.2f,
                                           inner.Right - 1.2f, inner.Bottom - 1.2f);
                canvas.DrawRoundRect(sheenRect, corner - 1, corner - 1, sheen);
            }

            // 4) Top-left "operating" badge — pill, glass, with green
            //    pulse dot + white text. Emoji-free for crispness.
            using var font = new SKFont(_badgeTypeface, 12);
            using var textPaint = new SKPaint
            {
                IsAntialias = true,
                Color = ApplyAlpha(BadgeText, _opacity),
            };
            var trimmed = _label.StartsWith("🤖", StringComparison.Ordinal)
                ? _label.Substring(2).TrimStart()
                : _label;
            var textBounds = new SKRect();
            font.MeasureText(trimmed, out textBounds, textPaint);
            const float padX = 14f, padY = 8f;
            const float dotSize = 7f;
            const float gap = 8f;
            var badgeW = padX + dotSize + gap + textBounds.Width + padX;
            var badgeH = padY * 2 + Math.Max(textBounds.Height, dotSize);
            var badgeRect = new SKRect(14, 14, 14 + badgeW, 14 + badgeH);

            // Drop shadow under badge.
            using (var shadow = new SKPaint
            {
                Style = SKPaintStyle.Fill, IsAntialias = true,
                Color = new SKColor(0, 0, 0, (byte)(80 * _opacity)),
                ImageFilter = SKImageFilter.CreateDropShadow(0, 2, 6, 6,
                    new SKColor(0, 0, 0, (byte)(120 * _opacity))),
            })
            {
                canvas.DrawRoundRect(badgeRect, badgeH / 2, badgeH / 2, shadow);
            }

            // Glass fill.
            using (var fill = new SKPaint
            {
                Style = SKPaintStyle.Fill, IsAntialias = true,
                Color = ApplyAlpha(BadgeFill, _opacity),
            })
            {
                canvas.DrawRoundRect(badgeRect, badgeH / 2, badgeH / 2, fill);
            }
            // 1px hairline for the glass rim.
            using (var rim = new SKPaint
            {
                Style = SKPaintStyle.Stroke, IsAntialias = true,
                StrokeWidth = 1,
                Color = ApplyAlpha(BadgeStroke, _opacity),
            })
            {
                canvas.DrawRoundRect(badgeRect, badgeH / 2, badgeH / 2, rim);
            }

            // Pulse dot (green).
            var dotCenter = new SKPoint(badgeRect.Left + padX + dotSize / 2,
                                        badgeRect.MidY);
            using (var dotGlow = new SKPaint
            {
                Style = SKPaintStyle.Fill, IsAntialias = true,
                Color = new SKColor(BadgeAccent.Red, BadgeAccent.Green, BadgeAccent.Blue,
                    (byte)(140 * _opacity)),
                ImageFilter = SKImageFilter.CreateBlur(3, 3),
            })
            {
                canvas.DrawCircle(dotCenter, dotSize, dotGlow);
            }
            using (var dot = new SKPaint
            {
                Style = SKPaintStyle.Fill, IsAntialias = true,
                Color = ApplyAlpha(BadgeAccent, _opacity),
            })
            {
                canvas.DrawCircle(dotCenter, dotSize / 2, dot);
            }

            // Text.
            canvas.DrawText(trimmed,
                badgeRect.Left + padX + dotSize + gap,
                badgeRect.MidY + textBounds.Height / 2 - 1,
                font, textPaint);
        }

        private static SKColor ApplyAlpha(SKColor c, double opacity)
            => new(c.Red, c.Green, c.Blue, (byte)Math.Clamp(c.Alpha * opacity, 0, 255));
    }
}
