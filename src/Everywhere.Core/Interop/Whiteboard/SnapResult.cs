using Avalonia;

namespace Everywhere.Interop.Whiteboard;

/// <summary>
/// Snapper output for one annotation. <see cref="Rect"/> is bound to the
/// union of the snapped leaves' bboxes; <see cref="Leaves"/> is the
/// Label/Hyperlink set the agent will see. <see cref="Rejected"/> means
/// the gesture missed by too much — UX should ask the user to redraw.
/// </summary>
public sealed record SnapResult(
    Rect Rect,
    IReadOnlyList<IVisualElement> Leaves,
    bool Rejected = false,
    string RejectReason = "",
    double Confidence = 1.0);
