using Windows.Win32;
using Avalonia;
using Avalonia.Threading;
using Everywhere.Interop;
using Interop.UIAutomationClient;
using Point = System.Drawing.Point;

namespace Everywhere.Windows.Interop;

public partial class VisualElementContext
{
    /// <summary>
    /// linkclump-plus style: drag a rectangle, harvest every Hyperlink
    /// element whose bounding rect intersects the drag rect, return the
    /// (Title, Url) batch. Same delivery channel as the rest of the
    /// picker — caller writes the batch into the agent-state snapshot.
    /// </summary>
    private sealed class LinkRectSession : ScreenSelectionSession
    {
        public static async Task<HarvestResult> HarvestAsync(
            IWindowHelper windowHelper, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            LinkRectSession? window = null;
            try
            {
                window = new LinkRectSession(windowHelper);
                window.Show();
            }
            catch (Exception ex)
            {
                window?._rectPromise.TrySetException(ex);
                throw;
            }
            using var _ = cancellationToken.Register(() =>
                Dispatcher.UIThread.Post(() => window!.Close()));
            var rect = await window._rectPromise.Task;
            if (rect is null) return new HarvestResult(window._wasCanceled, []);
            var harvested = await Task.Run(() => HarvestLinks(rect.Value), cancellationToken);
            return new HarvestResult(false, harvested);
        }

        private readonly TaskCompletionSource<PixelRect?> _rectPromise = new();

        private bool _isDragging;
        private bool _wasCanceled;
        private PixelPoint _dragStart;
        private PixelRect _dragRect;
        private bool _maskCleared;

        private LinkRectSession(IWindowHelper windowHelper)
            : base(
                windowHelper,
                [ScreenSelectionMode.LinkRect],
                ScreenSelectionMode.LinkRect)
        {
        }

        protected override void OnCanceled()
        {
            _wasCanceled = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (_wasCanceled || _dragRect.Width <= 0 || _dragRect.Height <= 0)
                _rectPromise.TrySetResult(null);
            else
                _rectPromise.TrySetResult(_dragRect);
        }

        protected override void OnLeftButtonDown()
        {
            PInvoke.GetCursorPos(out var point);
            _dragStart = new PixelPoint(point.X, point.Y);
            _isDragging = true;
            _dragRect = new PixelRect(_dragStart, new PixelSize(0, 0));
            foreach (var maskWindow in MaskWindows) maskWindow.SetMask(_dragRect);
            UpdateToolTipInfo(_dragRect);
        }

        protected override bool OnLeftButtonUp()
        {
            if (!_isDragging) return false;
            _isDragging = false;
            return _dragRect.Width > 0 && _dragRect.Height > 0;
        }

        protected override void PickElement(Point cursorPos)
        {
            var pixelPoint = new PixelPoint(cursorPos.X, cursorPos.Y);
            if (!_isDragging)
            {
                if (!_maskCleared)
                {
                    foreach (var maskWindow in MaskWindows) maskWindow.SetMask(new PixelRect(0, 0, 0, 0));
                    ToolTipWindow.ToolTip.SizeInfo = null;
                    _maskCleared = true;
                }
                return;
            }
            _maskCleared = false;
            var topLeft = new PixelPoint(Math.Min(_dragStart.X, pixelPoint.X), Math.Min(_dragStart.Y, pixelPoint.Y));
            var bottomRight = new PixelPoint(Math.Max(_dragStart.X, pixelPoint.X), Math.Max(_dragStart.Y, pixelPoint.Y));
            _dragRect = new PixelRect(topLeft, new PixelSize(bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y));
            foreach (var maskWindow in MaskWindows) maskWindow.SetMask(_dragRect);
            UpdateToolTipInfo(_dragRect);
        }

        private void UpdateToolTipInfo(PixelRect rect)
        {
            ToolTipWindow.ToolTip.SizeInfo = $"{rect.Width} x {rect.Height}";
        }

        private const int MaxDepth = 60;

        private static List<HarvestedLink> HarvestLinks(PixelRect dragRect)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<HarvestedLink>(64);
            // Total-node cap as a safety net for huge UIA trees (Outlook,
            // big Excel, document apps with thousands of inline links).
            // The drag-rect prune already drops most of them but a few
            // browsers still descend deep into off-screen virtualized lists.
            var budget = new WalkBudget { Remaining = 50_000 };
            try
            {
                var root = Automation.GetRootElement();
                if (root != null) Walk(root, dragRect, seen, result, depth: 0, budget);
            }
            catch (System.Runtime.InteropServices.COMException) { /* best-effort */ }
            catch (InvalidCastException) { /* COM-cast on detached element */ }
            catch (UnauthorizedAccessException) { /* sandboxed app */ }
            return result;
        }

        private sealed class WalkBudget { public int Remaining; }

        private static void Walk(
            IUIAutomationElement element,
            PixelRect dragRect,
            HashSet<string> seen,
            List<HarvestedLink> result,
            int depth,
            WalkBudget budget)
        {
            if (depth > MaxDepth) return;
            if (budget.Remaining <= 0) return;
            IUIAutomationElement? child;
            try { child = TreeWalker.GetFirstChildElement(element); }
            catch (System.Runtime.InteropServices.COMException) { return; }
            catch (InvalidCastException) { return; }

            while (child is not null && budget.Remaining > 0)
            {
                budget.Remaining--;
                ProcessChild(child, dragRect, seen, result, depth, budget);
                try { child = TreeWalker.GetNextSiblingElement(child); }
                catch (System.Runtime.InteropServices.COMException) { break; }
                catch (InvalidCastException) { break; }
            }
        }

        private static void ProcessChild(
            IUIAutomationElement child,
            PixelRect dragRect,
            HashSet<string> seen,
            List<HarvestedLink> result,
            int depth,
            WalkBudget budget)
        {
            tagRECT bounds;
            try { bounds = child.CurrentBoundingRectangle; }
            catch { return; }
            var width = bounds.right - bounds.left;
            var height = bounds.bottom - bounds.top;
            if (width <= 0 || height <= 0) return;
            var childRect = new PixelRect(bounds.left, bounds.top, width, height);
            if (!IntersectsLoose(childRect, dragRect)) return;

            int controlType;
            try { controlType = child.CurrentControlType; }
            catch { controlType = 0; }
            if (controlType == UIA_ControlTypeIds.UIA_HyperlinkControlTypeId)
            {
                string? url = null;
                try
                {
                    if (child.TryGetValuePattern() is { } vp
                        && !string.IsNullOrEmpty(vp.CurrentValue))
                        url = vp.CurrentValue;
                }
                catch { /* fall through */ }
                try
                {
                    if (string.IsNullOrEmpty(url)
                        && child.TryGetLegacyIAccessiblePattern() is { } lp
                        && !string.IsNullOrEmpty(lp.CurrentValue))
                        url = lp.CurrentValue;
                }
                catch { /* fall through */ }
                if (!string.IsNullOrEmpty(url)
                    && url.Length <= 2048
                    && IsAllowedScheme(url))
                {
                    // UIA hyperlink label fallback: Name -> HelpText -> first
                    // child's Name. Some browsers expose icon-anchor labels
                    // as the help text or as a child Text element only.
                    string? title = null;
                    try { title = child.CurrentName; }
                    catch (System.Runtime.InteropServices.COMException) { }
                    catch (InvalidCastException) { }
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        try { title = child.CurrentHelpText; }
                        catch (System.Runtime.InteropServices.COMException) { }
                        catch (InvalidCastException) { }
                    }
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        try
                        {
                            var firstText = TreeWalker.GetFirstChildElement(child);
                            if (firstText is not null) title = firstText.CurrentName;
                        }
                        catch (System.Runtime.InteropServices.COMException) { }
                        catch (InvalidCastException) { }
                    }
                    if (title is not null && title.Length > 200) title = title[..200];
                    var key = url + "\0" + (title ?? string.Empty);
                    if (seen.Add(key))
                        result.Add(new HarvestedLink(title ?? string.Empty, url, childRect));
                }
            }
            Walk(child, dragRect, seen, result, depth + 1, budget);
        }

        // Reject javascript:/data:/file:/vbscript: etc — they can come from
        // attacker-controlled page content and would land in agent-state
        // verbatim. Allow only navigable web schemes plus mailto.
        private static bool IsAllowedScheme(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
                return u.Scheme is "http" or "https" or "mailto";
            return false;
        }

        private static bool IntersectsLoose(PixelRect a, PixelRect b)
        {
            return !(a.Right < b.X || a.X > b.Right || a.Bottom < b.Y || a.Y > b.Bottom);
        }
    }
}
