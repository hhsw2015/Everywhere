using Everywhere.Interop;
using Everywhere.Mcp.Input;
using ModelContextProtocol.Protocol;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// Routes "click this element" to the right semantic action based on the element's type.
/// Calling <see cref="IVisualElement.Invoke"/> on a slider / text field / list item often
/// returns a silent no-op while reporting success — surface that as a typed error instead
/// of fake "ok" so the agent can switch tools.
///
/// When the AX action chain (Press → Confirm → Open → ShowMenu) fails entirely
/// (Invoke throws), we fall back to a coordinate click on the element's centre.
/// Mirrors OCCU ComputerUseService.localClickActionPoints. Caller passes the
/// IInputSimulator + FocusBorrow it already holds so we don't take ownership.
/// </summary>
internal static class ElementClickDispatcher
{
    public static CallToolResult Click(
        IVisualElement element,
        IInputSimulator? input = null,
        FocusBorrow? focusBorrow = null,
        IVisualElementContext? context = null,
        string? appHint = null,
        Everywhere.Mcp.CursorOverlay.ITargetWindowHighlighter? highlighter = null,
        int clickCount = 1,
        MouseButton mouseButton = MouseButton.Left)
    {
        switch (element.Type)
        {
            case VisualElementType.TextEdit:
            case VisualElementType.Document:
                return ToolErrors.Error(
                    $"Cannot click element of type '{element.Type}'. Use set_value to change its text, " +
                    "or use coordinate click(x,y) to put the caret at a specific position.");

            case VisualElementType.Slider:
            case VisualElementType.Spinner:
                return ToolErrors.Error(
                    $"Cannot click element of type '{element.Type}'. Use set_value with a numeric value.");
        }

        // Run OCCU-style click-target redirection: a small trailing "Done"
        // button next to a row is almost never what the agent meant; an
        // Electron-web app row's synthetic-text child can't be Pressed
        // but the row above it can. ClickHeuristics returns the same
        // element when nothing matches.
        string? processName = null;
        try
        {
            if (element.ProcessId > 0)
            {
                processName = System.Diagnostics.Process.GetProcessById(element.ProcessId).ProcessName;
            }
        }
        catch { /* dead process / restricted — skip heuristics */ }
        element = ClickHeuristics.RedirectIfNeeded(element, processName);

        // Visual indicator on the target window (no-op when overlay
        // disabled). Walks up the AX tree to find the containing
        // AXWindow's screen rect so the highlight wraps the whole
        // window the agent is operating, not just the element.
        if (highlighter is not null)
        {
            try
            {
                var top = element;
                for (var hop = 0; hop < 16 && top.Parent is not null; hop++) top = top.Parent;
                highlighter.Highlight(top.BoundingRectangle,
                    string.IsNullOrEmpty(appHint) ? "Everywhere operating" : $"Everywhere · {appHint}");
            }
            catch { /* highlighter is best-effort */ }
        }

        // SwiftUI bypass: macOS 26+ first-party apps (Calculator, etc.)
        // accept AXPress with success status but the SwiftUI gesture
        // handler doesn't fire — only real CGEvent input does. Skip the
        // AX chain entirely and synthesize a coordinate click on those
        // processes.
        // PrefersCoordinateClick whitelist disabled — the AX action-list
        // gating in AXUIElement.Invoke (v0.9.57) handles SwiftUI
        // Calculator generically: AXPress is not in the element's
        // SupportedActions, so TryPerformActionGated returns false for
        // every verb and Invoke() throws cleanly. The catch block then
        // runs the coordinate fallback (HidEventTap, with focus borrow)
        // — same path OCCU's performNonAXClickFallback takes. No need
        // to also short-circuit here, which had the side effect of
        // skipping the AX chain even when AXPress was actually
        // available (slowing down the common case for normal apps and
        // duplicating focus-borrow logic).
        if (false)
        {
            var rectS = element.BoundingRectangle;
            if (rectS.Width > 0 && rectS.Height > 0)
            {
                try
                {
                    var cx = rectS.X + rectS.Width / 2.0;
                    var cy = rectS.Y + rectS.Height / 2.0;
                    AppResolver.ResolvedApp? resolved = AppResolver.Resolve(context!, appHint!);
                    // Indicator already fired up at the AX-walked
                    // top-window rect a few lines above; don't double-
                    // call here (would visually flicker the same border).
                    IDisposable? focusHandle = resolved is not null
                        ? focusBorrow!.Acquire(resolved.Value.Window.NativeWindowHandle, requireFocus: true, processId: resolved.Value.ProcessId)
                        : null;
                    try
                    {
                        // SwiftUI gesture handlers in macOS 26+ Calculator
                        // hook NSGestureRecognizer on the systemwide
                        // event tap; CGEventPostToPid bypasses that tap
                        // and the gesture never fires. Use the GLOBAL
                        // SessionEventTap (targetPid=null) here so the
                        // event reaches the OS hit-test pipeline.
                        // This DOES move the user's real cursor for the
                        // duration of the click — the cost of accuracy
                        // on these specific apps.
                        input!.Click(cx, cy, clickCount, mouseButton, targetPid: null);
                    }
                    finally { focusHandle?.Dispose(); }
                    return new CallToolResult { Content = [new TextContentBlock { Text = "ok (SwiftUI bypass, coordinate click)" }] };
                }
                catch (Exception coordEx)
                {
                    // Fall through to AX path if coordinate failed
                    System.Diagnostics.Debug.WriteLine($"PrefersCoord click failed: {coordEx.Message}");
                }
            }
        }

        try
        {
            // 1:1 OCCU performPreferredClick (ComputerUseService.swift
            // L699-732):
            //   .left   → selectContainingListItem / AXPress / AXConfirm / AXOpen
            //   .right  → AXShowMenu only
            //   .middle → break (no AX verb attempted; falls to coord)
            //
            // Each verb fires only when the element advertises it; on
            // miss we throw and the catch below runs the coord fallback,
            // mirroring OCCU's performNonAXClickFallback path.
            if (mouseButton == MouseButton.Right)
            {
                if (element.TryInvokeAction("showmenu"))
                {
                    return new CallToolResult { Content = [new TextContentBlock { Text = "ok (right-click → ShowMenu)" }] };
                }
                throw new InvalidOperationException("right-click requires AXShowMenu, which the element does not advertise");
            }
            if (mouseButton == MouseButton.Middle)
            {
                // OCCU L728-729: case .middle: break — no AX action
                // attempted, drop straight to coord fallback.
                throw new InvalidOperationException("middle-click has no AX action; deferring to coordinate fallback");
            }
            element.Invoke(clickCount);
            return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        }
        catch (Exception axEx)
        {
            // OCCU-style coordinate fallback: if every AX verb in the chain
            // failed we still know the element's frame, so synthesize a real
            // click at its centre. Only safe when caller gave us the input
            // primitives — the MCP HTTP host always does, internal callers
            // (refactor seams) may not, in which case we surface the AX
            // failure verbatim.
            if (input is not null)
            {
                var rect = element.BoundingRectangle;
                if (rect.Width > 0 && rect.Height > 0)
                {
                    // Resolve up-front so the keyboard fallback in the
                    // catch block can read the same ProcessId for
                    // PressKey targeting. Hoisting outside the try also
                    // sidesteps the previous CS0103 'resolved not in
                    // scope' error.
                    AppResolver.ResolvedApp? resolved = null;
                    if (focusBorrow is not null && context is not null && !string.IsNullOrEmpty(appHint))
                    {
                        try { resolved = AppResolver.Resolve(context, appHint); }
                        catch { /* AppResolver may throw on dead pid */ }
                    }
                    try
                    {
                        var cx = rect.X + rect.Width / 2.0;
                        var cy = rect.Y + rect.Height / 2.0;
                        // Best-effort focus: AX invoke didn't need the
                        // target foreground, but a real CGEvent click
                        // does — otherwise it lands on whichever app
                        // currently has focus.
                        IDisposable? focusHandle = resolved is not null
                            ? focusBorrow!.Acquire(
                                resolved.Value.Window.NativeWindowHandle,
                                requireFocus: true,
                                processId: resolved.Value.ProcessId)
                            : null;
                        try
                        {
                            // 1:1 OCCU performNonAXClickFallback
                            // (ComputerUseService.swift L1647-1684).
                            //
                            // OCCU default = targeted PostToPid (L1668-1672):
                            // no cursor warp, no focus steal — events go
                            // straight to the app's run loop. Works for
                            // AppKit; SwiftUI / Electron with
                            // NSGestureRecognizer on the global tap may
                            // silently ignore them.
                            //
                            // OCCU global path (L1656-1664) runs
                            // prepareAppForGlobalPointerInput + clickGlobally
                            // and ONLY fires when env
                            // OPEN_COMPUTER_USE_ALLOW_GLOBAL_POINTER_FALLBACKS=1.
                            // That mode steals foreground focus and races
                            // physical cursor. Opt-in is the right default
                            // because the cure (focus theft) is worse than
                            // the disease for most apps.
                            //
                            // We mirror the env flag name with EVERYWHERE_*
                            // prefix.
                            var allowGlobal = Environment.GetEnvironmentVariable(
                                "EVERYWHERE_ALLOW_GLOBAL_POINTER_FALLBACKS") == "1";
                            if (Environment.GetEnvironmentVariable("EVERYWHERE_DEBUG_INPUT_FALLBACKS") == "1")
                            {
                                // OCCU debugInputFallback (CUS L1566-1575):
                                // single stderr line per coord fallback.
                                Console.Error.WriteLine(
                                    $"[everywhere] {(allowGlobal ? "global" : "targeted")} pointer fallback tool=click app={appHint ?? "?"} target=({cx},{cy})");
                            }
                            input.Click(cx, cy, clickCount, mouseButton,
                                targetPid: allowGlobal ? null : resolved?.ProcessId);
                        }
                        finally
                        {
                            focusHandle?.Dispose();
                        }
                        return new CallToolResult
                        {
                            Content = [new TextContentBlock
                            {
                                Text = $"ok (AX action chain failed: {axEx.Message}; coordinate fallback at {cx:0},{cy:0} succeeded)",
                            }],
                        };
                    }
                    catch (Exception coordEx)
                    {
                        // 1:1 OCCU: when both AX and coord fallback fail,
                        // OCCU throws (CUS L1675-1679). No keyboard
                        // fallback layer on top — that was our
                        // extension; drop it.
                        return ToolErrors.Error(
                            $"click could not be handled through accessibility ({axEx.Message}) and coordinate fallback failed ({coordEx.Message}). Set EVERYWHERE_ALLOW_GLOBAL_POINTER_FALLBACKS=1 to allow physical-pointer fallback.");
                    }
                }
            }
            return ToolErrors.FromException(axEx, "invoke element");
        }
    }
}
