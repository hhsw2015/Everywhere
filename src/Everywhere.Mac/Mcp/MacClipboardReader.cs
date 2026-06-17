using System.Runtime.InteropServices;
using Everywhere.Mcp.Input;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS clipboard reader via NSPasteboard (libobjc msgSend). Reads the first
/// NSStringPboardType value from the general pasteboard. Doesn't touch
/// changeCount, doesn't write — purely observational.
/// </summary>
public sealed class MacClipboardReader : IClipboardReader
{
    public string? GetText()
    {
        try
        {
            var pbClass = objc_getClass("NSPasteboard");
            var sel_general = sel_registerName("generalPasteboard");
            var pb = objc_msgSend_get(pbClass, sel_general);
            if (pb == nint.Zero) return null;

            var sel_stringForType = sel_registerName("stringForType:");
            var nsString = objc_getClass("NSString");
            var sel_stringWithUTF8 = sel_registerName("stringWithUTF8String:");
            // NSPasteboardTypeString is the constant @"public.utf8-plain-text" but
            // historically NSStringPboardType also resolves; pass the modern UTI.
            var typeStr = objc_msgSend_str(nsString, sel_stringWithUTF8, "public.utf8-plain-text");
            if (typeStr == nint.Zero) return null;

            var nsResult = objc_msgSend_obj(pb, sel_stringForType, typeStr);
            if (nsResult == nint.Zero) return null;

            var sel_utf8 = sel_registerName("UTF8String");
            var cstr = objc_msgSend_get(nsResult, sel_utf8);
            return cstr == nint.Zero ? null : Marshal.PtrToStringUTF8(cstr);
        }
        catch
        {
            return null;
        }
    }

    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [DllImport(Objc)]
    private static extern nint objc_getClass(string name);

    [DllImport(Objc)]
    private static extern nint sel_registerName(string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_get(nint receiver, nint selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_obj(nint receiver, nint selector, nint arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_str(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);
}
