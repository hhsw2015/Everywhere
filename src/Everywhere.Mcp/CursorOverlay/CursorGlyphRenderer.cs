// Line-for-line port of:
//   packages/OpenComputerUseKit/Sources/OpenComputerUseKit/SoftwareCursorGlyphRenderer.swift
// from iFurySt/open-codex-computer-use. NSColor / CGContext / NSBezierPath
// translate to SkiaSharp's SKColor / SKCanvas / SKPath. Constants verbatim.
// Coordinate convention: upstream coordinate space is bottom-up (AppKit);
// we render in top-down (Avalonia / Skia view-space), so the
// AppKitDrawingState Y-flip step is replaced by directly using the
// runtime state.

using System;
using Avalonia;
using SkiaSharp;

namespace Everywhere.Mcp.CursorOverlay;

internal readonly record struct CursorGlyphRenderState(
    double Rotation,
    Vector CursorBodyOffset,
    Vector FogOffset,
    double FogOpacity,
    double FogScale,
    double ClickProgress)
{
    /// <summary>
    /// Top-left-origin equivalent of upstream `appKitDrawingState`.
    /// AppKit flips Y but Skia/Avalonia don't — the Y-flip is already
    /// implicit in our render axis, so we return the state unchanged.
    /// </summary>
    public CursorGlyphRenderState ViewSpaceDrawingState => this;
}

internal static class CursorGlyphMetrics
{
    public const double WindowWidth = 126;
    public const double WindowHeight = 126;
    public static readonly Point TipAnchor = new(60.35, 70.3);
    public const double PointerWidth = 21;
    public const double PointerHeight = 21;
    public static readonly Point PointerOffset = new(2.6, -3.2);
    public static readonly double TargetNeutralHeading = -(3 * Math.PI / 4);
    public static readonly double ProceduralContourNeutralHeading = -(96.5 * Math.PI / 180);
    public static readonly double PointerArtworkRotation = -(TargetNeutralHeading - ProceduralContourNeutralHeading);
}

internal static class CursorGlyphRenderer
{
    private static readonly SKColor PointerFill = new(
        red: (byte)(0.38 * 255), green: (byte)(0.36 * 255), blue: (byte)(0.35 * 255), alpha: (byte)(0.98 * 255));
    private static readonly SKColor PointerStroke = new(
        red: (byte)(0.90 * 255), green: (byte)(0.90 * 255), blue: (byte)(0.90 * 255), alpha: (byte)(0.92 * 255));

    // 1:1 OCCU bundled PNG primary path. The procedural contour stays
    // as fallback if the asset can't be loaded. Sourced from
    // open-computer-use@0.1.53/Resources, MIT.
    private static SKBitmap? _referenceBitmap;
    private static bool _referenceBitmapTried;
    private const string ReferenceBitmapResourceName =
        "Everywhere.Mcp.CursorOverlay.Assets.official-software-cursor-window-252.png";

    private static SKBitmap? LoadReferenceBitmap()
    {
        if (_referenceBitmapTried) return _referenceBitmap;
        _referenceBitmapTried = true;
        try
        {
            var asm = typeof(CursorGlyphRenderer).Assembly;
            using var stream = asm.GetManifestResourceStream(ReferenceBitmapResourceName);
            if (stream is null) return null;
            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            _referenceBitmap = SKBitmap.Decode(ms);
        }
        catch
        {
            _referenceBitmap = null;
        }
        return _referenceBitmap;
    }

    /// <summary>
    /// Draws the official-style software cursor glyph at the given
    /// bounds. 1:1 OCCU SoftwareCursorGlyphRenderer primary path: blit
    /// the bundled 252×252 RGBA PNG with the same rotate / scale
    /// transform as the procedural body. Falls back to procedural
    /// contour drawing if the asset is unavailable (binary-trimmed
    /// build, asset load failure).
    /// </summary>
    public static void Draw(SKCanvas canvas, Rect bounds, CursorGlyphRenderState state)
    {
        var drawingState = state.ViewSpaceDrawingState;
        var pulse = drawingState.ClickProgress;
        var midX = bounds.X + bounds.Width / 2;
        var midY = bounds.Y + bounds.Height / 2;
        var fogCenter = new Point(midX + drawingState.FogOffset.X, midY + drawingState.FogOffset.Y);
        var pointerCenter = new Point(
            midX + CursorGlyphMetrics.PointerOffset.X + drawingState.CursorBodyOffset.X,
            midY + CursorGlyphMetrics.PointerOffset.Y + drawingState.CursorBodyOffset.Y + (pulse * 0.35));

        DrawFog(canvas, fogCenter, pulse, drawingState.FogOpacity, drawingState.FogScale);

        var refBitmap = LoadReferenceBitmap();
        if (refBitmap is not null)
        {
            DrawReferenceBitmap(canvas, refBitmap, bounds, pointerCenter, drawingState.Rotation, pulse,
                drawingState.CursorBodyOffset, new Point(midX, midY));
            return;
        }

        DrawPointer(canvas, pointerCenter, drawingState.Rotation, pulse,
            drawingState.CursorBodyOffset, new Point(midX, midY));
    }

    /// <summary>
    /// Mirror OCCU's NSImage.draw fast-path: stretch the 252×252 art
    /// over the full window canvas and apply the same body / artwork
    /// transforms as the procedural fallback so motion + click pulse
    /// behavior is identical.
    /// </summary>
    private static void DrawReferenceBitmap(SKCanvas canvas, SKBitmap bitmap, Rect bounds, Point pointerCenter,
        double rotation, double clickProgress, Vector cursorBodyOffset, Point boundsMidpoint)
    {
        canvas.Save();
        canvas.Translate((float)(boundsMidpoint.X + cursorBodyOffset.X), (float)(boundsMidpoint.Y + cursorBodyOffset.Y));
        canvas.RotateRadians((float)rotation);
        canvas.Scale((float)(1 - clickProgress * 0.04), (float)(1 + clickProgress * 0.02));
        canvas.Translate(-(float)(boundsMidpoint.X + cursorBodyOffset.X), -(float)(boundsMidpoint.Y + cursorBodyOffset.Y));
        canvas.Translate((float)pointerCenter.X, (float)pointerCenter.Y);
        canvas.RotateRadians((float)CursorGlyphMetrics.PointerArtworkRotation);
        canvas.Translate(-(float)pointerCenter.X, -(float)pointerCenter.Y);

        var dst = SKRect.Create(
            (float)bounds.X, (float)bounds.Y,
            (float)bounds.Width, (float)bounds.Height);
        using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
        canvas.DrawBitmap(bitmap, dst, paint);
        canvas.Restore();
    }

    private static void DrawFog(SKCanvas canvas, Point center, double pulse, double fogOpacity, double fogScale)
    {
        var radius = (float)((66 * fogScale) / 2 + pulse * 1.2);
        var glowRadius = (float)(radius * (0.30 + pulse * 0.025));
        var opacityMultiplier = Math.Max(0.28, Math.Min(fogOpacity / 0.12, 2.2));

        // Outer radial gradient: cool grey -> warmer mid -> faint warm -> transparent.
        var colors = new[]
        {
            ColorWithAlpha(0.38, 0.36, 0.35, (0.40 + pulse * 0.02) * opacityMultiplier),
            ColorWithAlpha(0.43, 0.41, 0.40, (0.28 + pulse * 0.015) * opacityMultiplier),
            ColorWithAlpha(0.46, 0.44, 0.43, 0.11 * opacityMultiplier),
            new SKColor(red: 153, green: 153, blue: 153, alpha: 0),
        };
        var positions = new[] { 0f, 0.50f, 0.82f, 1f };

        using (var paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint((float)center.X, (float)center.Y),
                radius, colors, positions, SKShaderTileMode.Clamp),
            IsAntialias = true,
        })
        {
            canvas.DrawRect(
                (float)(center.X - radius), (float)(center.Y - radius),
                radius * 2, radius * 2, paint);
        }

        // Inner core gradient — very faint highlight, contributes the
        // warm glow under the cursor body.
        var coreColors = new[]
        {
            ColorWithAlpha(0.41, 0.39, 0.38, (0.020 + pulse * 0.006) * opacityMultiplier),
            ColorWithAlpha(0.44, 0.41, 0.40, 0.008 * opacityMultiplier),
            new SKColor(red: 204, green: 204, blue: 204, alpha: 0),
        };
        var corePositions = new[] { 0f, 0.62f, 1f };

        using (var corePaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint((float)center.X, (float)center.Y),
                glowRadius, coreColors, corePositions, SKShaderTileMode.Clamp),
            IsAntialias = true,
        })
        {
            canvas.DrawRect(
                (float)(center.X - glowRadius), (float)(center.Y - glowRadius),
                glowRadius * 2, glowRadius * 2, corePaint);
        }
    }

    private static void DrawPointer(SKCanvas canvas, Point center, double rotation,
        double clickProgress, Vector cursorBodyOffset, Point boundsMidpoint)
    {
        var pointerRect = new Rect(
            center.X - CursorGlyphMetrics.PointerWidth / 2,
            center.Y - CursorGlyphMetrics.PointerHeight / 2,
            CursorGlyphMetrics.PointerWidth,
            CursorGlyphMetrics.PointerHeight);
        var outerPath = PointerPath(pointerRect);

        canvas.Save();
        // Body transform — translate to body-offset midpoint, rotate by
        // motion angle, scale slightly by click pulse, untranslate.
        canvas.Translate((float)(boundsMidpoint.X + cursorBodyOffset.X), (float)(boundsMidpoint.Y + cursorBodyOffset.Y));
        canvas.RotateRadians((float)rotation);
        canvas.Scale((float)(1 - clickProgress * 0.04), (float)(1 + clickProgress * 0.02));
        canvas.Translate(-(float)(boundsMidpoint.X + cursorBodyOffset.X), -(float)(boundsMidpoint.Y + cursorBodyOffset.Y));
        // Artwork rotation — translate to pointer centre, rotate by
        // pointerArtworkRotation, untranslate.
        canvas.Translate((float)center.X, (float)center.Y);
        canvas.RotateRadians((float)CursorGlyphMetrics.PointerArtworkRotation);
        canvas.Translate(-(float)center.X, -(float)center.Y);

        // Shadow underlay (blurred, semi-opaque).
        var shadowBlur = 3.2 + clickProgress * 1.4;
        using (var shadowPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(red: 0, green: 0, blue: 0, alpha: (byte)(0.05 * 255)),
            ImageFilter = SKImageFilter.CreateDropShadow(
                dx: 0, dy: -0.35f,
                sigmaX: (float)shadowBlur, sigmaY: (float)shadowBlur,
                color: new SKColor(red: 0, green: 0, blue: 0, alpha: (byte)(0.11 * 255))),
            IsAntialias = true,
        })
        {
            canvas.DrawPath(outerPath, shadowPaint);
        }

        // Solid pointer body.
        using (var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill, Color = PointerFill, IsAntialias = true,
        })
        {
            canvas.DrawPath(outerPath, fillPaint);
        }

        // Bright stroke outline.
        using (var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = PointerStroke,
            StrokeWidth = 1.55f,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true,
        })
        {
            canvas.DrawPath(outerPath, strokePaint);
        }

        canvas.Restore();
    }

    /// <summary>
    /// Procedural pointer contour. Upstream uses a hand-drawn 30-row
    /// table mapping y -> (minX, maxX) plus a source rect of (10,10,
    /// 38,39). We walk left boundary then reversed right boundary so
    /// the path closes around the cursor silhouette.
    /// </summary>
    private static SKPath PointerPath(Rect rect)
    {
        var contourRows = new (double Y, double MinX, double MaxX)[]
        {
            (39, 17, 21), (38, 16, 22), (37, 15, 22), (36, 15, 23), (35, 15, 24),
            (34, 15, 24), (33, 14, 25), (32, 14, 25), (31, 14, 26), (30, 14, 27),
            (29, 13, 29), (28, 13, 31), (27, 13, 34), (26, 13, 36), (25, 13, 37),
            (24, 12, 37), (23, 12, 37), (22, 12, 37), (21, 12, 37), (20, 12, 36),
            (19, 11, 36), (18, 11, 34), (17, 11, 32), (16, 11, 30), (15, 10, 27),
            (14, 10, 25), (13, 10, 23), (12, 11, 21), (11, 11, 19), (10, 13, 16),
        };
        const double sourceMinX = 10, sourceMaxX = 38, sourceMinY = 10, sourceMaxY = 39;

        SKPoint MappedPoint(double x, double y) => new(
            (float)(rect.X + ((x - sourceMinX) / (sourceMaxX - sourceMinX) * rect.Width)),
            (float)(rect.Y + ((y - sourceMinY) / (sourceMaxY - sourceMinY) * rect.Height)));

        var path = new SKPath();
        // Left boundary in source order.
        var first = MappedPoint(contourRows[0].MinX, contourRows[0].Y);
        path.MoveTo(first);
        for (var i = 1; i < contourRows.Length; i++)
            path.LineTo(MappedPoint(contourRows[i].MinX, contourRows[i].Y));
        // Right boundary in reversed source order.
        for (var i = contourRows.Length - 1; i >= 0; i--)
            path.LineTo(MappedPoint(contourRows[i].MaxX, contourRows[i].Y));
        path.Close();
        return path;
    }

    private static SKColor ColorWithAlpha(double r, double g, double b, double a)
        => new(
            red: (byte)Math.Clamp(r * 255, 0, 255),
            green: (byte)Math.Clamp(g * 255, 0, 255),
            blue: (byte)Math.Clamp(b * 255, 0, 255),
            alpha: (byte)Math.Clamp(a * 255, 0, 255));
}
