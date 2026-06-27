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
    /// Best-effort hint to the OS accessibility layer that we want the
    /// full a11y tree for this process. On macOS this sets
    /// AXManualAccessibility + AXEnhancedUserInterface on the
    /// application element, which Chromium/Electron and (critically)
    /// SwiftUI use to switch from a simplified surrogate tree to the
    /// real gesture-bindable elements. Without it,
    /// AXUIElementPerformAction(AXPress) on a SwiftUI button can
    /// silently no-op. Mirrors OCCU AccessibilitySnapshot.swift L109 /
    /// L352. Returns true if at least one attribute was accepted;
    /// caller treats failure as harmless.
    /// </summary>
    bool TryEnableBestEffortAccessibility(int processId) => false;

    /// <summary>
    /// Return a "fresh" focused-window IVisualElement sourced via the
    /// platform AX entry-point (1:1 OCCU AccessibilitySnapshot.swift
    /// L108-L130: AXUIElementCreateApplication(pid) → kAXFocusedWindow).
    /// Refs walked from this root accept AXUIElementPerformAction;
    /// refs reverse-looked-up via Avalonia ScreenSelectionSession or
    /// _AXUIElementGetWindow do not on SwiftUI hosts (Calculator 26).
    /// Returns null on platforms where this distinction does not
    /// apply (Windows backend always returns null and the caller
    /// falls back to whatever it had).
    /// </summary>
    IVisualElement? FreshFocusedWindowOf(int processId) => null;

    /// <summary>
    /// Fast app lookup by name fragment without enumerating every
    /// other app's AX tree. macOS impl uses NSWorkspace.runningApps
    /// to find a pid match, then AXUIElementCreateApplication +
    /// AXFocusedWindow / AXMainWindow for the front window. Returns
    /// null if no match — caller falls back to full screen-walk.
    /// </summary>
    IVisualElement? TryFastResolveByName(string name) => null;

    /// <summary>
    /// Fast list of running apps without enumerating every app's AX
    /// children (which is what made list_apps cost 10-30s on machines
    /// with many apps open). macOS impl uses
    /// NSWorkspace.runningApplications + AXUIElementCreateApplication
    /// per app to get only the focused/main window — no walks through
    /// other apps' subtrees. Returns empty when the platform can't
    /// provide a fast lookup; caller falls back to the screen walk.
    /// </summary>
    IReadOnlyList<(IVisualElement Window, int ProcessId)> TryFastListApps() =>
        System.Array.Empty<(IVisualElement, int)>();

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
    /// <summary>
    /// Hit-test that explicitly skips windows owned by our own process
    /// (whiteboard/linkrect masks, picker overlays, badge windows).
    /// Used by the annotation paths so the rect-resolution
    /// ElementFromPoint isn't intercepted by the mask we just drew on
    /// top of the screen — which is what made <c>SystemWide</c>
    /// hit-tests return null on Whiteboard's snap path. Default impl
    /// falls back to the system-wide hit-test for non-Mac platforms.
    /// </summary>
    IVisualElement? ElementAtPointBelowOwnProcess(PixelPoint point) => ElementFromPoint(point);

    Task<HarvestResult> HarvestLinksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="HarvestLinksAsync(CancellationToken)"/> but raises
    /// <paramref name="onRectCommitted"/> the moment the user releases the
    /// drag — so a caller can paint an outline + ➕ overlay immediately
    /// (Pin-style UX) while the link harvest continues in the background.
    /// The Task still completes when harvesting finishes. onRectCommitted
    /// is invoked at most once; not invoked on cancellation.
    /// </summary>
    Task<HarvestResult> HarvestLinksAsync(Action<PixelRect> onRectCommitted, CancellationToken cancellationToken = default)
        => HarvestLinksAsync(cancellationToken);
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
    PixelRect Bounds,
    IVisualElement? Element = null);