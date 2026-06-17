using Everywhere.Interop;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// One snapshot worth of element-index → element bindings, scoped per <c>appKey</c>.
/// Mirrors the upstream "session" struct: a fresh <see cref="AppSession"/> is issued on
/// every <c>get_app_state</c> for the same app, invalidating previously vended indices.
/// </summary>
public sealed class AppSession
{
    public required int Epoch { get; init; }

    public required string AppKey { get; init; }

    public required nint WindowHandle { get; init; }

    public required DateTime CapturedAtUtc { get; init; }

    public required IReadOnlyDictionary<int, IVisualElement> ElementsByIndex { get; init; }

    public IVisualElement? Resolve(int elementIndex)
    {
        ElementsByIndex.TryGetValue(elementIndex, out var element);
        return element;
    }
}
