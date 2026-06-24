using System.Runtime.InteropServices;
using Everywhere.Mcp.Input;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS app activator. Resolves the supplied identifier as a bundle id first
/// (NSRunningApplication.runningApplicationsWithBundleIdentifier:), and falls
/// back to a localizedName / executable-name search across running apps.
/// Activation goes through NSRunningApplication.activateWithOptions: with
/// ActivateAllWindows | ActivateIgnoringOtherApps so the target's frontmost
/// window comes forward even if the user was just in another space.
/// </summary>
public sealed class MacAppActivator : IAppActivator
{
    private const ulong ActivateAllWindows = 1;
    private const ulong ActivateIgnoringOtherApps = 2;

    private readonly ILogger<MacAppActivator>? _logger;

    public MacAppActivator() : this(null) { }

    public MacAppActivator(ILogger<MacAppActivator>? logger)
    {
        _logger = logger;

        // Prime NSWorkspace eagerly. As an LSUIElement service process,
        // Everywhere doesn't always have AppKit fully spun up by the time
        // the first SnapshotContext press lands; the very first
        // -[NSWorkspace runningApplications] call has been observed to
        // return an empty / partial list, which is why activation only
        // started working after the user did one AgentPickElement (the
        // picker wakes AppKit). Calling sharedWorkspace + runningApplications
        // once at DI construction makes the first Snapshot hot. We catch
        // narrowly and log so a regression here doesn't silently bring back
        // the original "first activation does nothing" symptom.
        try
        {
            var ws = objc_msgSend_get(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
            if (ws != 0)
            {
                _ = objc_msgSend_get(ws, sel_registerName("runningApplications"));
                _ = objc_msgSend_get(ws, sel_registerName("frontmostApplication"));
            }
            else
            {
                _logger?.LogWarning("MacAppActivator priming: NSWorkspace.sharedWorkspace returned 0; first activation may be cold.");
            }
        }
        catch (DllNotFoundException ex) { _logger?.LogWarning(ex, "MacAppActivator priming: libobjc missing."); }
        catch (EntryPointNotFoundException ex) { _logger?.LogWarning(ex, "MacAppActivator priming: P/Invoke entry missing."); }
        catch (Exception ex) { _logger?.LogWarning(ex, "MacAppActivator priming failed; first activation may be cold."); }
    }

    public bool Activate(string appIdentifier)
    {
        if (string.IsNullOrWhiteSpace(appIdentifier)) return false;

        var workspaceClass = objc_getClass("NSWorkspace");
        var sharedSel = sel_registerName("sharedWorkspace");
        var workspace = objc_msgSend_get(workspaceClass, sharedSel);
        if (workspace == 0) return false;

        var bundleSel = sel_registerName("bundleIdentifier");
        var nameSel = sel_registerName("localizedName");
        var execSel = sel_registerName("executableURL");
        var lastPathSel = sel_registerName("lastPathComponent");
        var pathSel = sel_registerName("path");
        var utf8Sel = sel_registerName("UTF8String");
        var activateSel = sel_registerName("activateWithOptions:");

        // If the target app is already frontmost, do nothing — switching to it
        // would cause a noisy focus blink and (worse) cancel the chat window
        // selection the user just made. Returns true (successful no-op) so
        // callers don't log this as "activation failed".
        var frontmostSel = sel_registerName("frontmostApplication");
        var frontmost = objc_msgSend_get(workspace, frontmostSel);
        if (frontmost != 0
            && (Matches(frontmost, bundleSel, utf8Sel, appIdentifier)
                || Matches(frontmost, nameSel, utf8Sel, appIdentifier)
                || MatchesExecutable(frontmost, execSel, lastPathSel, pathSel, utf8Sel, appIdentifier)))
        {
            return true;
        }

        var runningSel = sel_registerName("runningApplications");
        var apps = objc_msgSend_get(workspace, runningSel);
        if (apps == 0) return false;

        var countSel = sel_registerName("count");
        var count = (long)objc_msgSend_get(apps, countSel);
        if (count <= 0) return false;

        var objAtSel = sel_registerName("objectAtIndex:");

        for (long i = 0; i < count; i++)
        {
            var app = objc_msgSend_idx(apps, objAtSel, (nuint)i);
            if (app == 0) continue;

            if (Matches(app, bundleSel, utf8Sel, appIdentifier)
                || Matches(app, nameSel, utf8Sel, appIdentifier)
                || MatchesExecutable(app, execSel, lastPathSel, pathSel, utf8Sel, appIdentifier))
            {
                objc_msgSend_activate(app, activateSel, ActivateAllWindows | ActivateIgnoringOtherApps);
                return true;
            }
        }

        return false;
    }

    public bool IsFrontmost(string appIdentifier)
    {
        if (string.IsNullOrWhiteSpace(appIdentifier)) return false;
        try
        {
            var ws = objc_msgSend_get(objc_getClass("NSWorkspace"),
                                       sel_registerName("sharedWorkspace"));
            if (ws == 0) return false;
            var frontmost = objc_msgSend_get(ws, sel_registerName("frontmostApplication"));
            if (frontmost == 0) return false;
            var bundleSel = sel_registerName("bundleIdentifier");
            var nameSel = sel_registerName("localizedName");
            var execSel = sel_registerName("executableURL");
            var lastPathSel = sel_registerName("lastPathComponent");
            var pathSel = sel_registerName("path");
            var utf8Sel = sel_registerName("UTF8String");
            return Matches(frontmost, bundleSel, utf8Sel, appIdentifier)
                || Matches(frontmost, nameSel, utf8Sel, appIdentifier)
                || MatchesExecutable(frontmost, execSel, lastPathSel, pathSel, utf8Sel, appIdentifier);
        }
        catch
        {
            return false;
        }
    }

    private static bool Matches(nint app, nint propSel, nint utf8Sel, string needle)
    {
        var ns = objc_msgSend_get(app, propSel);
        if (ns == 0) return false;
        var ptr = objc_msgSend_get(ns, utf8Sel);
        if (ptr == 0) return false;
        var actual = Marshal.PtrToStringUTF8(ptr);
        if (string.IsNullOrEmpty(actual)) return false;
        // Exact match only. A substring fallback is dangerous: AgentAppId="chat"
        // would match "WeChat", "Whatsapp" (executable name), etc. Bundle ids
        // (com.foo.bar) and executable basenames already round-trip cleanly.
        return string.Equals(actual, needle, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesExecutable(nint app, nint execSel, nint lastPathSel, nint pathSel, nint utf8Sel, string needle)
    {
        var url = objc_msgSend_get(app, execSel);
        if (url == 0) return false;
        var lastPath = objc_msgSend_get(url, lastPathSel);
        if (lastPath == 0) return false;
        var ptr = objc_msgSend_get(lastPath, utf8Sel);
        if (ptr == 0) return false;
        var actual = Marshal.PtrToStringUTF8(ptr);
        if (string.IsNullOrEmpty(actual)) return false;
        return string.Equals(actual, needle, StringComparison.OrdinalIgnoreCase);
    }

    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [DllImport(Objc)] private static extern nint objc_getClass(string name);
    [DllImport(Objc)] private static extern nint sel_registerName(string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_get(nint receiver, nint selector);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_idx(nint receiver, nint selector, nuint index);
    // -[NSRunningApplication activateWithOptions:] returns Objective-C BOOL
    // (signed char, 1 byte). .NET's default `bool` marshalling treats the
    // return as a 4-byte Win32 BOOL, leaving the upper bytes undefined on
    // arm64 and producing non-deterministic values. We don't need the
    // return value (success is observed via the next focus event), so
    // declare void to mirror MacFocusBackend.ActivateProcess.
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_activate(nint receiver, nint selector, ulong options);
}
