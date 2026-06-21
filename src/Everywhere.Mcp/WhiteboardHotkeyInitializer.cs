using System.IO;
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

        // Open transparent overlay IMMEDIATELY. No screenshot is shown
        // underneath — user draws directly on top of the live screen,
        // so there's no chance of pixel mismatch / 'frame shrink'.
        _logger.LogInformation("Whiteboard opening overlay on screen {Screen}", screen);
        var overlay = new WhiteboardOverlay(screen, backgroundImage: null);
        _activeOverlay = overlay;

        // OCR-only screenshot, taken in the background while user draws.
        // Doesn't affect the overlay visually.
        var captureSources = new List<(string Name, IVisualElement Element)>();
        if (focusedRoot is not null) captureSources.Add(("focused window", focusedRoot));
        if (targetScreen is not null && !ReferenceEquals(targetScreen, focusedRoot))
            captureSources.Add(("target screen", targetScreen));

        // Pre-read each source's bbox on the UI thread (AX calls are not
        // documented as thread-safe on macOS / Windows backends).
        var sourcesWithBounds = captureSources
            .Select(s => (s.Name, s.Element, Bounds: s.Element.BoundingRectangle))
            .ToList();

        var ocrCts = new CancellationTokenSource();
        var captureTcs = new TaskCompletionSource<(Avalonia.Media.Imaging.Bitmap? Bitmap, PixelRect Bounds)>();
        var captureTask = Task.Run(async () =>
        {
            try
            {
                foreach (var (name, source, bounds) in sourcesWithBounds)
                {
                    if (ocrCts.Token.IsCancellationRequested) break;
                    try
                    {
                        using var capture = await source.CaptureAsync(ocrCts.Token);
                        // Capture-result -> Bitmap. Take ownership directly,
                        // no PNG round-trip — we never share with overlay.
                        var bg = capture.ToAvaloniaBitmap();
                        if (bg is null) continue;
                        _logger.LogInformation(
                            "Whiteboard async capture from {Source}, bbox {Bbox}",
                            name, bounds);
                        captureTcs.TrySetResult((bg, bounds));
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Whiteboard: capture from {Source} failed; trying next", name);
                    }
                }
                _logger.LogWarning("Whiteboard: no capture source produced a bitmap (OCR will fall back to leaf-fraction)");
                captureTcs.TrySetResult((null, screen));
            }
            catch (OperationCanceledException)
            {
                captureTcs.TrySetResult((null, screen));
            }
        });
        // Surface unobserved exceptions in logs.
        _ = captureTask.ContinueWith(t =>
            _logger.LogError(t.Exception, "Whiteboard: capture task faulted"),
            TaskContinuationOptions.OnlyOnFaulted);

        Avalonia.Media.Imaging.Bitmap? ocrBitmap = null;
        PixelRect ocrBitmapBounds = screen;
        try
        {
            overlay.Show();
            overlay.Activate();
            _logger.LogInformation("Whiteboard overlay shown");
            var result = await overlay.ResultTask;
            if (!result.Canceled && result.Strokes.Count > 0 && result.Strokes[0].Count > 0)
            {
                var first = result.Strokes[0][0];
                _logger.LogInformation(
                    "Whiteboard coords: window.Position={WinPos} screenBounds={Sb} firstStrokeScreenPt=({X:F1},{Y:F1})",
                    result.WindowPosition, result.ScreenBounds, first.X, first.Y);
            }
            if (result.Canceled || result.Strokes.Count == 0)
            {
                _logger.LogDebug("Whiteboard cancelled or empty; nothing to stash");
                ocrCts.Cancel();
                return;
            }
            // Wait for the OCR-only capture task (typically 100-300ms).
            // Bounded so a stuck screencapture doesn't hang the user.
            var done = await Task.WhenAny(captureTcs.Task, Task.Delay(2_000));
            if (ReferenceEquals(done, captureTcs.Task))
            {
                var (bm, bb) = captureTcs.Task.Result;
                ocrBitmap = bm;
                ocrBitmapBounds = bb;
            }
            else
            {
                // Timed out — cancel the capture and dispose any bitmap that
                // arrives later so it isn't leaked.
                ocrCts.Cancel();
                _ = captureTcs.Task.ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion)
                        t.Result.Bitmap?.Dispose();
                });
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

            var (annotations, strokeGroups) = WhiteboardParser.ParseGrouped(strokes);
            var regions = new List<WhiteboardRegion>(annotations.Count);
            var snapTrace = new List<(Annotation Ann, SnapResult Snap)>();
            _logger.LogInformation(
                "Whiteboard snap context: focusedRoot bbox={FrootBbox}, ocrBitmap bbox={OcrBbox}",
                focusedRoot.BoundingRectangle, ocrBitmapBounds);
            for (var ai = 0; ai < annotations.Count; ai++)
            {
                var ann = annotations[ai];
                var annStrokes = strokeGroups[ai];
                _logger.LogInformation(
                    "Whiteboard ann: kind={Kind} parserRect={Rect}",
                    ann.Kind, ann.BoundingRect);
                // Always snap against focusedRoot. v0.8.25 tried falling
                // back to targetScreen when focusedRoot.BoundingRect was
                // 0x0, but that traverses EVERY app's leaves on screen
                // (Finder + terminal + browser + ...) and pulled in noise
                // like 'VLESS' / 'Downloads' from other windows. Even a
                // degenerate-bbox focusedRoot has the right Children in
                // its subtree.
                //
                // Pass ONLY this annotation's strokes (annStrokes) to the
                // snapper, NOT all strokes — otherwise endpoints from
                // unrelated gestures contaminate SnapArrow's "nearest
                // text" lookup and SnapUnderline's strokeTop/Bottom.
                var snap = AnnotationSnapper.Snap(ann, focusedRoot, annStrokes);
                snapTrace.Add((ann, snap));
                if (!string.IsNullOrEmpty(snap.Diagnostics))
                    _logger.LogInformation("Whiteboard snap diag ({Kind}): {Diag}",
                        ann.Kind, snap.Diagnostics);
                if (snap.Rejected || snap.Leaves.Count == 0)
                {
                    _logger.LogInformation("Whiteboard region rejected ({Kind}): {Reason}",
                        ann.Kind, string.IsNullOrEmpty(snap.RejectReason) ? "no leaves" : snap.RejectReason);
                    continue;
                }
                _logger.LogInformation(
                    "Whiteboard snap: snapRect={SnapRect} leaves=[{Leaves}]",
                    snap.Rect,
                    string.Join("; ", snap.Leaves.Select(l =>
                        $"{l.Type} bbox={l.BoundingRectangle} text=\"{TruncateForLog(l.GetText())}\"")));
                // Per-region OCR: cropping the screenshot to ONLY this region's
                // rect saves work (small image vs full screen) and prevents
                // adjacent windows / unrelated text from polluting the OCR
                // result. Cropped to the snapped rect (which already hugs
                // the captured a11y leaves).
                // OCR the user's GESTURE area, not the snapped leaf bounds.
                // We need OCR to give us per-line bboxes over what the user
                // actually drew so the slicer can keep the lines they
                // gestured over — slicing across the entire leaf would
                // produce too many candidate rows.
                var ocrLines = RunOcrForRegion(ocrBitmap, ocrBitmapBounds, ann.BoundingRect);
                _logger.LogInformation(
                    "Whiteboard OCR: bitmap={HasBitmap} region={Region} -> {LineCount} lines",
                    ocrBitmap is not null, snap.Rect, ocrLines.Count);
                // Use the parser-output rect (user's actual gesture bbox)
                // for downstream slicing, NOT snap.Rect — snapper expands
                // its rect to the full a11y leaf bounds so the agent sees a
                // visual highlight tied to the leaf, but the slicer needs
                // the user's GESTURE rect to extract just the lines they
                // drew over within a multi-line leaf.
                regions.Add(new WhiteboardRegion(
                    ann.Kind, ann.BoundingRect, snap.Leaves, snap.Confidence, ocrLines));
            }

            if (regions.Count == 0)
            {
                _logger.LogInformation("Whiteboard produced no usable regions; nothing to stash");
                TryDumpDebugBundle(strokes, ocrBitmap, ocrBitmapBounds, focusedRoot, snapTrace);
                return;
            }

            _whiteboardStash.Set(regions);
            _logger.LogInformation("Whiteboard stash filled with {Count} region(s)", regions.Count);
            TryDumpDebugBundle(strokes, ocrBitmap, ocrBitmapBounds, focusedRoot, snapTrace);

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
    /// Dump a self-contained snapshot of one whiteboard session to disk —
    /// the OCR-captured screenshot, every stroke point, every parser
    /// annotation, every snap result + diagnostics, every text leaf bbox
    /// in the focused tree. Lets us reproduce/diagnose bad gestures from
    /// a single bundle without re-running the app. Best-effort: failure
    /// here never affects the user-visible flow.
    /// </summary>
    private const int DebugBundleRetention = 20;

    private void TryDumpDebugBundle(
        IReadOnlyList<Stroke> strokes,
        Avalonia.Media.Imaging.Bitmap? ocrBitmap,
        PixelRect ocrBounds,
        IVisualElement focusedRoot,
        IReadOnlyList<(Annotation Ann, SnapResult Snap)> snapTrace)
    {
        // Always dump locally — bundles never leave the machine and
        // retention is bounded to DebugBundleRetention. The Settings
        // "user experience program" flag governs telemetry uploads,
        // not local diagnostic files, so we don't gate on it.
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Everywhere", "whiteboard-debug");
            Directory.CreateDirectory(root);
            // Keep at most N most-recent bundles to bound disk growth.
            try
            {
                var existing = Directory.GetDirectories(root)
                    .OrderByDescending(d => d, StringComparer.Ordinal)
                    .Skip(DebugBundleRetention - 1)
                    .ToList();
                foreach (var old in existing) Directory.Delete(old, recursive: true);
            }
            catch { /* best effort */ }
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fffffff",
                System.Globalization.CultureInfo.InvariantCulture);
            var dir = Path.Combine(root, stamp);
            Directory.CreateDirectory(dir);

            // 1. Screenshot.
            if (ocrBitmap is not null)
            {
                using var fs = File.Create(Path.Combine(dir, "screen.png"));
                ocrBitmap.Save(fs);
            }

            // 2. Trace JSON.
            var leaves = new List<object>();
            foreach (var e in DescendantsOf(focusedRoot))
            {
                var bb = e.BoundingRectangle;
                if (bb.Width <= 0 || bb.Height <= 0) continue;
                leaves.Add(new
                {
                    type = e.Type.ToString(),
                    x = bb.X, y = bb.Y, w = bb.Width, h = bb.Height,
                    text = e.GetText(maxLength: 200) ?? "",
                });
            }
            var trace = new
            {
                timestamp = DateTimeOffset.Now.ToString("o"),
                screen_bounds = new { x = ocrBounds.X, y = ocrBounds.Y, w = ocrBounds.Width, h = ocrBounds.Height },
                strokes = strokes.Select(s => new
                {
                    points = s.Points.Select(p => new[] { p.X, p.Y, p.TimestampMs }).ToArray(),
                }).ToArray(),
                annotations = snapTrace.Select(t => new
                {
                    kind = t.Ann.Kind.ToString(),
                    rect = new { x = t.Ann.BoundingRect.X, y = t.Ann.BoundingRect.Y,
                                 w = t.Ann.BoundingRect.Width, h = t.Ann.BoundingRect.Height },
                    rejected = t.Snap.Rejected,
                    reject_reason = t.Snap.RejectReason,
                    diagnostics = t.Snap.Diagnostics,
                    confidence = t.Snap.Confidence,
                    snap_rect = new { x = t.Snap.Rect.X, y = t.Snap.Rect.Y,
                                      w = t.Snap.Rect.Width, h = t.Snap.Rect.Height },
                    snap_leaves = t.Snap.Leaves.Select(l => new
                    {
                        type = l.Type.ToString(),
                        x = l.BoundingRectangle.X, y = l.BoundingRectangle.Y,
                        w = l.BoundingRectangle.Width, h = l.BoundingRectangle.Height,
                        text = l.GetText(maxLength: 200) ?? "",
                    }).ToArray(),
                }).ToArray(),
                leaves,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(trace,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(dir, "trace.json"), json);

            _logger.LogInformation("Whiteboard debug bundle written to {Dir}", dir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Whiteboard: failed to write debug bundle");
        }
    }

    private static IEnumerable<IVisualElement> DescendantsOf(
        IVisualElement root, int maxNodes = 5000, int maxDepth = 64)
    {
        var visited = new HashSet<IVisualElement>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(IVisualElement Node, int Depth)>();
        stack.Push((root, 0));
        var emitted = 0;
        while (stack.Count > 0 && emitted < maxNodes)
        {
            var (node, depth) = stack.Pop();
            if (!visited.Add(node)) continue;
            yield return node;
            emitted++;
            if (depth >= maxDepth) continue;
            foreach (var c in node.Children)
                stack.Push((c, depth + 1));
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
            // Skip when the region doesn't overlap the captured area at all
            // — this happens when the user gestures over a popup/child
            // window that lives outside the focused-window bounds. Without
            // this guard, the clamps below collapse the crop to a 1-pixel
            // edge slice and OCR would return spurious results.
            var screenRectDip = new Avalonia.Rect(
                screenBounds.X, screenBounds.Y, screenBounds.Width, screenBounds.Height);
            var overlap = regionRectScreenPx.Intersect(screenRectDip);
            if (overlap.Width <= 0 || overlap.Height <= 0)
            {
                _logger.LogDebug(
                    "Whiteboard: region {Rect} doesn't overlap captured area {Bounds}; skipping OCR",
                    regionRectScreenPx, screenBounds);
                return [];
            }

            // Recover the physical-px-per-DIP scale from the captured image
            // size vs the screen bounds. Both axes computed independently
            // in case of non-uniform scaling (uncommon but possible).
            var pxW = (int)fullBitmap.PixelSize.Width;
            var pxH = (int)fullBitmap.PixelSize.Height;
            var scaleX = screenBounds.Width > 0 ? pxW / (double)screenBounds.Width : 1.0;
            var scaleY = screenBounds.Height > 0 ? pxH / (double)screenBounds.Height : 1.0;

            // Use the OVERLAP rect (already clipped to capture bounds), not
            // the raw region rect — avoids needing the per-axis clamps to
            // do partial-overlap correction.
            var imgX = (int)Math.Round((overlap.X - screenBounds.X) * scaleX);
            var imgY = (int)Math.Round((overlap.Y - screenBounds.Y) * scaleY);
            var imgW = (int)Math.Round(overlap.Width * scaleX);
            var imgH = (int)Math.Round(overlap.Height * scaleY);

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
            _logger.LogWarning(ex, "Whiteboard: per-region OCR failed; falling back to leaf-fraction slice");
            return [];
        }
    }

    private static string TruncateForLog(string? s, int max = 60)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.Replace('\n', ' ').Replace('\r', ' ');
        return t.Length > max ? t.Substring(0, max) + "…" : t;
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
