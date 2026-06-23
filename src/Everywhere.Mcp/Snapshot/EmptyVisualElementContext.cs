using Avalonia.Media.Imaging;
using Everywhere.Interop;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Fallback <see cref="IVisualElementContext"/> for the stdio transport when no GUI host
/// has registered a real implementation. Returns "no apps / no focus" — the agent will
/// see <c>list_apps == []</c> instead of an exception. The HTTP transport runs inside the
/// GUI host where the real platform context is wired (see <c>EverywhereMcpServiceExtensions</c>).
/// </summary>
public sealed class EmptyVisualElementContext : IVisualElementContext
{
    public IVisualElement? FocusedElement => null;

    public IEnumerable<IVisualElement> Screens => [];

    public IVisualElement? ElementFromPoint(Avalonia.PixelPoint point, ScreenSelectionMode mode = ScreenSelectionMode.Element) => null;

    public IVisualElement? ElementFromPointer(ScreenSelectionMode mode = ScreenSelectionMode.Element) => null;

    public IVisualElement? ElementFromWindowHandle(nint windowHandle) => null;

    public Task<IVisualElement?> PickVisualElementAsync(ScreenSelectionMode? initialMode) =>
        Task.FromResult<IVisualElement?>(null);

    public Task<Bitmap?> TakeScreenshotAsync(ScreenSelectionMode? initialMode) =>
        Task.FromResult<Bitmap?>(null);

    public Task<HarvestResult> HarvestLinksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new HarvestResult(false, Array.Empty<HarvestedLink>()));

    public IDisposable Subscribe(IObserver<TextSelectionData> observer) =>
        new EmptySubscription();

    private sealed class EmptySubscription : IDisposable
    {
        public void Dispose() { }
    }
}
