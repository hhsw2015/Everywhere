// Line-for-line port of:
//   packages/OpenComputerUseKit/Sources/OpenComputerUseKit/CursorMotionModel.swift
// from iFurySt/open-codex-computer-use. Upstream comments and parameter
// values preserved verbatim. CGPoint/CGVector/CGRect -> Avalonia.Point /
// Avalonia.Vector / Avalonia.Rect. Math intrinsics mapped to System.Math
// (atan2, hypot=sqrt(dx*dx+dy*dy), pow, sqrt). Identifiers kept in their
// Swift form so a reader can find them in the original source.
//
// This file is the kinematics + scoring brain; the *visual* (Skia draw +
// motion-tracking glyph) lives in CursorRenderer.cs. They map to the
// Swift SoftwareCursorOverlay + SoftwareCursorGlyphRenderer files.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;

namespace Everywhere.Mcp.CursorOverlay;

internal readonly record struct CursorMotionSegment(Point End, Point Control1, Point Control2);

internal sealed record CursorMotionPath
{
    public Point Start { get; }
    public Point End { get; }
    public Point? StartControl { get; }
    public Point? Arc { get; }
    public Point? ArcIn { get; }
    public Point? ArcOut { get; }
    public Point? EndControl { get; }
    public IReadOnlyList<CursorMotionSegment> Segments { get; }
    public double CurveScale { get; }

    public CursorMotionPath(
        Point start,
        Point end,
        Point? startControl = null,
        Point? arc = null,
        Point? arcIn = null,
        Point? arcOut = null,
        Point? endControl = null,
        IReadOnlyList<CursorMotionSegment>? segments = null,
        double curveScale = 1)
    {
        Start = start; End = end;
        StartControl = startControl; Arc = arc; ArcIn = arcIn; ArcOut = arcOut; EndControl = endControl;
        Segments = segments ?? new List<CursorMotionSegment>();
        CurveScale = curveScale;
    }

    /// <summary>
    /// Mirrors Swift init(start:end:curveDirection:curveScale:): builds a
    /// single-segment curved path biased toward an inflection side.
    /// </summary>
    public static CursorMotionPath FromEndpoints(Point start, Point end, double? curveDirection = null, double curveScale = 1)
    {
        var delta = end.Sub(start);
        var distance = Math.Max(delta.Length(), 1);
        var normal = delta.Perpendicular().Normalized();
        var resolvedCurveDirection = curveDirection ?? (delta.X >= 0 ? 1 : -1);
        var resolvedCurveScale = Math.Max(curveScale, 0);
        var curveAmount = Math.Min(Math.Max(distance * 0.22, 28), 110) * resolvedCurveScale;
        var controlOffset = normal.Scaled(curveAmount * resolvedCurveDirection);
        var c1Base = new Point(
            start.X + (delta.X * (resolvedCurveScale == 0 ? 1.0 / 3.0 : 0.18)),
            start.Y + (delta.Y * (resolvedCurveScale == 0 ? 1.0 / 3.0 : 0.10)));
        var c2Base = new Point(
            start.X + (delta.X * (resolvedCurveScale == 0 ? 2.0 / 3.0 : 0.80)),
            start.Y + (delta.Y * (resolvedCurveScale == 0 ? 2.0 / 3.0 : 0.96)));
        var control1 = c1Base + controlOffset;
        var control2 = c2Base + controlOffset.Scaled(0.48);
        return new CursorMotionPath(
            start, end,
            startControl: control1, endControl: control2,
            segments: new[] { new CursorMotionSegment(end, control1, control2) },
            curveScale: resolvedCurveScale);
    }

    public Point PointAt(double progress) => Sample(progress).Point;
    public Vector TangentAt(double progress) => Sample(progress).Tangent;

    public (Point Point, Vector Tangent) Sample(double progress)
    {
        if (Segments.Count == 0) return (Start, new Vector(1, 0));
        var clamped = Math.Clamp(progress, 0, 1);
        int segmentIndex;
        double localT;
        if (clamped >= 1) { segmentIndex = Segments.Count - 1; localT = 1; }
        else
        {
            var scaled = clamped * Segments.Count;
            segmentIndex = Math.Min((int)scaled, Segments.Count - 1);
            localT = scaled - segmentIndex;
        }
        var seg = Segments[segmentIndex];
        var segmentStart = segmentIndex == 0 ? Start : Segments[segmentIndex - 1].End;
        var point = CubicMath.SampleCubic(segmentStart, seg.Control1, seg.Control2, seg.End, localT);
        var tangent = CubicMath.SampleCubicTangent(segmentStart, seg.Control1, seg.Control2, seg.End, localT).Normalized();
        return (point, tangent);
    }

    public IReadOnlyList<Point> SampledConstraintPoints(int samplesPerSegment = 6)
    {
        var totalSteps = Math.Max(Segments.Count * Math.Max(samplesPerSegment, 1), 1);
        var result = new List<Point>(totalSteps);
        for (var step = 1; step <= totalSteps; step++)
            result.Add(PointAt((double)step / totalSteps));
        return result;
    }

    public CursorMotionMeasurement Measure(Rect? bounds, double minStepDistance = 0.01, int samplesPerSegment = 24)
    {
        double totalLength = 0, angleChangeEnergy = 0, maxAngleChange = 0, totalTurn = 0;
        var staysInBounds = bounds is null || bounds.Value.Contains(Start, padding: 20);
        var previousPoint = Start;
        double? previousAngle = null;
        var totalSteps = Math.Max(Segments.Count * Math.Max(samplesPerSegment, 1), 1);
        for (var step = 1; step <= totalSteps; step++)
        {
            var progress = (double)step / totalSteps;
            var point = PointAt(progress);
            var delta = point.Sub(previousPoint);
            var stepLength = delta.Length();
            if (bounds is { } b && staysInBounds) staysInBounds = b.Contains(point, padding: 20);
            if (stepLength > minStepDistance)
            {
                var angle = Math.Atan2(delta.Y, delta.X);
                totalLength += stepLength;
                if (previousAngle is { } prev)
                {
                    var angleDelta = angle - prev;
                    while (angleDelta > Math.PI) angleDelta -= 2 * Math.PI;
                    while (angleDelta < -Math.PI) angleDelta += 2 * Math.PI;
                    angleChangeEnergy += angleDelta * angleDelta;
                    var abs = Math.Abs(angleDelta);
                    maxAngleChange = Math.Max(maxAngleChange, abs);
                    totalTurn += abs;
                }
                previousAngle = angle;
                previousPoint = point;
            }
        }
        return new CursorMotionMeasurement(totalLength, angleChangeEnergy, maxAngleChange, totalTurn, staysInBounds);
    }
}

internal readonly record struct CursorMotionMeasurement(
    double Length, double AngleChangeEnergy, double MaxAngleChange, double TotalTurn, bool StaysInBounds);

internal readonly record struct CursorMotionCandidate(
    string Identifier, CursorMotionKind Kind, int Side,
    double? TableAScale, double? TableBScale,
    CursorMotionPath Path, CursorMotionMeasurement Measurement, double Score);

internal enum CursorMotionKind { Base, Arched }

// =====================================================================
// Critically-damped spring config used by the *progress* animator
// (parametric path traversal) — official upstream values.
// =====================================================================
internal readonly record struct CursorMotionSpringConfiguration(
    double Response, double DampingFraction, double Stiffness, double Drag,
    double Dt, double CloseEnoughProgressThreshold, double CloseEnoughDistanceThreshold,
    double IdleVelocityThreshold)
{
    public static readonly CursorMotionSpringConfiguration Official = CreateOfficial();

    private static CursorMotionSpringConfiguration CreateOfficial()
    {
        const double response = 1.4;
        const double dampingFraction = 0.9;
        const double dt = 1.0 / 240.0;
        const double idleVelocityThreshold = 28_800;
        var rawStiffness = response > 0 ? Math.Pow((2 * Math.PI) / response, 2) : double.PositiveInfinity;
        var stiffness = Math.Min(rawStiffness, idleVelocityThreshold);
        var drag = 2 * dampingFraction * Math.Sqrt(stiffness);
        return new CursorMotionSpringConfiguration(
            response, dampingFraction, stiffness, drag, dt,
            CloseEnoughProgressThreshold: 1,
            CloseEnoughDistanceThreshold: 0.01,
            IdleVelocityThreshold: idleVelocityThreshold);
    }
}

internal struct CursorMotionSpringState
{
    public double Time;
    public double Velocity;
    public double Force;
}

internal static class CursorMotionProgressAnimator
{
    public static (double Current, CursorMotionSpringState State) Advance(
        double current, double target, CursorMotionSpringState state,
        CursorMotionSpringConfiguration configuration = default)
    {
        if (configuration.Dt == 0) configuration = CursorMotionSpringConfiguration.Official;
        var halfDT = configuration.Dt * 0.5;
        var velocityHalf = state.Velocity + (state.Force * halfDT);
        var nextCurrent = current + (velocityHalf * configuration.Dt);
        var force = (configuration.Stiffness * (target - nextCurrent)) + (-configuration.Drag * velocityHalf);
        var velocity = velocityHalf + (force * halfDT);
        return (nextCurrent, new CursorMotionSpringState
        {
            Time = state.Time + configuration.Dt,
            Velocity = velocity,
            Force = force,
        });
    }

    public static (double Current, CursorMotionSpringState State) AdvanceTo(
        double current, double target, CursorMotionSpringState state,
        CursorMotionSpringConfiguration configuration, double targetTime)
    {
        if (configuration.Dt == 0) configuration = CursorMotionSpringConfiguration.Official;
        var adjustedState = state;
        var adjustedCurrent = current;
        if ((targetTime - adjustedState.Time) > 1) adjustedState.Time = targetTime - (1.0 / 60.0);
        while (adjustedState.Time < targetTime)
            (adjustedCurrent, adjustedState) = Advance(adjustedCurrent, target, adjustedState, configuration);
        return (adjustedCurrent, adjustedState);
    }

    public static bool IsCloseEnough(double progress, double target = 1,
        CursorMotionSpringConfiguration configuration = default)
    {
        if (configuration.Dt == 0) configuration = CursorMotionSpringConfiguration.Official;
        return progress >= configuration.CloseEnoughProgressThreshold
            && Math.Abs(target - progress) <= configuration.CloseEnoughDistanceThreshold;
    }

    public static double CloseEnoughTime(CursorMotionSpringConfiguration configuration = default)
    {
        if (configuration.Dt == 0) configuration = CursorMotionSpringConfiguration.Official;
        double current = 0;
        var state = default(CursorMotionSpringState);
        for (var step = 1; step < 4096; step++)
        {
            var targetTime = step * configuration.Dt;
            (current, state) = AdvanceTo(current, 1, state, configuration, targetTime);
            if (IsCloseEnough(current, 1, configuration)) return state.Time;
        }
        return 1.43;
    }
}

// =====================================================================
// OfficialCursorMotionModel — 1:1 port of the "official" candidate
// generator (8-of-12 column tables, full-vs-scaled guide, base + arched
// families). Constants verbatim.
// =====================================================================
internal static class OfficialCursorMotionModel
{
    public const double MinimumStepDistance = 0.01;
    public static readonly Vector GuideVectorInLocalBasis = new(-0.6946583704589973, 0.7193398003386512);
    public static readonly double[] TableA = { 0.55, 0.8, 1.05 };
    public static readonly double[] TableB = { 0.65, 1.0, 1.35 };
    public static readonly double CloseEnoughTimeValue = CursorMotionProgressAnimator.CloseEnoughTime();

    private const double NormalizationEpsilon = 0.001;
    private const double SideBiasScale = 0.65;
    private const double PrimaryDistanceScale = 0.41960295031576633;
    private const double DirectSpanScale = 0.9;
    private const double SecondaryDistanceScale = 0.2765523188064277;
    private const double ArcDistanceScale = 0.5783555327868779;
    private const double CandidateArcMin = 38;
    private const double CandidateArcMax = 440;
    private const double ScoreExcessLengthWeight = 320;
    private const double ScoreAngleEnergyWeight = 140;
    private const double ScoreMaxAngleWeight = 180;
    private const double ScoreTotalTurnWeight = 18;
    private const double ScoreOutOfBoundsPenalty = 45;

    public static List<CursorMotionCandidate> MakeCandidates(Point start, Point end, Rect? bounds)
    {
        var delta = end.Sub(start);
        var distance = Math.Max(delta.Length(), NormalizationEpsilon);
        var direction = delta.Normalized();
        var localNormal = direction.Perpendicular();
        var guide = direction.Scaled(GuideVectorInLocalBasis.X) + localNormal.Scaled(GuideVectorInLocalBasis.Y);
        var reverseGuide = guide.Scaled(-1);

        var (startExtentPre, endExtentPre) = BinaryPiecewisePrimaryExtents(distance);
        var startExtent = Math.Min(startExtentPre, ClipPositiveRay(start, guide, bounds));
        var endExtent = Math.Min(endExtentPre, ClipPositiveRay(end, reverseGuide, bounds));
        var startExtentScaled = Math.Min(Math.Max(startExtent * SideBiasScale, 0), ClipPositiveRay(start, guide, bounds));
        var endExtentScaled = Math.Min(Math.Max(endExtent * SideBiasScale, 0), ClipPositiveRay(end, reverseGuide, bounds));

        var fullStartControl = start + guide.Scaled(startExtent);
        var fullEndControl = end - guide.Scaled(endExtent);
        var scaledStartControl = start + guide.Scaled(startExtentScaled);
        var scaledEndControl = end - guide.Scaled(endExtentScaled);

        var rawHandleExtent = BinaryPiecewiseHandleExtent(distance);
        var rawArcExtent = Math.Clamp(distance * ArcDistanceScale, CandidateArcMin, CandidateArcMax);

        var midpoint = new Point((start.X + end.X) * 0.5, (start.Y + end.Y) * 0.5);
        var signedNormal = localNormal;
        var cross = (guide.Y * direction.X) - (guide.X * direction.Y);
        if (cross < 0) signedNormal = signedNormal.Scaled(-1);
        var arcAnchorBias = guide.Scaled(startExtent * SideBiasScale);
        var forwardUnit = NormalizedOrDefault(
            direction.Scaled(distance) + signedNormal.Scaled(rawArcExtent),
            minimumLength: rawHandleExtent);

        var candidates = new List<CursorMotionCandidate>();
        candidates.Add(MakeCandidate(
            "base-full-guide", CursorMotionKind.Base, 0, null, null,
            new CursorMotionPath(start, end,
                startControl: fullStartControl, endControl: fullEndControl,
                segments: new[] { new CursorMotionSegment(end, fullStartControl, fullEndControl) },
                curveScale: 1),
            distance, bounds));
        candidates.Add(MakeCandidate(
            "base-scaled-guide", CursorMotionKind.Base, 0, null, null,
            new CursorMotionPath(start, end,
                startControl: scaledStartControl, endControl: scaledEndControl,
                segments: new[] { new CursorMotionSegment(end, scaledStartControl, scaledEndControl) },
                curveScale: SideBiasScale),
            distance, bounds));

        foreach (var outerScale in TableA)
        {
            var anchorOffset = signedNormal.Scaled(rawHandleExtent * outerScale);
            foreach (var innerScale in TableB)
            {
                var tangentSpan = forwardUnit.Scaled(rawArcExtent * innerScale);
                foreach (var side in new[] { 1, -1 })
                {
                    var anchor = midpoint + arcAnchorBias + anchorOffset.Scaled(side);
                    var arcIn = anchor - tangentSpan;
                    var arcOut = anchor + tangentSpan;
                    var path = new CursorMotionPath(
                        start, end,
                        startControl: fullStartControl,
                        arc: anchor, arcIn: arcIn, arcOut: arcOut,
                        endControl: fullEndControl,
                        segments: new[]
                        {
                            new CursorMotionSegment(anchor, fullStartControl, arcIn),
                            new CursorMotionSegment(end, arcOut, fullEndControl),
                        },
                        curveScale: innerScale);
                    var id = $"a{CursorIdentifier(outerScale)}-b{CursorIdentifier(innerScale)}-{(side > 0 ? "positive" : "negative")}";
                    candidates.Add(MakeCandidate(id, CursorMotionKind.Arched, side, outerScale, innerScale, path, distance, bounds));
                }
            }
        }
        return candidates;
    }

    public static CursorMotionCandidate? ChooseBestCandidate(IReadOnlyList<CursorMotionCandidate> candidates)
    {
        if (candidates.Count == 0) return null;
        var inBounds = candidates.Where(c => c.Measurement.StaysInBounds).ToList();
        var pool = inBounds.Count == 0 ? candidates : (IReadOnlyList<CursorMotionCandidate>)inBounds;
        var best = pool[0];
        for (var i = 1; i < pool.Count; i++)
        {
            var c = pool[i];
            if (c.Score < best.Score || (c.Score == best.Score && string.CompareOrdinal(c.Identifier, best.Identifier) < 0))
                best = c;
        }
        return best;
    }

    public static double CalibratedTravelDuration(double distance, CursorMotionMeasurement measurement)
        => CloseEnoughTimeValue;

    private static CursorMotionCandidate MakeCandidate(
        string identifier, CursorMotionKind kind, int side,
        double? tableAScale, double? tableBScale,
        CursorMotionPath path, double distance, Rect? bounds)
    {
        var measurement = path.Measure(bounds, MinimumStepDistance);
        var score = ScoreCandidate(distance, measurement);
        return new CursorMotionCandidate(identifier, kind, side, tableAScale, tableBScale, path, measurement, score);
    }

    private static double ScoreCandidate(double distance, CursorMotionMeasurement measurement)
    {
        var excessLengthRatio = Math.Max((measurement.Length / Math.Max(distance, 1)) - 1, 0);
        return (excessLengthRatio * ScoreExcessLengthWeight)
            + (measurement.AngleChangeEnergy * ScoreAngleEnergyWeight)
            + (measurement.MaxAngleChange * ScoreMaxAngleWeight)
            + (measurement.TotalTurn * ScoreTotalTurnWeight)
            + (measurement.StaysInBounds ? 0 : ScoreOutOfBoundsPenalty);
    }

    private static (double StartExtent, double EndExtent) BinaryPiecewisePrimaryExtents(double distance)
    {
        var primary = distance * PrimaryDistanceScale;
        var direct = distance * DirectSpanScale;
        var secondary = distance * 0.15;
        const double lowCutoff = 48;
        const double highCutoff = 640;
        if (primary < lowCutoff) return (lowCutoff, lowCutoff);
        if (primary < highCutoff) return (primary, direct);
        if (secondary < highCutoff) return (highCutoff, lowCutoff);
        return (highCutoff, highCutoff);
    }

    private static double BinaryPiecewiseHandleExtent(double distance)
    {
        var raw = distance * SecondaryDistanceScale;
        if (raw < 50) return 50;
        if (raw < 640) return raw;
        return 520;
    }

    private static double ClipPositiveRay(Point origin, Vector direction, Rect? bounds)
    {
        if (bounds is null) return double.PositiveInfinity;
        var b = bounds.Value;
        var limit = double.PositiveInfinity;
        if (direction.X > 0) limit = Math.Min(limit, (b.Right - origin.X) / direction.X);
        else if (direction.X < 0) limit = Math.Min(limit, (b.Left - origin.X) / direction.X);
        if (direction.Y > 0) limit = Math.Min(limit, (b.Bottom - origin.Y) / direction.Y);
        else if (direction.Y < 0) limit = Math.Min(limit, (b.Top - origin.Y) / direction.Y);
        return Math.Max(limit, 0);
    }

    private static Vector NormalizedOrDefault(Vector vector, double minimumLength)
    {
        var length = vector.Length();
        if (length < minimumLength || length < NormalizationEpsilon) return new Vector(1, 0);
        return vector.Scaled(1 / length);
    }

    private static string CursorIdentifier(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
}

// =====================================================================
// HeadingDrivenCursorMotionModel — line-by-line port of upstream
// heading-driven path search (10 named descriptors, turn/brake/orbit
// families, signed-side preference, weighted multi-axis score).
// =====================================================================
internal static class HeadingDrivenCursorMotionModel
{
    private const double DefaultStartHandle = 0.29;
    private const double DefaultEndHandle = 0.08;
    private const double DefaultArcSize = 0.06;
    private const double DefaultArcFlow = 0.64;
    private const double NormalizationEpsilon = 0.001;

    public static List<CursorMotionCandidate> MakeCandidates(
        Point start, Point end, Rect? bounds, Vector startForward, Vector endForward)
    {
        var metrics = new MotionMetrics(start, end);
        var resolvedStartForward = NormalizedOrDefault(startForward);
        var resolvedEndForward = NormalizedOrDefault(endForward);
        var preferredSide = PreferredTurnSide(metrics, resolvedStartForward, resolvedEndForward);
        var scoringContext = new MotionScoringContext(metrics, resolvedStartForward, resolvedEndForward, preferredSide);
        var result = new List<CursorMotionCandidate>();
        foreach (var descriptor in Descriptors(metrics, preferredSide))
        {
            var path = MakePath(start, end, metrics, descriptor, resolvedStartForward, resolvedEndForward);
            result.Add(MakeCandidate(descriptor.Id, descriptor.Kind, descriptor.Side, null, null,
                path, bounds, scoringContext, descriptor));
        }
        return result;
    }

    public static CursorMotionCandidate? ChooseBestCandidate(IReadOnlyList<CursorMotionCandidate> candidates)
        => OfficialCursorMotionModel.ChooseBestCandidate(candidates);

    public static double CalibratedTravelDuration(double distance, CursorMotionMeasurement measurement)
        => OfficialCursorMotionModel.CalibratedTravelDuration(distance, measurement);

    private static CursorMotionPath MakePath(Point start, Point end, MotionMetrics metrics,
        MotionDescriptor descriptor, Vector startForward, Vector endForward)
    {
        var distance = metrics.Distance;
        var direction = metrics.Direction;
        var normal = metrics.Normal;
        var resolvedFlow = Math.Clamp(DefaultArcFlow + descriptor.FlowShift, 0, 1);
        var flowBias = (resolvedFlow - 0.5) * distance * 0.18;
        var baseStartReach = distance * (0.10 + DefaultStartHandle * 0.56);
        var baseEndReach = distance * (0.11 + DefaultEndHandle * 0.62);
        var distanceLift = 0.68 + (metrics.FarFactor * 0.56);
        var baseArcHeight = Math.Min(
            Math.Max(distance * (0.10 + DefaultArcSize * 0.92) * descriptor.ArcScale * distanceLift, 20),
            distance * 0.96);
        var sideSign = (double)descriptor.Side;
        var arcVector = new Vector(normal.X * baseArcHeight * sideSign, normal.Y * baseArcHeight * sideSign);
        var startGuide = ResolvedGuide(direction, startForward, normal, sideSign,
            descriptor.StartLineWeight, descriptor.StartHeadingWeight, descriptor.StartGuideNormalBias);
        var endGuide = ResolvedGuide(direction, endForward, normal, sideSign,
            descriptor.EndLineWeight, descriptor.EndHeadingWeight, descriptor.EndGuideNormalBias);
        var startReach = Math.Max(baseStartReach * descriptor.StartReachScale + flowBias * descriptor.StartFlowWeight, 12);
        var endReach = Math.Max(baseEndReach * descriptor.EndReachScale - flowBias * descriptor.EndFlowWeight, 12);
        var control1Base = start + startGuide.Scaled(startReach);
        var control2Base = end - endGuide.Scaled(endReach);
        var control1 = control1Base + arcVector.Scaled(descriptor.StartNormalScale);
        var control2 = control2Base + arcVector.Scaled(descriptor.EndNormalScale);
        var resolvedArcHeight = baseArcHeight * Math.Max(Math.Max(
            Math.Abs(descriptor.StartNormalScale), Math.Abs(descriptor.EndNormalScale)), 0.12);
        return new CursorMotionPath(start, end,
            startControl: control1, endControl: control2,
            segments: new[] { new CursorMotionSegment(end, control1, control2) },
            curveScale: resolvedArcHeight);
    }

    private static CursorMotionCandidate MakeCandidate(string identifier, CursorMotionKind kind, int side,
        double? tableAScale, double? tableBScale, CursorMotionPath path, Rect? bounds,
        MotionScoringContext context, MotionDescriptor descriptor)
    {
        var measurement = path.Measure(bounds, OfficialCursorMotionModel.MinimumStepDistance);
        var score = ScoreCandidate(measurement, path, descriptor, context);
        return new CursorMotionCandidate(identifier, kind, side, tableAScale, tableBScale, path, measurement, score);
    }

    private static double ScoreCandidate(CursorMotionMeasurement measurement, CursorMotionPath path,
        MotionDescriptor descriptor, MotionScoringContext context)
    {
        var distance = Math.Max(context.Metrics.Distance, 1);
        var excessLengthRatio = Math.Max((measurement.Length / distance) - 1, 0);
        var startTangent = NormalizedOrDefault(path.TangentAt(0.04));
        var endTangent = NormalizedOrDefault(path.TangentAt(0.96));
        var startHeadingError = Math.Abs(SignedAngle(context.StartForward, startTangent));
        var endHeadingError = Math.Abs(SignedAngle(endTangent, context.EndForward));
        var score = descriptor.ScoreBias;
        score += excessLengthRatio * 180;
        score += measurement.AngleChangeEnergy * 90;
        score += measurement.MaxAngleChange * 85;
        score += measurement.TotalTurn * (descriptor.Side == 0 ? 10 : 12);
        score += startHeadingError * 150;
        score += endHeadingError * 120;
        if (descriptor.Side == 0)
        {
            score += context.TurnDemand * 130;
            score += context.ArrivalDemand * 30;
        }
        else
        {
            score += context.Directness * 90;
            if (descriptor.Side != context.PreferredSide)
                score += Math.Max(context.TurnDemand, 0.45) * 200;
        }
        switch (descriptor.Family)
        {
            case "turn":   score += (1 - context.TurnDemand) * 55; break;
            case "brake":  score += (1 - context.ArrivalDemand) * 40; break;
            case "orbit":  score += context.Directness * 70; break;
            case "direct": score += Math.Max(context.TurnDemand - 0.12, 0) * 80; break;
        }
        if (!measurement.StaysInBounds) score += 90;
        return score;
    }

    private static IReadOnlyList<MotionDescriptor> Descriptors(MotionMetrics metrics, int preferredSide)
    {
        var orbitScale = 0.82 + (metrics.FarFactor * 0.26);
        var turnaroundScale = 0.90 + (metrics.FarFactor * 0.30);
        var brakingScale = 0.74 + (metrics.FarFactor * 0.24);
        return new[]
        {
            new MotionDescriptor("direct-tight", "direct", 0,
                0.90, 0.86, 1.12, 1.04, 0.18, 0.20, 0.02, 0.02, 0.0, 0.0, 0.02, 0.02, -0.02, 0.16, 18),
            new MotionDescriptor("direct-soft", "direct", 0,
                0.98, 0.94, 1.04, 0.96, 0.22, 0.28, 0.04, 0.08, 0.0, 0.04, 0.04, 0.08, 0.02, 0.24, 24),
            new MotionDescriptor("turn-primary-tight", "turn", preferredSide,
                1.26, 1.30, -0.24, -0.04, 1.50, 1.18, 0.46, 0.08, 0.30, 0.16, -0.30, 0.20, -0.08, turnaroundScale, 40),
            new MotionDescriptor("turn-primary-wide", "turn", preferredSide,
                1.30, 1.36, -0.28, -0.10, 1.54, 1.24, 0.58, 0.12, 0.34, 0.20, -0.34, 0.24, 0.06, turnaroundScale * 1.06, 46),
            new MotionDescriptor("brake-primary-tight", "brake", preferredSide,
                0.92, 1.42, 0.50, -0.20, 0.70, 1.52, 0.16, 0.20, 0.10, 0.26, 0.10, 0.32, -0.04, brakingScale, 44),
            new MotionDescriptor("brake-primary-wide", "brake", preferredSide,
                0.98, 1.50, 0.44, -0.26, 0.74, 1.62, 0.22, 0.26, 0.12, 0.32, 0.14, 0.38, 0.04, brakingScale * 1.04, 50),
            new MotionDescriptor("orbit-primary-tight", "orbit", preferredSide,
                0.90, 0.98, 0.72, 0.76, 0.30, 0.22, 0.90, 0.82, 0.16, 0.06, 0.26, 0.12, -0.06, orbitScale, 54),
            new MotionDescriptor("orbit-primary-wide", "orbit", preferredSide,
                0.94, 1.02, 0.68, 0.82, 0.28, 0.22, 1.02, 0.94, 0.18, 0.08, 0.30, 0.16, 0.06, orbitScale * 1.06, 60),
            new MotionDescriptor("turn-secondary", "turn", -preferredSide,
                1.18, 1.26, -0.18, 0.02, 1.32, 1.08, 0.34, 0.06, 0.22, 0.14, -0.20, 0.14, 0.02, turnaroundScale * 0.92, 88),
            new MotionDescriptor("brake-secondary", "brake", -preferredSide,
                0.90, 1.34, 0.52, -0.16, 0.62, 1.40, 0.12, 0.18, 0.08, 0.20, 0.10, 0.28, -0.02, brakingScale * 0.92, 96),
        };
    }

    private static int PreferredTurnSide(MotionMetrics metrics, Vector startForward, Vector endForward)
    {
        var startDelta = SignedAngle(startForward, metrics.Direction);
        if (Math.Abs(startDelta) > 0.16) return startDelta > 0 ? 1 : -1;
        var endDelta = SignedAngle(metrics.Direction, endForward);
        if (Math.Abs(endDelta) > 0.18) return endDelta > 0 ? -1 : 1;
        if (Math.Abs(metrics.Dy) > Math.Abs(metrics.Dx) * 0.72) return metrics.Dy > 0 ? -1 : 1;
        return metrics.Dx >= 0 ? 1 : -1;
    }

    private static Vector ResolvedGuide(Vector line, Vector forward, Vector normal,
        double sideSign, double lineWeight, double headingWeight, double normalBias)
        => NormalizedOrDefault(line.Scaled(lineWeight) + forward.Scaled(headingWeight) + normal.Scaled(normalBias * sideSign));

    public static Vector NormalizedOrDefault(Vector vector)
    {
        var length = Math.Max(vector.Length(), NormalizationEpsilon);
        return new Vector(vector.X / length, vector.Y / length);
    }

    public static double SignedAngle(Vector lhs, Vector rhs)
        => Math.Atan2((lhs.X * rhs.Y) - (lhs.Y * rhs.X), (lhs.X * rhs.X) + (lhs.Y * rhs.Y));

    internal readonly struct MotionScoringContext
    {
        public MotionMetrics Metrics { get; }
        public Vector StartForward { get; }
        public Vector EndForward { get; }
        public int PreferredSide { get; }

        public MotionScoringContext(MotionMetrics metrics, Vector startForward, Vector endForward, int preferredSide)
        {
            Metrics = metrics; StartForward = startForward; EndForward = endForward; PreferredSide = preferredSide;
        }

        public double TurnDemand
            => Math.Min(Math.Abs(SignedAngle(StartForward, Metrics.Direction)) / Math.PI, 1);

        public double ArrivalDemand
            => Math.Min(Math.Abs(SignedAngle(Metrics.Direction, EndForward)) / Math.PI, 1);

        public double Directness => Math.Clamp(1 - Math.Max(TurnDemand, ArrivalDemand * 0.82), 0, 1);
    }

    internal readonly record struct MotionDescriptor(
        string Id, string Family, int Side,
        double StartReachScale, double EndReachScale,
        double StartLineWeight, double EndLineWeight,
        double StartHeadingWeight, double EndHeadingWeight,
        double StartNormalScale, double EndNormalScale,
        double StartGuideNormalBias, double EndGuideNormalBias,
        double StartFlowWeight, double EndFlowWeight,
        double FlowShift, double ArcScale, double ScoreBias)
    {
        public CursorMotionKind Kind => Family == "direct" ? CursorMotionKind.Base : CursorMotionKind.Arched;
    }

    internal readonly struct MotionMetrics
    {
        public Point Start { get; }
        public Point End { get; }
        public double Dx { get; }
        public double Dy { get; }
        public double Distance { get; }
        public Vector Direction { get; }
        public Vector Normal { get; }
        public double HorizontalFactor { get; }
        public double VerticalFactor { get; }
        public double DiagonalFactor { get; }
        public double CloseFactor { get; }
        public double FarFactor { get; }

        public MotionMetrics(Point start, Point end)
        {
            Start = start; End = end;
            Dx = end.X - start.X;
            Dy = end.Y - start.Y;
            Distance = Math.Max(Math.Sqrt(Dx * Dx + Dy * Dy), 1);
            Direction = NormalizedOrDefault(new Vector(Dx, Dy));
            Normal = NormalizedOrDefault(new Vector(-Direction.Y, Direction.X));
            HorizontalFactor = Math.Abs(Dx) / Distance;
            VerticalFactor = Math.Abs(Dy) / Distance;
            DiagonalFactor = Math.Min(HorizontalFactor, VerticalFactor) * 2;
            CloseFactor = Math.Clamp(1 - (Distance / 280), 0, 1);
            FarFactor = Math.Clamp((Distance - 180) / 540, 0, 1);
        }
    }
}

// =====================================================================
// Visual dynamics — port of the second spring config (tip + angle),
// the renderer state, and the rolling-step advance. Drives the cursor
// glyph's body offset / heading wobble / fog trail.
// =====================================================================
internal readonly record struct CursorVisualSpringConfiguration(
    double Response, double DampingFraction, double Stiffness, double Drag, double Dt, double IdleVelocityThreshold)
{
    public static CursorVisualSpringConfiguration Create(double response, double dampingFraction,
        double dt = 1.0 / 240.0, double idleVelocityThreshold = 28_800)
    {
        var rawStiffness = response > 0 ? Math.Pow((2 * Math.PI) / response, 2) : double.PositiveInfinity;
        var stiffness = Math.Min(rawStiffness, idleVelocityThreshold);
        var drag = 2 * dampingFraction * Math.Sqrt(stiffness);
        return new CursorVisualSpringConfiguration(response, dampingFraction, stiffness, drag, dt, idleVelocityThreshold);
    }
}

internal readonly record struct CursorVisualDynamicsConfiguration(
    CursorVisualSpringConfiguration TipSpring,
    CursorVisualSpringConfiguration AngleSpring,
    double HeadingVelocityFloor,
    double AnimatedAngleOffsetMax,
    double BodyOffsetScale,
    double BodyOffsetMax,
    double BodyLateralScale,
    double BodyLateralMax,
    double FogOffsetScale,
    double FogOffsetMax,
    double FogOpacityBase,
    double FogOpacityVelocityScale,
    double FogScaleVelocityScale,
    double FogScaleMaxDelta)
{
    public static readonly CursorVisualDynamicsConfiguration OfficialInspired = new(
        TipSpring: CursorVisualSpringConfiguration.Create(0.18, 0.76),
        AngleSpring: CursorVisualSpringConfiguration.Create(0.24, 0.82),
        HeadingVelocityFloor: 14,
        AnimatedAngleOffsetMax: 0.26,
        BodyOffsetScale: 0.0012,
        BodyOffsetMax: 2.4,
        BodyLateralScale: 0.06,
        BodyLateralMax: 1.4,
        FogOffsetScale: 0.0045,
        FogOffsetMax: 9,
        FogOpacityBase: 0.12,
        FogOpacityVelocityScale: 0.00006,
        FogScaleVelocityScale: 0.00012,
        FogScaleMaxDelta: 0.22);
}

internal struct CursorVisualDynamicsState
{
    public double Time;
    public Point TipPosition;
    public Vector TipVelocity;
    public Vector TipForce;
    public double Angle;
    public double AngleVelocity;
    public double AngleForce;

    public static CursorVisualDynamicsState At(Point tipPosition, double time = 0)
        => new() { Time = time, TipPosition = tipPosition };
}

internal readonly record struct CursorVisualRenderState(
    Point TipPosition, double Rotation, Vector CursorBodyOffset, Vector FogOffset,
    double FogOpacity, double FogScale);

internal static class CursorVisualDynamicsAnimator
{
    public static (CursorVisualDynamicsState State, CursorVisualRenderState Render) Advance(
        CursorVisualDynamicsState state, Point targetTipPosition, double targetTime,
        double idleAngleOffset, double baseHeading, double renderYAxisMultiplier = 1,
        CursorVisualDynamicsConfiguration configuration = default)
    {
        if (configuration.TipSpring.Dt == 0) configuration = CursorVisualDynamicsConfiguration.OfficialInspired;
        var adjusted = state;
        if ((targetTime - adjusted.Time) > 1) adjusted.Time = targetTime - (1.0 / 60.0);
        while (adjusted.Time < targetTime)
            adjusted = AdvanceStep(adjusted, targetTipPosition, idleAngleOffset, baseHeading, renderYAxisMultiplier, configuration);
        return (adjusted, RenderState(adjusted, idleAngleOffset, baseHeading, renderYAxisMultiplier, configuration));
    }

    private static CursorVisualDynamicsState AdvanceStep(
        CursorVisualDynamicsState state, Point targetTipPosition,
        double idleAngleOffset, double baseHeading, double renderYAxisMultiplier,
        CursorVisualDynamicsConfiguration configuration)
    {
        var dt = configuration.TipSpring.Dt;
        var halfDT = dt * 0.5;
        var tipVelocityHalf = state.TipVelocity + state.TipForce.Scaled(halfDT);
        var nextTipPosition = state.TipPosition + tipVelocityHalf.Scaled(dt);
        var tipDisplacement = targetTipPosition.Sub(nextTipPosition);
        var tipForce = tipDisplacement.Scaled(configuration.TipSpring.Stiffness)
            + tipVelocityHalf.Scaled(-configuration.TipSpring.Drag);
        var tipVelocity = tipVelocityHalf + tipForce.Scaled(halfDT);
        var renderVelocity = VisualCursorScreenStateVelocity(tipVelocity, renderYAxisMultiplier);
        var targetAngle = ResolvedTargetAngle(renderVelocity, idleAngleOffset, baseHeading, configuration);
        var angleVelocityHalf = state.AngleVelocity + (state.AngleForce * halfDT);
        var nextAngle = NormalizeAngle(state.Angle + (angleVelocityHalf * dt));
        var angleError = NormalizeAngle(targetAngle - nextAngle);
        var angleForce = (angleError * configuration.AngleSpring.Stiffness) + (-configuration.AngleSpring.Drag * angleVelocityHalf);
        var angleVelocity = angleVelocityHalf + (angleForce * halfDT);
        return new CursorVisualDynamicsState
        {
            Time = state.Time + dt,
            TipPosition = nextTipPosition,
            TipVelocity = tipVelocity,
            TipForce = tipForce,
            Angle = NormalizeAngle(nextAngle),
            AngleVelocity = angleVelocity,
            AngleForce = angleForce,
        };
    }

    private static CursorVisualRenderState RenderState(
        CursorVisualDynamicsState state, double idleAngleOffset, double baseHeading,
        double renderYAxisMultiplier, CursorVisualDynamicsConfiguration configuration)
    {
        var renderVelocity = VisualCursorScreenStateVelocity(state.TipVelocity, renderYAxisMultiplier);
        var speed = renderVelocity.Length();
        var direction = speed > 0.001
            ? renderVelocity.Normalized()
            : new Vector(Math.Cos(baseHeading + idleAngleOffset), Math.Sin(baseHeading + idleAngleOffset));
        var bodyBackward = direction.Scaled(-Math.Min(speed * configuration.BodyOffsetScale, configuration.BodyOffsetMax));
        var lateralAmount = Math.Clamp(state.AngleVelocity * configuration.BodyLateralScale,
            -configuration.BodyLateralMax, configuration.BodyLateralMax);
        var bodyLateral = direction.Perpendicular().Scaled(lateralAmount);
        var cursorBodyOffset = bodyBackward + bodyLateral;
        var fogOffset = direction.Scaled(-Math.Min(speed * configuration.FogOffsetScale, configuration.FogOffsetMax))
            + bodyLateral.Scaled(0.6);
        var fogOpacity = Math.Min(configuration.FogOpacityBase + (speed * configuration.FogOpacityVelocityScale), 0.34);
        var fogScale = 1 + Math.Min(speed * configuration.FogScaleVelocityScale, configuration.FogScaleMaxDelta);
        var rotation = NormalizeAngle(state.Angle + Math.Clamp(idleAngleOffset,
            -configuration.AnimatedAngleOffsetMax, configuration.AnimatedAngleOffsetMax));
        return new CursorVisualRenderState(state.TipPosition, rotation, cursorBodyOffset, fogOffset, fogOpacity, fogScale);
    }

    private static double ResolvedTargetAngle(Vector velocity, double idleAngleOffset, double baseHeading,
        CursorVisualDynamicsConfiguration configuration)
    {
        var speed = velocity.Length();
        if (speed <= configuration.HeadingVelocityFloor) return 0;
        var heading = Math.Atan2(velocity.Y, velocity.X);
        return NormalizeAngle(heading - baseHeading);
    }

    private static double NormalizeAngle(double angle)
    {
        var v = angle;
        while (v > Math.PI) v -= 2 * Math.PI;
        while (v < -Math.PI) v += 2 * Math.PI;
        return v;
    }

    /// <summary>Identity by default; mac runtime flips Y vs render because
    /// NSScreen origins disagree with view-space — caller passes -1 in
    /// that case. Cross-platform layers using top-left origin pass 1.</summary>
    private static Vector VisualCursorScreenStateVelocity(Vector runtimeVelocity, double yAxisMultiplier)
        => new(runtimeVelocity.X, runtimeVelocity.Y * yAxisMultiplier);
}

// =====================================================================
// Math helpers (Swift extensions on CGPoint/CGVector/CGRect/CGFloat).
// Ported as static helpers so any caller can do `point + vector` style
// via overloaded ops below, plus extension-method conveniences.
// =====================================================================
internal static class CubicMath
{
    public static Point SampleCubic(Point start, Point control1, Point control2, Point end, double t)
    {
        var omt = 1 - t;
        var omt2 = omt * omt;
        var t2 = t * t;
        return new Point(
            (omt2 * omt * start.X) + (3 * omt2 * t * control1.X) + (3 * omt * t2 * control2.X) + (t2 * t * end.X),
            (omt2 * omt * start.Y) + (3 * omt2 * t * control1.Y) + (3 * omt * t2 * control2.Y) + (t2 * t * end.Y));
    }

    public static Vector SampleCubicTangent(Point start, Point control1, Point control2, Point end, double t)
    {
        var omt = 1 - t;
        return new Vector(
            (3 * omt * omt * (control1.X - start.X)) + (6 * omt * t * (control2.X - control1.X)) + (3 * t * t * (end.X - control2.X)),
            (3 * omt * omt * (control1.Y - start.Y)) + (6 * omt * t * (control2.Y - control1.Y)) + (3 * t * t * (end.Y - control2.Y)));
    }
}

internal static class CursorMotionExtensions
{
    public static double Length(this Vector v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);
    public static Vector Normalized(this Vector v)
    {
        var len = Math.Max(v.Length(), 0.001);
        return new Vector(v.X / len, v.Y / len);
    }
    public static Vector Perpendicular(this Vector v) => new(-v.Y, v.X);
    public static Vector Scaled(this Vector v, double factor) => new(v.X * factor, v.Y * factor);
    public static Vector Limited(this Vector v, double maxLength)
    {
        var len = v.Length();
        if (len <= maxLength || len < 0.001) return v;
        return v.Scaled(maxLength / len);
    }

    public static bool Contains(this Rect r, Point p, double padding)
        => r.Inflate(padding).Contains(p);

    // Avalonia.Point arithmetic. Point-Point=Vector, Point+Vector=Point,
    // Point-Vector=Point — but only for actual Avalonia.Vector. We
    // provide explicit helpers so call sites read like the Swift source.
    public static Vector ToVector(this Point p) => new(p.X, p.Y);
    public static Vector Sub(this Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
}
