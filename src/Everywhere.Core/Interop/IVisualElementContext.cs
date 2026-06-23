using Avalonia;
using Avalonia.Media.Imaging;

namespace Everywhere.Interop;

/// <summary>
/// Represents the mode of screen selection.
/// Used when picking elements or taking screenshots.
/// </summary>
public enum ScreenSelectionMode
{
    /// <summary>
    /// Pick a whole screen.
    /// </summary>
    [DynamicResourceKey(LocaleKey.ScreenSelectionMode_Screen)]
    Screen,

    /// <summary>
    /// Pick a window.
    /// </summary>
    [DynamicResourceKey(LocaleKey.ScreenSelectionMode_Window)]
    Window,

    /// <summary>
    /// Pick a specific element.
    /// </summary>
    [DynamicResourceKey(LocaleKey.ScreenSelectionMode_Element)]
    Element,

    /// <summary>
    /// Free selection mode.
    /// </summary>
    [DynamicResourceKey(LocaleKey.ScreenSelectionMode_Free)]
    Free,

    /// <summary>
    /// Drag a rectangle and harvest every hyperlink element whose bounds
    /// intersect the rect. The (title, URL) batch is written into the
    /// agent-state snapshot via the same channel as a single-element pick.
    /// </summary>
    [DynamicResourceKey(LocaleKey.ScreenSelectionMode_LinkRect)]
    LinkRect
}

/// <summary>
/// Represents data about text selection.
/// Used in IVisualElementContext to notify about text selection changes.
/// </summary>
/// <param name="Text">The selected text, or null if no text is selected.</param>
/// <param name="Element">The visual element from which the text is selected, or null if no element is associated.</param>
public readonly record struct TextSelectionData(
    string? Text,
    IVisualElement? Element
);

/// <summary>
/// Represents a context for visual elements, providing methods to interact with them.
/// </summary>
/// <remarks>
/// This interface extends IObservable to allow observers to subscribe to text selection changes.
/// Warning: Implementers should ensure that related hooks only exist when there are active subscribers
/// to avoid unnecessary resource usage and side effects (e.g. unnecessary clipboard monitoring).
/// </remarks>
public interface IVisualElementContext : IObservable<TextSelectionData>
{
    /// <summary>
    /// Get the currently focused element.
    /// </summary>
    IVisualElement? FocusedElement { get; }

    /// <summary>
    /// Get all screens available in the system.
    /// </summary>
    IEnumerable<IVisualElement> Screens { get; }

    /// <summary>
    /// Get the element at the specified point.
    /// </summary>
    /// <param name="point">Point in screen pixels.</param>
    /// <param name="mode"></param>
    /// <returns></returns>
    IVisualElement? ElementFromPoint(PixelPoint point, ScreenSelectionMode mode = ScreenSelectionMode.Element);

    /// <summary>
    /// Get the element under the mouse pointer.
    /// </summary>
    /// <param name="mode"></param>
    /// <returns></returns>
    IVisualElement? ElementFromPointer(ScreenSelectionMode mode = ScreenSelectionMode.Element);

    /// <summary>
    /// Get the element from a native window handle.
    /// </summary>
    /// <param name="windowHandle"></param>
    /// <returns></returns>
    IVisualElement? ElementFromWindowHandle(nint windowHandle);

    /// <summary>
    /// Let the user pick an element from the screen.
    /// </summary>
    /// <param name="initialMode">
    /// The initial pick mode to use. If null, it remembers the last used mode.
    /// </param>
    /// <returns></returns>
    Task<IVisualElement?> PickVisualElementAsync(ScreenSelectionMode? initialMode);

    /// <summary>
    /// Let the user take a screenshot of a selected area.
    /// </summary>
    /// <param name="initialMode">
    /// The initial pick mode to use. If null, it remembers the last used mode.
    /// </param>
    /// <returns></returns>
    Task<Bitmap?> TakeScreenshotAsync(ScreenSelectionMode? initialMode);

    /// <summary>
    /// Drag a rectangle and harvest every Hyperlink element inside it.
    /// Returns (Canceled, Links). Canceled=true when the user pressed
    /// Esc / right-clicked; callers should NOT activate the agent app
    /// in that case. Empty Links with Canceled=false means a successful
    /// drag that produced no navigable URLs (e.g. only javascript:
    /// anchors) — usually still worth surfacing to the agent.
    /// </summary>
    Task<HarvestResult> HarvestLinksAsync(CancellationToken cancellationToken = default);
}

public readonly record struct HarvestResult(
    bool Canceled,
    IReadOnlyList<HarvestedLink> Links);

/// <summary>
/// One link harvested by <see cref="IVisualElementContext.HarvestLinksAsync"/>.
/// Bounds are in screen pixels — the caller may want to attribute the link
/// to a specific app/window for downstream tracking.
/// </summary>
public readonly record struct HarvestedLink(
    string Title,
    string Url,
    PixelRect Bounds);