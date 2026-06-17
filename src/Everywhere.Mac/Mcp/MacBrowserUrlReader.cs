using System.Runtime.InteropServices;
using Everywhere.Mcp.Snapshot;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// Reads the focused web area's URL via Accessibility (kAXURLAttribute).
/// Walks the AX tree of the target process, finds the focused element, and
/// looks for AXURL on it or any ancestor up to the AXWebArea.
/// </summary>
public sealed class MacBrowserUrlReader : IBrowserUrlReader
{
    public string? GetUrl(int processId)
    {
        if (processId <= 0) return null;
        try
        {
            var app = AXUIElementCreateApplication(processId);
            if (app == nint.Zero) return null;
            try
            {
                // Read AXFocusedUIElement on the app element.
                var focused = CopyAttribute(app, "AXFocusedUIElement");
                if (focused == nint.Zero)
                {
                    // Fallback: AXMainWindow → AXFocusedUIElement.
                    var mainWin = CopyAttribute(app, "AXMainWindow");
                    if (mainWin == nint.Zero) return null;
                    try { focused = CopyAttribute(mainWin, "AXFocusedUIElement"); }
                    finally { CFRelease(mainWin); }
                    if (focused == nint.Zero) return null;
                }

                try
                {
                    // Walk up looking for AXURL.
                    var cur = focused;
                    var owns = false;
                    for (var i = 0; i < 16 && cur != nint.Zero; i++)
                    {
                        var url = CopyAttributeAsString(cur, "AXURL");
                        if (!string.IsNullOrEmpty(url))
                        {
                            if (owns) CFRelease(cur);
                            return url;
                        }
                        var parent = CopyAttribute(cur, "AXParent");
                        if (owns) CFRelease(cur);
                        cur = parent;
                        owns = true;
                    }
                    if (owns && cur != nint.Zero) CFRelease(cur);
                    return null;
                }
                finally { CFRelease(focused); }
            }
            finally { CFRelease(app); }
        }
        catch
        {
            return null;
        }
    }

    private static nint CopyAttribute(nint element, string attr)
    {
        var cf = CFStringCreateWithCString(nint.Zero, attr, kCFStringEncodingUTF8);
        try
        {
            if (AXUIElementCopyAttributeValue(element, cf, out var value) == 0)
                return value;
            return nint.Zero;
        }
        finally { CFRelease(cf); }
    }

    private static string? CopyAttributeAsString(nint element, string attr)
    {
        var v = CopyAttribute(element, attr);
        if (v == nint.Zero) return null;
        try
        {
            // AXURL returns a CFURLRef; convert to NSString via absoluteString-equivalent.
            var asStr = CFURLGetString(v);
            if (asStr != nint.Zero) return CfStringToManaged(asStr);
            // Otherwise it might already be a CFStringRef.
            return CfStringToManaged(v);
        }
        finally { CFRelease(v); }
    }

    private static string? CfStringToManaged(nint cfString)
    {
        if (cfString == nint.Zero) return null;
        var len = CFStringGetLength(cfString);
        if (len <= 0) return null;
        var maxBytes = checked(len * 4 + 1);
        var buf = Marshal.AllocHGlobal(maxBytes);
        try
        {
            if (!CFStringGetCString(cfString, buf, maxBytes, kCFStringEncodingUTF8))
                return null;
            return Marshal.PtrToStringUTF8(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---- AXAccessibility / CoreFoundation P/Invoke ----

    private const string AppKit = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint kCFStringEncodingUTF8 = 0x08000100;

    [DllImport(AppKit)]
    private static extern nint AXUIElementCreateApplication(int pid);

    [DllImport(AppKit)]
    private static extern int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);

    [DllImport(CF)]
    private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string s, uint encoding);

    [DllImport(CF)]
    private static extern int CFStringGetLength(nint s);

    [DllImport(CF, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(nint s, nint buffer, int bufferSize, uint encoding);

    [DllImport(CF)]
    private static extern void CFRelease(nint cf);

    [DllImport(CF)]
    private static extern nint CFURLGetString(nint anURL);
}
