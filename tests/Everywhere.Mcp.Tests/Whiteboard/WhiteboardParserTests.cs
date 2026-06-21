#pragma warning disable CS0618 // tests intentionally exercise the obsolete Parse overload
using Avalonia;
using Everywhere.Interop.Whiteboard;
using Everywhere.Mcp.Whiteboard;

namespace Everywhere.Mcp.Tests.Whiteboard;

/// <summary>
/// Verifies the C# port classifies the same canonical shapes as the Python
/// reference (whiteboard-sandbox/src/parser.py) — straightness ~0/0.5/0.9
/// + multi-stroke = X.
/// </summary>
[TestFixture]
public sealed class WhiteboardParserTests
{
    [Test]
    public void Parse_EmptyStrokes_ReturnsEmpty()
    {
        var result = WhiteboardParser.Parse([]);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Parse_Circle_ClassifiesAsCircle()
    {
        var pts = new List<StrokePoint>();
        const int n = 32;
        for (var i = 0; i < n; i++)
        {
            var a = i / (double)(n - 1) * 2 * Math.PI;
            pts.Add(new StrokePoint(500 + 100 * Math.Cos(a), 400 + 100 * Math.Sin(a), i * 5));
        }
        var result = WhiteboardParser.Parse([new Stroke(pts)]);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Kind, Is.EqualTo(AnnotationKind.Circle));
        Assert.That(result[0].BoundingRect.Width, Is.GreaterThan(150));
        Assert.That(result[0].BoundingRect.Height, Is.GreaterThan(150));
    }

    [Test]
    public void Parse_Underline_ClassifiesAsUnderline()
    {
        // Slight slope + jitter so bbox area > 1 (parser treats degenerate
        // stroke as Unknown, which is correct for a perfectly flat line).
        var pts = new List<StrokePoint>();
        for (var i = 0; i < 30; i++)
        {
            var f = i / 29.0;
            pts.Add(new StrokePoint(100 + 400 * f, 500 + f * 2 + (i % 2 == 0 ? 1 : -1), i * 4));
        }
        var result = WhiteboardParser.Parse([new Stroke(pts)]);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Kind, Is.EqualTo(AnnotationKind.Underline));
        // Underline rect lives ABOVE the stroke (parser shifts up by line height)
        Assert.That(result[0].BoundingRect.Bottom, Is.LessThanOrEqualTo(510));
    }

    [Test]
    public void Parse_Arrow_ClassifiesAsArrow()
    {
        var pts = new List<StrokePoint>();
        // shaft (100,100) -> (400,400)
        for (var i = 0; i < 30; i++)
        {
            var f = i / 29.0;
            pts.Add(new StrokePoint(100 + 300 * f, 100 + 300 * f, i * 3));
        }
        // arrowhead (doubles back from tip at +- 30 deg from reverse-shaft)
        var (tx, ty) = (400.0, 400.0);
        var headLen = 60.0;
        for (var sign = -1; sign <= 1; sign += 2)
        {
            var ang = Math.PI + sign * Math.PI / 6 + Math.PI / 4;
            for (var j = 0; j < 6; j++)
            {
                var f = j / 5.0;
                pts.Add(new StrokePoint(
                    tx + headLen * f * Math.Cos(ang),
                    ty + headLen * f * Math.Sin(ang),
                    100 + j * 3 + (sign > 0 ? 0 : 100)));
            }
        }
        var result = WhiteboardParser.Parse([new Stroke(pts)]);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Kind, Is.EqualTo(AnnotationKind.Arrow));
        var r = result[0].BoundingRect;
        Assert.That(r.Center.X, Is.EqualTo(tx).Within(5));
        Assert.That(r.Center.Y, Is.EqualTo(ty).Within(5));
    }

    [Test]
    public void Parse_TwoStrokes_ClassifiesAsX()
    {
        var s1Pts = new List<StrokePoint>();
        var s2Pts = new List<StrokePoint>();
        for (var i = 0; i < 15; i++)
        {
            var f = i / 14.0 * 2 - 1;
            s1Pts.Add(new StrokePoint(500 + 50 * f, 400 + 50 * f, i * 3));
            s2Pts.Add(new StrokePoint(500 + 50 * f, 400 - 50 * f, 200 + i * 3));
        }
        var result = WhiteboardParser.Parse([new Stroke(s1Pts), new Stroke(s2Pts)]);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Kind, Is.EqualTo(AnnotationKind.X));
    }

    [Test]
    public void Parse_TimeFarApart_GroupsSeparately()
    {
        var pts1 = MakeCircle(200, 200, 80, t0: 0);
        var pts2 = MakeCircle(800, 600, 80, t0: 5000);
        var result = WhiteboardParser.Parse([new Stroke(pts1), new Stroke(pts2)]);
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].Kind, Is.EqualTo(AnnotationKind.Circle));
        Assert.That(result[1].Kind, Is.EqualTo(AnnotationKind.Circle));
    }

    private static List<StrokePoint> MakeCircle(double cx, double cy, double r, double t0)
    {
        var pts = new List<StrokePoint>();
        const int n = 32;
        for (var i = 0; i < n; i++)
        {
            var a = i / (double)(n - 1) * 2 * Math.PI;
            pts.Add(new StrokePoint(cx + r * Math.Cos(a), cy + r * Math.Sin(a), t0 + i * 5));
        }
        return pts;
    }
}
