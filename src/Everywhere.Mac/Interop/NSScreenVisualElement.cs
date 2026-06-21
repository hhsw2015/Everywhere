using System.Diagnostics;
using System.IO;
using Avalonia;
using Everywhere.Interop;
using Foundation;

namespace Everywhere.Mac.Interop;

public class NSScreenVisualElement(NSScreen screen) : IVisualElement
{
    private readonly NSScreen _screen = screen;

    public string Id => $"Screen:{GetScreenNumber(_screen)}";

    public IVisualElement? Parent => null;

    public VisualElementSiblingAccessor SiblingAccessor => new ScreenSiblingAccessor(this);

    public IEnumerable<IVisualElement> Children
    {
        get
        {
            var bounds = BoundingRectangle;
            var apps = NSWorkspace.SharedWorkspace.RunningApplications;

            foreach (var app in apps)
            {
                if (app.ActivationPolicy == NSApplicationActivationPolicy.Prohibited) continue;

                if (AXUIElement.ElementFromPid(app.ProcessIdentifier) is not { } axApp) continue;

                foreach (var child in axApp.Children)
                {
                    // Filter for windows (TopLevel) that are on this screen
                    if (child.Type == VisualElementType.TopLevel &&
                        child.BoundingRectangle.Intersects(bounds))
                    {
                        yield return child;
                    }
                }
            }
        }
    }

    public VisualElementType Type => VisualElementType.Screen;

    public VisualElementStates States => VisualElementStates.None;

    public string Name => _screen.LocalizedName;

    public PixelRect BoundingRectangle
    {
        get
        {
            var frame = _screen.Frame;
            // NSScreen.Screens[0] is the primary screen.
            // Cocoa coordinates: (0,0) is bottom-left of primary screen.
            // Quartz/Avalonia coordinates: (0,0) is top-left of primary screen.

            var primaryFrame = NSScreen.Screens[0].Frame;
            var x = (int)frame.X;
            var y = (int)(primaryFrame.Height - (frame.Y + frame.Height));

            return new PixelRect(x, y, (int)frame.Width, (int)frame.Height);
        }
    }

    public int ProcessId => 0;

    public nint NativeWindowHandle => 0;

    public string? GetText(int maxLength = -1) => null;

    public void Invoke() => throw new InvalidOperationException();

    public void SetText(string text) => throw new InvalidOperationException();

    public void SendShortcut(KeyboardShortcut shortcut) => throw new InvalidOperationException();

    public string? GetSelectionText() => null;

    public Task<IVisualElement.ICapturedBitmapData> CaptureAsync(CancellationToken cancellationToken)
    {
        var bounds = BoundingRectangle;
        var rect = new CGRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);

#pragma warning disable CA1422
        var cgImage = CGImage.ScreenImage(0, rect);
#pragma warning restore CA1422

        if (cgImage is not null)
        {
            try
            {
                return Task.FromResult<IVisualElement.ICapturedBitmapData>(
                    new CapturedBitmapData(cgImage, 1d));
            }
            finally
            {
                cgImage.Dispose();
            }
        }

        // Fallback: macOS 14+/27 has been observed to return null from
        // CGImage.ScreenImage even with Screen Recording permission granted.
        // The /usr/sbin/screencapture CLI uses the modern ScreenCaptureKit
        // path under the hood and works reliably.
        var fallback = CaptureViaScreencaptureCli(rect);
        if (fallback is not null)
            return Task.FromResult(fallback);

        return Task.FromException<IVisualElement.ICapturedBitmapData>(
            new InvalidOperationException("Failed to capture screen via both CGImage.ScreenImage and screencapture CLI."));
    }

    private static IVisualElement.ICapturedBitmapData? CaptureViaScreencaptureCli(CGRect rect)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ev-cap-{Guid.NewGuid():N}.png");
        try
        {
            // -x: silent (no UI sound). -R x,y,w,h: capture a rect in screen coords.
            var psi = new ProcessStartInfo("/usr/sbin/screencapture")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-x");
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{(int)rect.X},{(int)rect.Y},{(int)rect.Width},{(int)rect.Height}");
            psi.ArgumentList.Add(tmp);
            using var p = Process.Start(psi);
            if (p is null) return null;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            if (!p.WaitForExit(3_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }
            if (p.ExitCode != 0 || !File.Exists(tmp)) return null;

            using var url = NSUrl.FromFilename(tmp);
            using var src = CGImageSource.FromUrl(url);
            if (src is null || src.ImageCount == 0) return null;
            using var cg = src.CreateImage(0, new CGImageOptions());
            if (cg is null) return null;
            return new CapturedBitmapData(cg, 1d);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static int GetScreenNumber(NSScreen screen)
    {
        return (screen.DeviceDescription["NSScreenNumber"] as NSNumber)?.Int32Value ?? 0;
    }

    private sealed class ScreenSiblingAccessor(NSScreenVisualElement element) : VisualElementSiblingAccessor
    {
        private NSScreen[]? _screens;
        private int _index;

        protected override void EnsureResources()
        {
            if (_screens != null) return;
            _screens = NSScreen.Screens;
            _index = Array.IndexOf(_screens, element._screen);
        }

        protected override void ReleaseResources()
        {
            _screens = null;
        }

        protected override IEnumerator<IVisualElement> CreateForwardEnumerator()
        {
            if (_screens is not { } screens || _index < 0) yield break;

            for (var i = _index + 1; i < screens.Length; i++)
            {
                yield return new NSScreenVisualElement(screens[i]);
            }
        }

        protected override IEnumerator<IVisualElement> CreateBackwardEnumerator()
        {
            if (_screens is not { } screens || _index < 0) yield break;

            for (var i = _index - 1; i >= 0; i--)
            {
                yield return new NSScreenVisualElement(screens[i]);
            }
        }
    }
}