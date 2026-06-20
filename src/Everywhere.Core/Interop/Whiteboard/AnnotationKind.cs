namespace Everywhere.Interop.Whiteboard;

/// <summary>
/// Semantic intent of a single whiteboard gesture. Each shape carries a different
/// meaning the agent can use to interpret the user's annotation:
///   Circle    — emphasis, this region matters
///   Underline — emphasis on a single line
///   Arrow     — pointing at a precise leaf
///   X         — strike-through / exclude this region
///   Unknown   — gesture didn't fit any known shape (parser fallback)
/// </summary>
public enum AnnotationKind
{
    Unknown,
    Circle,
    Underline,
    Arrow,
    X,
}
