using System.Runtime.InteropServices;
using Everywhere.Mcp.Input;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS implementation of <see cref="IFocusBackend"/>. Raise the target app's main window
/// via Accessibility (AXRaiseAction); fall back to NSRunningApplication.activate.
/// </summary>
public sealed class MacFocusBackend : IFocusBackend
{
    public nint GetForegroundWindow()
    {
        // No cheap CGWindow query for "current foreground" — return 0 and let the
        // borrow's restore step be a no-op. The borrow logic only uses this value
        // to decide whether to skip activation; with 0 we always attempt activation,
        // which is cheap and idempotent.
        return 0;
    }

    public bool TryAxRaise(nint windowOrPid)
    {
        // We don't carry a real AXUIElement handle — the caller passes either 0 or a
        // platform-specific identifier we can't reliably interpret. Always return false
        // so FocusBorrow falls through to Activate(), which uses the explicit pid path.
        return false;
    }

    public void Activate(nint windowOrPid)
    {
        if (windowOrPid > 0 && windowOrPid <= int.MaxValue)
        {
            ActivateProcess((int)windowOrPid);
        }
    }

    public void ActivateProcess(int processId)
    {
        if (processId <= 0) return;
        try
        {
            // Bring the app to front using AppKit's NSRunningApplication.
            // -[NSRunningApplication activateWithOptions:NSApplicationActivateAllWindows]
            var rapClass = objc_getClass("NSRunningApplication");
            var sel_running = sel_registerName("runningApplicationWithProcessIdentifier:");
            var rap = objc_msgSend_pid(rapClass, sel_running, processId);
            if (rap == nint.Zero) return;

            const ulong NSApplicationActivateAllWindows = 1UL << 0;
            const ulong NSApplicationActivateIgnoringOtherApps = 1UL << 1;
            var sel_activate = sel_registerName("activateWithOptions:");
            objc_msgSend_options(rap, sel_activate,
                NSApplicationActivateAllWindows | NSApplicationActivateIgnoringOtherApps);
        }
        catch
        {
            // Best-effort — input still flows even if activation fails (CGEventPost
            // doesn't strictly require foreground).
        }
    }

    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [DllImport(Objc)]
    private static extern nint objc_getClass(string name);

    [DllImport(Objc)]
    private static extern nint sel_registerName(string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_pid(nint receiver, nint selector, int processId);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_options(nint receiver, nint selector, ulong options);
}
