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
            // AX success path bypasses IInputSimulator entirely, so the
            // cursor-overlay never sees a Move/Click trace event and
            // the user gets no visual feedback. Stage the visual move
            // BEFORE invoking AX (otherwise the target reacts before
            // the soft cursor has visibly arrived — looks like the
            // cursor lags behind the click), then pulse AFTER.
            Everywhere.Mcp.Input.TracedInputSimulator? tracedSim =
                input as Everywhere.Mcp.Input.TracedInputSimulator;
            double tx = 0, ty = 0;
            bool haveCenter = false;
            if (tracedSim is not null)
            {
                var rect = element.BoundingRectangle;
                if (rect.Width > 0 && rect.Height > 0)
                {
                    tx = rect.X + rect.Width / 2.0;
                    ty = rect.Y + rect.Height / 2.0;
                    haveCenter = true;
                    // 1:1 OCCU moveVisualCursor (DispatchQueue.main.sync):
                    // wait until the spring has actually converged on the
                    // target before AX changes button state. The trace's
                    // MoveAndAwait hook returns a Task that completes
                    // when AnimateMove's tick loop hits close-enough. If
                    // no overlay is wired (headless / cursor disabled),
                    // the hook is null and we just fire-and-forget via
                    // Publish + skip the wait.
                    if (tracedSim.Trace.MoveAndAwait is { } awaiter)
                    {
                        try { awaiter(new Avalonia.Point(tx, ty)).GetAwaiter().GetResult(); }
                        catch { /* overlay best-effort */ }
                    }
                    else
                    {
                        tracedSim.Trace.Publish(new Everywhere.Mcp.Input.CursorTraceEvent(
                            Everywhere.Mcp.Input.CursorTraceKind.Move, tx, ty));
                    }
                }
            }
            element.Invoke(clickCount);
            if (tracedSim is not null && haveCenter)
            {
                tracedSim.Trace.Publish(new Everywhere.Mcp.Input.CursorTraceEvent(
                    Everywhere.Mcp.Input.CursorTraceKind.Click,
                    tx, ty, ClickCount: clickCount, Button: mouseButton));
            }
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
                    // 1:1 OCCU performNonAXClickFallback
                    // (ComputerUseService.swift L1647-1682):
                    //   if global-pointer-fallbacks env=1:
                    //       prepareAppForGlobalPointerInput(app)
                    //       clickGlobally(point, button, clickCount)
                    //   else:
                    //       clickTargeted(point, button, clickCount, pid)
                    //
                    // Targeted is the default. NO FocusBorrow, NO AXRaise,
                    // NO activate, NO restore — OCCU never touches focus
                    // on the targeted path. CGEventPostToPid delivers
                    // straight to the app's run loop regardless of which
                    // app is currently frontmost. We mirror exactly.
                    var allowGlobal = Environment.GetEnvironmentVariable(
                        "EVERYWHERE_ALLOW_GLOBAL_POINTER_FALLBACKS") == "1";
                    var cx = rect.X + rect.Width / 2.0;
                    var cy = rect.Y + rect.Height / 2.0;

                    AppResolver.ResolvedApp? resolved = null;
                    if (context is not null && !string.IsNullOrEmpty(appHint))
                    {
                        try { resolved = AppResolver.Resolve(context, appHint); }
                        catch { /* AppResolver may throw on dead pid */ }
                    }

                    if (Environment.GetEnvironmentVariable("EVERYWHERE_DEBUG_INPUT_FALLBACKS") == "1")
                    {
                        Console.Error.WriteLine(
                            $"[everywhere] {(allowGlobal ? "global" : "targeted")} pointer fallback tool=click app={appHint ?? "?"} target=({cx},{cy})");
                    }

                    try
                    {
                        if (allowGlobal && focusBorrow is not null && resolved is not null)
                        {
                            // 1:1 OCCU L1662-1663: prepareAppForGlobalPointerInput
                            // then clickGlobally (HidEventTap).
                            using var _ = focusBorrow.Acquire(
                                resolved.Value.Window.NativeWindowHandle,
                                requireFocus: true,
                                processId: resolved.Value.ProcessId);
                            input.Click(cx, cy, clickCount, mouseButton, targetPid: null);
                        }
                        else
                        {
                            // 1:1 OCCU L1668-1672: clickTargeted via postToPid.
                            // No prep. targetPid required — when AppResolver
                            // can't supply one we surface the AX failure
                            // verbatim rather than guessing a frontmost.
                            if (resolved is null)
                            {
                                return ToolErrors.FromException(axEx, "invoke element");
                            }
                            input.Click(cx, cy, clickCount, mouseButton,
                                targetPid: resolved.Value.ProcessId);
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
                        // 1:1 OCCU L1675-1680: both failed.
                        return ToolErrors.Error(
                            $"click could not be handled through accessibility ({axEx.Message}) and coordinate fallback failed ({coordEx.Message}). Set EVERYWHERE_ALLOW_GLOBAL_POINTER_FALLBACKS=1 to allow physical-pointer fallback.");
                    }
                }
            }
            return ToolErrors.FromException(axEx, "invoke element");
        }
    }
}
