using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Everywhere.Interop;

public enum VisualElementType
{
    Unknown,
    Label,
    TextEdit,
    Document,
    Button,
    Hyperlink,
    Image,
    CheckBox,
    RadioButton,
    ComboBox,
    ListView,
    ListViewItem,
    TreeView,
    TreeViewItem,
    DataGrid,
    DataGridItem,
    TabControl,
    TabItem,
    Table,
    TableRow,
    Menu,
    MenuItem,
    Slider,
    ScrollBar,
    ProgressBar,
    Spinner,

    ToolBar,
    StatusBar,

    Header,
    HeaderItem,

    Splitter,

    /// <summary>
    /// The most generic container element, its parent and children can be any type.
    /// </summary>
    Panel,

    /// <summary>
    /// The toplevel of a window, it's parent must be Screen or null
    /// </summary>
    TopLevel,

    /// <summary>
    /// A screen that contains toplevel, its parent is always null and children are toplevel.
    /// </summary>
    Screen
}

[Flags]
public enum VisualElementStates
{
    None = 0,
    Offscreen = 1 << 0,
    Disabled = 1 << 1,
    Focused = 1 << 2,
    Selected = 1 << 3,
    ReadOnly = 1 << 4,
    Password = 1 << 5,
    // OCCU summarizeTraits parity (AccessibilitySnapshot.swift): the
    // aggregator picks any of these AX flags up so the snapshot can
    // expose 'expanded', 'required', 'edited', main-window, minimized,
    // grabbed (drag-source), pressed (button held), checked (toggle).
    Expanded  = 1 << 6,
    Required  = 1 << 7,
    Edited    = 1 << 8,
    Main      = 1 << 9,
    Minimized = 1 << 10,
    Grabbed   = 1 << 11,
    // Pressed bit removed — no platform impl populated it (would have
    // required reading AXSelected/AXPressed which AppKit / SwiftUI
    // expose inconsistently). Re-add when there's a real source.
    Checked   = 1 << 13,
}

/// <summary>
/// Accessor to navigate siblings of a visual element.
/// It manages enumeration of sibling elements in both forward and backward directions,
/// and Dispose all resources when enumerator is disposed.
/// </summary>
public abstract class VisualElementSiblingAccessor : IDisposable
{
    private int _activeEnumerators;
    private bool _disposed;

    /// <summary>
    /// Gets a forward enumerator for iterating next sibling elements.
    /// </summary>
    public IEnumerator<IVisualElement> ForwardEnumerator
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, GetType());

            EnsureResources();
            Interlocked.Increment(ref _activeEnumerators);
            return new ManagedEnumerator(this, CreateForwardEnumerator());
        }
    }

    /// <summary>
    /// Gets a backward enumerator for iterating previous sibling elements.
    /// </summary>
    public IEnumerator<IVisualElement> BackwardEnumerator
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, GetType());

            EnsureResources();
            Interlocked.Increment(ref _activeEnumerators);
            return new ManagedEnumerator(this, CreateBackwardEnumerator());
        }
    }

    /// <summary>
    /// Creates an enumerator for iterating over sibling elements in the forward direction.
    /// </summary>
    /// <returns></returns>
    protected abstract IEnumerator<IVisualElement> CreateForwardEnumerator();

    /// <summary>
    /// Creates an enumerator for iterating over sibling elements in the backward direction.
    /// </summary>
    /// <returns></returns>
    protected abstract IEnumerator<IVisualElement> CreateBackwardEnumerator();

    /// <summary>
    /// Ensures that necessary resources are allocated for sibling enumeration.
    /// </summary>
    protected virtual void EnsureResources() { }

    /// <summary>
    /// Releases any allocated resources used for sibling enumeration.
    /// </summary>
    protected virtual void ReleaseResources() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseResources();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Called when an enumerator is disposed to manage resource cleanup.
    /// </summary>
    private void EnumeratorDisposed()
    {
        if (Interlocked.Decrement(ref _activeEnumerators) == 0 && _disposed)
        {
            ReleaseResources();
        }
    }

    /// <summary>
    /// Wrapper class to manage enumerator disposal and notify the owner.
    /// </summary>
    /// <param name="owner"></param>
    /// <param name="innerEnumerator"></param>
    private class ManagedEnumerator(VisualElementSiblingAccessor owner, IEnumerator<IVisualElement> innerEnumerator) : IEnumerator<IVisualElement>
    {
        public IVisualElement Current => innerEnumerator.Current;

        object IEnumerator.Current => Current;

        public bool MoveNext() => innerEnumerator.MoveNext();

        public void Reset() => innerEnumerator.Reset();

        public void Dispose()
        {
            owner.EnumeratorDisposed();
            innerEnumerator.Dispose();
        }
    }
}

public interface IVisualElement
{
    /// <summary>
    /// Unique identifier in one Visual Tree.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the visual parent, returns null if not found
    /// </summary>
    IVisualElement? Parent { get; }

    /// <summary>
    /// Gets an accessor that can enumerate siblings
    /// If this supports direct sibling accessing (e.g. Windows use a linked-array), enumerate it directly.
    /// If this doesn't support (e.g. macOS and Linux), get parent and cache children here
    /// </summary>
    VisualElementSiblingAccessor SiblingAccessor { get; }

    /// <summary>
    /// Gets the visual children, return empty if empty
    /// </summary>
    IEnumerable<IVisualElement> Children { get; }

    VisualElementType Type { get; }

    VisualElementStates States { get; }

    string? Name { get; }

    /// <summary>
    /// For elements of <see cref="VisualElementType.Hyperlink"/>: the
    /// underlying href / URL exposed via accessibility (AXURL on macOS,
    /// ValuePattern on Windows). Null for non-link elements or when the
    /// platform doesn't expose it.
    /// </summary>
    string? Url => null;

    /// <summary>
    /// Relative to the screen pixels, regardless of the parent element.
    /// </summary>
    PixelRect BoundingRectangle { get; }

    /// <summary>
    /// Gets the process ID of the application that owns this visual element.
    /// If the element does not belong to any process, return -1.
    /// </summary>
    int ProcessId { get; }

    /// <summary>
    /// Gets the native window handle (HWND of the RootWindow on Windows, WindowNumber on macOS, XID on Linux) of the visual element.
    /// If element is a Screen, return -1.
    /// </summary>
    nint NativeWindowHandle { get; }

    /// <summary>
    /// get text content of the visual element.
    /// </summary>
    /// <param name="maxLength">allowed max length of the text, -1 means no limit.</param>
    /// <returns></returns>
    /// <remarks>
    /// set maxLength to 1 can check if the text is null or empty, with minimal performance impact.
    /// </remarks>
    string? GetText(int maxLength = -1);

    /// <summary>
    /// OCCU meaningfulActions parity. Names of AX actions the element
    /// supports, filtered to verbs that change UI state (Press / Confirm
    /// / Open / ShowMenu / Increment / Decrement / Cancel / Pick /
    /// Delete / Raise). Empty when the element has no actions or only
    /// internal AX bookkeeping actions. Default implementation returns
    /// empty so non-Mac platforms compile without changes; Mac path
    /// queries AXUIElementCopyActionNames.
    ///
    /// EXPENSIVE — the Mac implementation performs a cross-process AX
    /// RPC and allocates an NSArray on every read. Callers that touch
    /// many elements per snapshot (renderers, JSON builders) should
    /// read once per element and cache locally.
    /// </summary>
    IReadOnlyList<string> SupportedActions => Array.Empty<string>();

    /// <summary>
    /// Get the selected text of the visual element.
    /// </summary>
    /// <returns></returns>
    string? GetSelectionText();

    /// <summary>
    /// Invokes the default action on the visual element using UI Automation patterns.
    /// </summary>
    void Invoke();

    /// <summary>
    /// OCCU performPreferredClick switches on mouse button: right-click
    /// invokes only AXShowMenu, never Press/Confirm/Open. Callers that
    /// know they want a specific verb (right-click → show_menu, slider
    /// → increment) can call this instead of Invoke. Default impl
    /// returns false (non-Mac platforms don't expose AX actions).
    /// </summary>
    bool TryInvokeAction(string verb) => false;

    /// <summary>
    /// OCCU formattedPlaceholderSegment (AX L1243). The placeholder
    /// text on text fields / search boxes / combo boxes (e.g. "搜索"
    /// or "Email"). Mac AX exposes this on AXPlaceholderValue;
    /// non-text controls return null. Default impl returns null.
    /// </summary>
    string? Placeholder => null;

    /// <summary>
    /// OCCU formattedLabelSegment (AX L1212). The element's accessible
    /// description distinct from its title — 'Description: ...' segment
    /// in the rendered tree. Mac AX = AXDescription; default null.
    /// </summary>
    string? AccessibleDescription => null;

    /// <summary>
    /// OCCU-parity overload: repeats the AX action chain
    /// <paramref name="clickCount"/> times (single/double/triple click).
    /// Default-implemented as a fall-through to <see cref="Invoke"/>
    /// looped manually so non-Mac implementations don't have to know
    /// about the AX action retry timing.
    /// </summary>
    void Invoke(int clickCount)
    {
        for (var i = 0; i < Math.Max(clickCount, 1); i++)
        {
            Invoke();
            if (i < clickCount - 1) System.Threading.Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Sets the textual content of the visual element using UI Automation patterns.
    /// </summary>
    void SetText(string text);

    /// <summary>
    /// Sends virtual key input to the visual element using UI Automation patterns.
    /// Supports common keys and shortcuts like Enter, Ctrl+C, or Ctrl+V even when the window is minimized.
    /// </summary>
    void SendShortcut(KeyboardShortcut shortcut);

    /// <summary>
    /// Captures the visual element into a bitmap and returns a pointer to the bitmap data along with metadata.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<ICapturedBitmapData> CaptureAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets a pointer to the bitmap data of the visual element along with metadata.
    /// </summary>
    interface ICapturedBitmapData : IDisposable
    {
        PixelFormat Format { get; }

        AlphaFormat AlphaFormat { get; }

        nint Data { get; }

        // TODO: actual bounds to solve ghoat window issues

        /// <summary>
        /// The actual size of the captured bitmap data. May not equal to CaptureRect.Size due to scaling.
        /// </summary>
        PixelSize Size { get; }

        Vector Dpi { get; }

        int Stride { get; }
    }
}

public static class VisualElementExtension
{
    extension(IVisualElement element)
    {
        public IEnumerable<IVisualElement> GetDescendants(bool includeSelf = false)
        {
            if (includeSelf)
            {
                yield return element;
            }

            foreach (var child in element.Children)
            {
                yield return child;
                foreach (var descendant in child.GetDescendants())
                {
                    yield return descendant;
                }
            }
        }

        public IEnumerable<IVisualElement> GetAncestors(bool includeSelf = false)
        {
            var current = includeSelf ? element : element.Parent;
            while (current != null)
            {
                yield return current;
                current = current.Parent;
            }
        }
    }

    extension(IVisualElement.ICapturedBitmapData data)
    {
        /// <summary>
        /// Converts the captured bitmap data into an Avalonia Bitmap object.
        /// </summary>
        /// <returns>Converted Bitmap if successful, or null if the pixel data is empty.</returns>
        public Bitmap? ToAvaloniaBitmap()
        {
            var pixelSize = data.Size;
            return pixelSize.Width <= 0 || pixelSize.Height <= 0 ?
                null :
                new Bitmap(
                    data.Format,
                    data.AlphaFormat,
                    data.Data,
                    pixelSize,
                    data.Dpi,
                    data.Stride);
        }

        /// <summary>
        /// Converts the captured bitmap data into a SkiaSharp SKImage object.
        /// </summary>
        /// <returns>Converted SKImage if successful, or null if the pixel data is empty.</returns>
        /// <exception cref="ArgumentException"></exception>
        public SKImage? ToSKImage()
        {
            var pixelSize = data.Size;
            if (pixelSize.Width <= 0 || pixelSize.Height <= 0) return null;

            var info = new SKImageInfo(pixelSize.Width, pixelSize.Height, ToSkColorType(data.Format), ToSkAlphaType(data.AlphaFormat));
            using var skData = SKData.CreateCopy(data.Data, data.Stride * pixelSize.Height);
            return SKImage.FromPixels(info, skData, data.Stride);

            static SKColorType ToSkColorType(PixelFormat fmt)
            {
                if (fmt == PixelFormat.Rgb565)
                    return SKColorType.Rgb565;
                if (fmt == PixelFormat.Bgra8888)
                    return SKColorType.Bgra8888;
                if (fmt == PixelFormat.Rgba8888)
                    return SKColorType.Rgba8888;
                if (fmt == PixelFormat.Rgb32)
                    return SKColorType.Rgb888x;
                throw new ArgumentException("Unknown pixel format: " + fmt);
            }

            static SKAlphaType ToSkAlphaType(AlphaFormat fmt)
            {
                return fmt switch
                {
                    AlphaFormat.Premul => SKAlphaType.Premul,
                    AlphaFormat.Unpremul => SKAlphaType.Unpremul,
                    AlphaFormat.Opaque => SKAlphaType.Opaque,
                    _ => throw new ArgumentException($"Unknown alpha format: {fmt}")
                };
            }
        }
    }
}