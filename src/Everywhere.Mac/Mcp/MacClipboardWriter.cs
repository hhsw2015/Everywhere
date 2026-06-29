using System.Runtime.InteropServices;
using Everywhere.Mcp.Input;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS clipboard writer via NSPasteboard. Replaces the general pasteboard
/// contents with one UTF-8 string, after clearing prior content (matching ab's
/// agent_browser_clipboard_write semantics).
/// </summary>
public sealed class MacClipboardWriter : IClipboardWriter
{
    public bool IsAvailable => true;

    public void SetText(string text)
    {
        var pb = General();
        if (pb == nint.Zero) return;
        objc_msgSend_get(pb, sel_registerName("clearContents"));

        var nsString = objc_getClass("NSString");
        var sel_stringWith = sel_registerName("stringWithUTF8String:");
        var nsText = objc_msgSend_str(nsString, sel_stringWith, text ?? string.Empty);
        if (nsText == nint.Zero) return;

        var sel_setStringForType = sel_registerName("setString:forType:");
        var typeStr = objc_msgSend_str(nsString, sel_stringWith, "public.utf8-plain-text");
        objc_msgSend_obj_obj(pb, sel_setStringForType, nsText, typeStr);
    }

    public void Clear()
    {
        var pb = General();
        if (pb == nint.Zero) return;
        objc_msgSend_get(pb, sel_registerName("clearContents"));
    }

    private static nint General()
    {
        var pbClass = objc_getClass("NSPasteboard");
        return objc_msgSend_get(pbClass, sel_registerName("generalPasteboard"));
    }

    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [DllImport(Objc)] private static extern nint objc_getClass(string name);
    [DllImport(Objc)] private static extern nint sel_registerName(string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_get(nint receiver, nint selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_str(nint receiver, nint selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_obj_obj(nint receiver, nint selector, nint a, nint b);
}
