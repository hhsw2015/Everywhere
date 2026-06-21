using Avalonia;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Interop.Whiteboard;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Whiteboard;
using Everywhere.Utilities;
using Everywhere.Views.Whiteboard;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp;

/// <summary>
/// Wires the user-configurable Whiteboard hotkey: pressing it opens a full-screen
/// transparent overlay where the user draws gestures (circle / underline / arrow / X)
/// over the current screen. Releasing the gesture (Enter or click "Done") runs
/// <see cref="WhiteboardParser"/> + <see cref="AnnotationSnapper"/> against the
/// focused window's a11y tree, fills <see cref="WhiteboardStash"/> with the
/// resulting regions, and triggers <see cref="ContextStashWriter"/> so the
/// agent's next prompt sees the whiteboard in its context envelope.
///
/// On Esc / overlay close-without-strokes, no stash is produced.
///
/// Lives in Everywhere.Mcp because the writer + stash + snapper are MCP-side
/// concerns. The overlay itself is in Everywhere.Core/Views/Whiteboard.
/// </summary>
public sealed class WhiteboardHotkeyInitializer : IAsyncInitializer
{
    private readonly Settings _settings;
    private readonly IShortcutListener _shortcutListener;
    private readonly IVisualElementContext _visualContext;
    private readonly WhiteboardStash _whiteboardStash;
    private readonly ContextStashWriter _contextWriter;
    private readonly Everywhere.Interop.Whiteboard.IOcrEngine _ocrEngine;
    private readonly ILogger<WhiteboardHotkeyInitializer> _logger;
    private readonly Lock _syncLock = new();

    private volatile WhiteboardOverlay? _activeOverlay;
    // Reentry guard: set 1 the moment the hotkey starts opening an overlay,
    // before the first await. Without it a second hotkey press during the
    // capture-screen await would race-create a second overlay.
    private int _opening;

    public WhiteboardHotkeyInitializer(
        Settings settings,
        IShortcutListener shortcutListener,
        IVisualElementContext visualContext,
        WhiteboardStash whiteboardStash,
        ContextStashWriter contextWriter,
        Everywhere.Interop.Whiteboard.IOcrEngine ocrEngine,
        ILogger<WhiteboardHotkeyInitializer> logger)
    {
        _settings = settings;
        _shortcutListener = shortcutListener;
        _visualContext = visualContext;
        _whiteboardStash = whiteboardStash;
        _contextWriter = contextWriter;
        _ocrEngine = ocrEngine;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        InitializeShortcut(_settings.Shortcut.Whiteboard);
        return Task.CompletedTask;
    }

    private void InitializeShortcut(CompositeKeyboardShortcut shortcut)
    {
        IDisposable? mainSubscription = null;
        IDisposable? alternativeSubscription = null;

        shortcut.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(CompositeKeyboardShortcut.IsEnabled):
                {
                    if (shortcut.IsEnabled) RegisterAll();
                    else
                    {
                        using var _0 = _syncLock.EnterScope();
                        DisposeHelper.DisposeToDefault(ref mainSubscription);
                        DisposeHelper.DisposeToDefault(ref alternativeSubscription);
                    }
                    break;
                }
                case nameof(CompositeKeyboardShortcut.Main) when shortcut.IsEnabled:
                    RegisterOne(shortcut.Main, ref mainSubscription);
                    break;
                case nameof(CompositeKeyboardShortcut.Alternative) when shortcut.IsEnabled:
                    RegisterOne(shortcut.Alternative, ref alternativeSubscription);
                    break;
            }
        };

        if (shortcut.IsEnabled) RegisterAll();

        void RegisterAll()
        {
            if (shortcut.Main.IsValid) RegisterOne(shortcut.Main, ref mainSubscription);
            if (shortcut.Alternative.IsValid) RegisterOne(shortcut.Alternative, ref alternativeSubscription);
        }
    }

    private void RegisterOne(KeyboardShortcut shortcut, ref IDisposable? slot)
    {
        using var _0 = _syncLock.EnterScope();
        DisposeHelper.DisposeToDefault(ref slot);
        if (!shortcut.IsValid)
        {
            _logger.LogInformation("Whiteboard shortcut not yet bound; waiting for user input");
            return;
        }
        try
        {
            slot = _shortcutListener.Register(shortcut, OnHotkey);
            _logger.LogInformation("Whiteboard shortcut registered: {Shortcut}", shortcut);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register Whiteboard shortcut {Shortcut}", shortcut);
        }
    }

    private void OnHotkey()
    {
        _logger.LogInformation("Whiteboard hotkey fired");
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (_activeOverlay is not null)
                {
                    _logger.LogInformation("Whiteboard re-press: committing active overlay");
                    _activeOverlay.Commit();
                    return;
                }
                if (Interlocked.CompareExchange(ref _opening, 1, 0) != 0)
                {
                    // Another OpenOverlayAsync is mid-await (e.g. during screen
                    // capture). Ignore this hotkey press — overlay will appear
                    // shortly and the user can re-press to commit.
                    _logger.LogInformation("Whiteboard hotkey ignored: overlay opening");
                    return;
                }
                try
                {
                    await OpenOverlayAsync();
                }
                finally
                {
                    Interlocked.Exchange(ref _opening, 0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Whiteboard hotkey handler failed");
            }
        });
    }

    private async Task OpenOverlayAsync()
    {
        // Capture the focused element BEFORE the overlay steals focus, so
        // we have a stable a11y tree to snap against on commit.
        // Pre-capture the focused window's a11y root so we have a stable
        // tree to snap against on commit. The OVERLAY itself covers the
        // entire screen (not just the focused window) so the user can
        // gesture across the full desktop.
        IVisualElement? focusedRoot = null;
        IVisualElement? targetScreen = null;
        PixelRect screen;
        try
        {
            var focused = _visualContext.FocusedElement;
            focusedRoot = focused?.Root() ?? focused;
            if (focusedRoot is not null)
            {
                var winRect = focusedRoot.BoundingRectangle;
                var winCenter = new PixelPoint(
                    winRect.X + winRect.Width / 2,
                    winRect.Y + winRect.Height / 2);
                foreach (var sc in _visualContext.Screens)
                {
                    var b = sc.BoundingRectangle;
                    if (winCenter.X >= b.X && winCenter.X < b.X + b.Width
                        && winCenter.Y >= b.Y && winCenter.Y < b.Y + b.Height)
                    { targetScreen = sc; break; }
                }
            }
            targetScreen ??= _visualContext.Screens.FirstOrDefault();
            screen = targetScreen?.BoundingRectangle
                     ?? new PixelRect(0, 0, 1920, 1080);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Whiteboard could not resolve target screen; using fallback");
            screen = new PixelRect(0, 0, 1920, 1080);
        }

        // Capture the screen image to use as the overlay background. This
        // sidesteps Avalonia macOS Transparent-window unreliability — we
        // paint the captured screenshot UNDER the user's strokes, so the
        // overlay visually IS the screen even though the window is opaque.
        Avalonia.Media.Imaging.Bitmap? backgroundImage = null;
        // Decoded ONCE for OCR; survives past the overlay's ownership-take
        // because we keep a separate reference here. Disposed in finally
        // after all per-region OCR calls.
        Avalonia.Media.Imaging.Bitmap? ocrBitmap = null;
        if (targetScreen is not null)
        {
            try
            {
                using var capture = await targetScreen.CaptureAsync(CancellationToken.None);
                backgroundImage = capture.ToAvaloniaBitmap();
                if (backgroundImage is not null)
                {
                    // Re-encode once + decode once into a separate bitmap so
                    // we own a copy independent of the overlay.
                    using var ms = new System.IO.MemoryStream();
                    backgroundImage.Save(ms);
                    ms.Position = 0;
                    ocrBitmap = new Avalonia.Media.Imaging.Bitmap(ms);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Whiteboard: screen capture for background failed; using dim overlay");
            }
        }

        _logger.LogInformation("Whiteboard opening overlay on screen {Screen}, background={HasBg}",
            screen, backgroundImage is not null);
        var overlay = new WhiteboardOverlay(screen, backgroundImage);
        _activeOverlay = overlay;
        try
        {
            overlay.Show();
            overlay.Activate();
            _logger.LogInformation("Whiteboard overlay shown");
            var result = await overlay.ResultTask;
            if (result.Canceled || result.Strokes.Count == 0)
            {
                _logger.LogDebug("Whiteboard cancelled or empty; nothing to stash");
                return;
            }

            var strokes = result.Strokes
                .Select(pts => new Stroke(pts
                    .Select(p => new StrokePoint(p.X, p.Y, p.T)).ToList()))
                .ToList();

            // Re-pull the focused root if we don't have one (overlay closed
            // before pre-capture succeeded — rare).
            focusedRoot ??= _visualContext.FocusedElement?.Root()
                            ?? _visualContext.FocusedElement;
            if (focusedRoot is null)
            {
                _logger.LogWarning("Whiteboard committed but no focused root for snap");
                return;
            }

            var annotations = WhiteboardParser.Parse(strokes);
            var regions = new List<WhiteboardRegion>(annotations.Count);
            foreach (var ann in annotations)
            {
                var snap = AnnotationSnapper.Snap(ann, focusedRoot, strokes);
                if (snap.Rejected || snap.Leaves.Count == 0)
                {
                    _logger.LogInformation("Whiteboard region rejected ({Kind}): {Reason}",
                        ann.Kind, string.IsNullOrEmpty(snap.RejectReason) ? "no leaves" : snap.RejectReason);
                    continue;
                }
                // Per-region OCR: cropping the screenshot to ONLY this region's
                // rect saves work (small image vs full screen) and prevents
                // adjacent windows / unrelated text from polluting the OCR
                // result. Cropped to the snapped rect (which already hugs
                // the captured a11y leaves).
                var ocrLines = RunOcrForRegion(ocrBitmap, screen, snap.Rect);
                regions.Add(new WhiteboardRegion(
                    ann.Kind, snap.Rect, snap.Leaves, snap.Confidence, ocrLines));
            }

            if (regions.Count == 0)
            {
                _logger.LogInformation("Whiteboard produced no usable regions; nothing to stash");
                return;
            }

            _whiteboardStash.Set(regions);
            _logger.LogInformation("Whiteboard stash filled with {Count} region(s)", regions.Count);

            try
            {
                await _contextWriter.CaptureAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Whiteboard: ContextStashWriter.CaptureAsync failed");
            }
        }
        finally
        {
            _activeOverlay = null;
            ocrBitmap?.Dispose();
        }
    }

    /// <summary>
    /// Crop a pre-decoded screenshot to the region rect, run OCR on the
    /// crop, and return lines in screen-pixel coords. The bitmap is
    /// decoded ONCE upstream and reused across all regions to avoid
    /// re-decoding a multi-MB PNG N times per whiteboard session.
    ///
    /// Coordinate conversion: <paramref name="screenBounds"/> /
    /// <paramref name="regionRectScreenPx"/> are in DIP (Avalonia screen
    /// space). <c>fullBitmap.PixelSize</c> is the captured image's
    /// PHYSICAL pixel size. On Retina/HiDPI these differ by a scale
    /// factor; we recover it from the ratio and apply it before
    /// cropping. OCR origin is returned in DIP space again so the
    /// HybridSlicer can intersect directly with region rects.
    /// </summary>
    private IReadOnlyList<OcrLine> RunOcrForRegion(
        Avalonia.Media.Imaging.Bitmap? fullBitmap,
        PixelRect screenBounds,
        Avalonia.Rect regionRectScreenPx)
    {
        if (fullBitmap is null) return [];
        try
        {
            // Recover the physical-px-per-DIP scale from the captured image
            // size vs the screen bounds. Both axes computed independently
            // in case of non-uniform scaling (uncommon but possible).
            var pxW = (int)fullBitmap.PixelSize.Width;
            var pxH = (int)fullBitmap.PixelSize.Height;
            var scaleX = screenBounds.Width > 0 ? pxW / (double)screenBounds.Width : 1.0;
            var scaleY = screenBounds.Height > 0 ? pxH / (double)screenBounds.Height : 1.0;

            // DIP -> physical px, translated by -screenBounds.
            var imgX = (int)Math.Round((regionRectScreenPx.X - screenBounds.X) * scaleX);
            var imgY = (int)Math.Round((regionRectScreenPx.Y - screenBounds.Y) * scaleY);
            var imgW = (int)Math.Round(regionRectScreenPx.Width * scaleX);
            var imgH = (int)Math.Round(regionRectScreenPx.Height * scaleY);

            imgX = Math.Max(0, Math.Min(pxW - 1, imgX));
            imgY = Math.Max(0, Math.Min(pxH - 1, imgY));
            imgW = Math.Max(1, Math.Min(pxW - imgX, imgW));
            imgH = Math.Max(1, Math.Min(pxH - imgY, imgH));

            var cropRect = new PixelRect(imgX, imgY, imgW, imgH);
            using var cropTarget = new Avalonia.Media.Imaging.RenderTargetBitmap(
                cropRect.Size, fullBitmap.Dpi);
            using (var ctx = cropTarget.CreateDrawingContext())
            {
                ctx.DrawImage(
                    fullBitmap,
                    new Avalonia.Rect(cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height),
                    new Avalonia.Rect(0, 0, cropRect.Width, cropRect.Height));
            }

            using var outMs = new System.IO.MemoryStream();
            cropTarget.Save(outMs);
            outMs.Position = 0;

            // OCR origin: convert physical-px crop offset back to DIP so
            // the slicer compares OCR bboxes to a11y rects in the same
            // coord space.
            var origin = new PixelPoint(
                (int)Math.Round(screenBounds.X + cropRect.X / scaleX),
                (int)Math.Round(screenBounds.Y + cropRect.Y / scaleY));
            return _ocrEngine.Recognize(outMs, origin);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Whiteboard: per-region OCR failed; falling back to leaf-fraction slice");
            return [];
        }
    }
}

internal static class WhiteboardElementExtensions
{
    public static IVisualElement Root(this IVisualElement element)
    {
        var current = element;
        while (current.Parent is { } p) current = p;
        return current;
    }
}
