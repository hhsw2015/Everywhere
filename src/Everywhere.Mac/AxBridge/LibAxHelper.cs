using System.Runtime.InteropServices;

namespace Everywhere.Mac.AxBridge;

/// <summary>
/// P/Invoke into libAxHelper.dylib (built from src/Everywhere.Mac.AxHelper/).
/// All entry points return either NULL on failure (use ax_last_error to
/// retrieve a message) or a malloc'd UTF-8 C-string the caller MUST
/// free via ax_free. The Swift bridge wraps every body in do/catch so
/// ObjC exceptions never cross the C boundary into the .NET runtime —
/// without that wrapping CoreCLR's PAL_DispatchExceptionWrapper would
/// hang on a raised AX NSException for tens of seconds.
/// </summary>
internal static partial class LibAxHelper
{
    private const string Lib = "AxHelper";

    [LibraryImport(Lib, EntryPoint = "ax_last_error")]
    internal static partial nint LastError();

    [LibraryImport(Lib, EntryPoint = "ax_free")]
    internal static partial void Free(nint ptr);

    [LibraryImport(Lib, EntryPoint = "ax_self_test")]
    internal static partial int SelfTest();

    [LibraryImport(Lib, EntryPoint = "ax_list_apps")]
    internal static partial nint ListApps();

    [LibraryImport(Lib, EntryPoint = "ax_get_app_state", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetAppState(string app, int showFullText);

    [LibraryImport(Lib, EntryPoint = "ax_click", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Click(string app, string? elementIndex, double x, double y, int useXY, int clickCount, string mouseButton);

    [LibraryImport(Lib, EntryPoint = "ax_scroll", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Scroll(string app, string direction, string elementIndex, double pages);

    [LibraryImport(Lib, EntryPoint = "ax_drag", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Drag(string app, double fromX, double fromY, double toX, double toY);

    [LibraryImport(Lib, EntryPoint = "ax_type_text", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint TypeText(string app, string text);

    [LibraryImport(Lib, EntryPoint = "ax_press_key", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint PressKey(string app, string key);

    [LibraryImport(Lib, EntryPoint = "ax_set_value", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint SetValue(string app, string elementIndex, string value);

    /// <summary>
    /// Read a C-string returned by the bridge, then free it. Returns
    /// null when ptr is 0 — caller should consult LastError().
    /// </summary>
    internal static string? ConsumeCString(nint ptr)
    {
        if (ptr == 0) return null;
        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            Free(ptr);
        }
    }

    /// <summary>
    /// Pull the most recent error message set by the Swift bridge.
    /// Returns "" when no error is currently latched.
    /// </summary>
    internal static string LastErrorMessage()
    {
        var ptr = LastError();
        return ConsumeCString(ptr) ?? string.Empty;
    }

    /// <summary>
    /// Probe whether libAxHelper.dylib is loadable in this process.
    /// Returns false on missing dylib, missing a11y permission, or
    /// any other dlopen-time failure. Used by DI to decide whether
    /// to wire OCCU-backed services or fall back to the C# AX path.
    /// </summary>
    internal static bool IsAvailable()
    {
        try
        {
            // SelfTest ends up calling ax_list_apps, which exercises
            // the OCCU AX permission check. If the dylib loads but
            // a11y is denied, this returns 0 — and that's correct,
            // because none of our other functions would work either.
            return SelfTest() == 1;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (Exception) { return false; }
    }
}
