using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Everywhere.Interop;
using ShadUI.Extensions;
using ZLinq;

namespace Everywhere.Mac.Interop;

public partial class VisualElementContext(IWindowHelper windowHelper) : IVisualElementContext
{
    public IVisualElement? FocusedElement => AXUIElement.SystemWide.ElementByAttributeValue(AXAttributeConstants.FocusedUIElement);

    public IEnumerable<IVisualElement> Screens => NSScreen.Screens.Select(screen => new NSScreenVisualElement(screen));

    IVisualElement? IVisualElementContext.ElementFromPoint(PixelPoint point, ScreenSelectionMode mode) => ElementFromPoint(point, mode);

    private static IVisualElement? ElementFromPoint(PixelPoint point, ScreenSelectionMode mode = ScreenSelectionMode.Element)
    {
        switch (mode)
        {
            case ScreenSelectionMode.Element:
            {
                return AXUIElement.SystemWide.ElementAtPosition(point.X, point.Y);
            }
            case ScreenSelectionMode.Window:
            {
                // Traverse up to find the containing window element
                IVisualElement? current = AXUIElement.SystemWide.ElementAtPosition(point.X, point.Y);
                while (current is AXUIElement axui && axui.Role != AXRoleAttribute.AXWindow)
                {
                    current = current.Parent;
                }
                return current;
            }
            case ScreenSelectionMode.Screen:
            {
                var screen = NSScreen.Screens.FirstOrDefault(s => s.Frame.Contains(new CGPoint(point.X, point.Y)));
                return screen is null ? null : new NSScreenVisualElement(screen);
            }
            default:
            {
                return null;
            }
        }
    }

    public IVisualElement? ElementFromPointer(ScreenSelectionMode mode = ScreenSelectionMode.Element)
    {
        var point = Dispatcher.UIThread.Invoke<PixelPoint?>(() =>
        {
            // NSEvent.CurrentMouseLocation gives coordinates with the origin at the bottom-left of the primary screen.
            var mouseLocation = NSEvent.CurrentMouseLocation;

            // We need to find which screen the mouse is on to correctly convert coordinates.
            var screen = NSScreen.Screens.AsValueEnumerable().FirstOrDefault(s => s.Frame.Contains(mouseLocation)) ?? NSScreen.MainScreen;
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (screen is null) return null;

            // Convert to a top-left origin coordinate system.
            var y = screen.Frame.Height - (mouseLocation.Y - screen.Frame.Y);
            var x = mouseLocation.X - screen.Frame.X;
            return new PixelPoint((int)x, (int)y);
        });

        return point is null ? null : ElementFromPoint(point.Value, mode);
    }

    public IVisualElement? ElementFromWindowHandle(IntPtr windowHandle)
    {
        return AXUIElement.ElementFromWindowId((uint)windowHandle);
    }

    public IVisualElement? FreshFocusedWindowOf(int processId)
    {
        return AXUIElement.FreshFocusedWindowOf(processId);
    }

    public IVisualElement? TryFastResolveByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var apps = NSWorkspace.SharedWorkspace.RunningApplications;
        foreach (var app in apps)
        {
            if (app.ActivationPolicy == NSApplicationActivationPolicy.Prohibited) continue;
            var localized = app.LocalizedName;
            var bundle = app.BundleIdentifier;
            if ((localized != null && localized.Contains(name, StringComparison.OrdinalIgnoreCase)) ||
                (bundle != null && bundle.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                return AXUIElement.FreshFocusedWindowOf(app.ProcessIdentifier);
            }
        }
        return null;
    }

    public IReadOnlyList<(IVisualElement Window, int ProcessId)> TryFastListApps()
    {
        var apps = NSWorkspace.SharedWorkspace.RunningApplications;
        var result = new List<(IVisualElement, int)>(apps.Length);
        foreach (var app in apps)
        {
            if (app.ActivationPolicy == NSApplicationActivationPolicy.Prohibited) continue;
            var pid = app.ProcessIdentifier;
            if (pid <= 0) continue;
            var win = AXUIElement.FreshFocusedWindowOf(pid);
            if (win is null) continue;
            result.Add((win, pid));
        }
        return result;
    }

    public bool TryEnableBestEffortAccessibility(int processId)
    {
        // 1:1 OCCU AccessibilitySnapshot.swift:352-358. SwiftUI buttons
        // (Calculator 26 was the smoking gun) advertise AXPress but
        // AXUIElementPerformAction silently no-ops on the cached
        // element ref until the app's a11y subsystem is upgraded to
        // "full" mode via these two private attributes. Set on the
        // application element, then re-walk the tree.
        if (processId <= 0) return false;
        // 1:1 OCCU AccessibilitySnapshot.swift:352-358. Must use the
        // CoreFoundation kCFBooleanTrue singleton — AXUIElementSet-
        // AttributeValue rejects NSNumber(true) on these private
        // attributes (silent .failure). v0.9.91 diagnostic confirmed:
        // both calls returned false in production with NSNumber.
        var manualOk = AXUIElement.SetAppBoolAttribute(processId, "AXManualAccessibility", true);
        var enhancedOk = AXUIElement.SetAppBoolAttribute(processId, "AXEnhancedUserInterface", true);
        return manualOk || enhancedOk;
    }

    public Task<IVisualElement?> PickVisualElementAsync(ScreenSelectionMode? initialMode)
    {
        return PickerSession.PickAsync(windowHelper, initialMode);
    }

    public Task<Bitmap?> TakeScreenshotAsync(ScreenSelectionMode? initialMode)
    {
        return ScreenshotSession.TakeAsync(windowHelper, initialMode);
    }

    public Task<HarvestResult> HarvestLinksAsync(CancellationToken cancellationToken = default)
    {
        return LinkRectSession.HarvestAsync(windowHelper, onRectCommitted: null, cancellationToken);
    }

    public Task<HarvestResult> HarvestLinksAsync(Action<PixelRect> onRectCommitted, CancellationToken cancellationToken = default)
    {
        return LinkRectSession.HarvestAsync(windowHelper, onRectCommitted, cancellationToken);
    }
}