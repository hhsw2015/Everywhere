using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using CoreFoundation;
using Everywhere.Interop;
using ObjCRuntime;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Provides interop methods for macOS Accessibility API (AXUIElement).
/// </summary>
public partial class AXUIElement : NSObject, IVisualElement
{
    public string Id => $"{ProcessId}.{NativeWindowHandle}.{CFInterop.CFHash(Handle)}";

    public IVisualElement? Parent => field ??= GetAttributeAsElement(AXAttributeConstants.Parent);

    public VisualElementSiblingAccessor SiblingAccessor => new SiblingAccessorImpl(this);

    public IEnumerable<IVisualElement> Children
    {
        get
        {
            // OCCU childTraversalAttributes (AccessibilitySnapshot.swift L866):
            // table-like roles surface their actual rows on AXRows, not
            // AXChildren. AXList additionally uses AXVisibleChildren.
            // AXContents catches some SwiftUI/web container cases. Visit
            // AXChildren too unless the role 'primary-uses' Rows/Visible.
            var role = Role;
            var usesRowsPrimary = role == AXRoleAttribute.AXOutline
                || role == AXRoleAttribute.AXList
                || role == AXRoleAttribute.AXTable
                || role == AXRoleAttribute.AXBrowser;
            var usesVisiblePrimary = role == AXRoleAttribute.AXList;

            // Probe per-attribute and yield in OCCU's order. We dedup on
            // CFEqual via a hash of the underlying handle to avoid
            // re-emitting the same child when AXRows / AXVisibleChildren
            // overlap (common on AXList).
            var seen = new HashSet<nint>();

            // AXChildren — skipped only when a row-bearing primary
            // attribute is already producing the children.
            using var rowsProbe = usesRowsPrimary || usesVisiblePrimary
                ? GetAttribute<NSArray>(AXAttributeConstants.Rows)
                : null;
            using var visProbe = usesVisiblePrimary
                ? GetAttribute<NSArray>(AXAttributeConstants.VisibleChildren)
                : null;
            var hasRows = rowsProbe is { Count: > 0 };
            var hasVisible = visProbe is { Count: > 0 };
            if (!(hasRows && usesRowsPrimary) && !(hasVisible && usesVisiblePrimary))
            {
                using var children = GetAttribute<NSArray>(AXAttributeConstants.Children);
                if (children is not null)
                {
                    for (nuint i = 0; i < children.Count; i++)
                    {
                        if (FromCopyArray(children, i) is { } child)
                        {
                            if (seen.Add((nint)child.Handle)) yield return child;
                            else child.Dispose();
                        }
                    }
                }
            }

            // AXRows — for AXTable / AXOutline / AXList / AXBrowser.
            using var rows = GetAttribute<NSArray>(AXAttributeConstants.Rows);
            if (rows is not null)
            {
                for (nuint i = 0; i < rows.Count; i++)
                {
                    if (FromCopyArray(rows, i) is { } child)
                    {
                        if (seen.Add((nint)child.Handle)) yield return child;
                        else child.Dispose();
                    }
                }
            }

            // AXContents — SwiftUI / web area containers.
            using var contents = GetAttribute<NSArray>(AXAttributeConstants.Contents);
            if (contents is not null)
            {
                for (nuint i = 0; i < contents.Count; i++)
                {
                    if (FromCopyArray(contents, i) is { } child)
                    {
                        if (seen.Add((nint)child.Handle)) yield return child;
                        else child.Dispose();
                    }
                }
            }

            // AXVisibleChildren — AXList shows only on-screen items here.
            using var visible = GetAttribute<NSArray>(AXAttributeConstants.VisibleChildren);
            if (visible is not null)
            {
                for (nuint i = 0; i < visible.Count; i++)
                {
                    if (FromCopyArray(visible, i) is { } child)
                    {
                        if (seen.Add((nint)child.Handle)) yield return child;
                        else child.Dispose();
                    }
                }
            }
        }
    }

    public AXRoleAttribute Role { get; }

    public AXSubroleAttribute Subrole { get; }

    public VisualElementType Type
    {
        get
        {
            return Role switch
            {
                AXRoleAttribute.AXStaticText => VisualElementType.Label,
                AXRoleAttribute.AXTextField or
                    AXRoleAttribute.AXTextArea => VisualElementType.TextEdit,

                AXRoleAttribute.AXButton or
                    AXRoleAttribute.AXMenuButton or
                    AXRoleAttribute.AXPopUpButton or
                    AXRoleAttribute.AXDisclosureTriangle => VisualElementType.Button,

                AXRoleAttribute.AXCheckBox => VisualElementType.CheckBox,
                AXRoleAttribute.AXRadioButton => VisualElementType.RadioButton,
                AXRoleAttribute.AXComboBox => VisualElementType.ComboBox,

                AXRoleAttribute.AXList or
                    AXRoleAttribute.AXRuler => VisualElementType.ListView,

                AXRoleAttribute.AXOutline => VisualElementType.TreeView,
                AXRoleAttribute.AXTable => VisualElementType.Table,
                AXRoleAttribute.AXRow => VisualElementType.TableRow,

                AXRoleAttribute.AXMenuBar or
                    AXRoleAttribute.AXMenu => VisualElementType.Menu,

                AXRoleAttribute.AXMenuBarItem or
                    AXRoleAttribute.AXMenuItem => VisualElementType.MenuItem,

                AXRoleAttribute.AXTabGroup => VisualElementType.TabControl,
                AXRoleAttribute.AXToolbar => VisualElementType.ToolBar,

                AXRoleAttribute.AXGroup or
                    AXRoleAttribute.AXRadioGroup or
                    AXRoleAttribute.AXSplitGroup or
                    AXRoleAttribute.AXBrowser or
                    AXRoleAttribute.AXSheet or
                    AXRoleAttribute.AXDrawer or
                    AXRoleAttribute.AXCell => VisualElementType.Panel,

                AXRoleAttribute.AXWindow or
                    AXRoleAttribute.AXApplication or
                    AXRoleAttribute.AXSystemWide => VisualElementType.TopLevel,

                AXRoleAttribute.AXSplitter => VisualElementType.Splitter,
                AXRoleAttribute.AXSlider => VisualElementType.Slider,
                AXRoleAttribute.AXScrollBar => VisualElementType.ScrollBar,

                AXRoleAttribute.AXBusyIndicator => VisualElementType.Spinner,
                AXRoleAttribute.AXProgressIndicator or
                    AXRoleAttribute.AXLevelIndicator or
                    AXRoleAttribute.AXRelevanceIndicator or
                    AXRoleAttribute.AXValueIndicator => VisualElementType.ProgressBar,

                AXRoleAttribute.AXImage => VisualElementType.Image,
                AXRoleAttribute.AXLink => VisualElementType.Hyperlink,
                AXRoleAttribute.AXWebArea => VisualElementType.Document,

                AXRoleAttribute.AXScrollArea or
                    AXRoleAttribute.AXLayoutArea or
                    AXRoleAttribute.AXLayoutItem or
                    AXRoleAttribute.AXGrowArea or
                    AXRoleAttribute.AXMatte or
                    AXRoleAttribute.AXRulerMarker or
                    AXRoleAttribute.AXColumn or
                    AXRoleAttribute.AXGrid or
                    AXRoleAttribute.AXPage or
                    AXRoleAttribute.AXPopover => VisualElementType.Panel,

                _ => Subrole switch
                {
                    AXSubroleAttribute.AXCloseButton or
                        AXSubroleAttribute.AXMinimizeButton or
                        AXSubroleAttribute.AXZoomButton or
                        AXSubroleAttribute.AXToolbarButton or
                        AXSubroleAttribute.AXSortButton or
                        AXSubroleAttribute.AXTabButton => VisualElementType.Button,

                    AXSubroleAttribute.AXSearchField => VisualElementType.TextEdit,

                    AXSubroleAttribute.AXToggle or
                        AXSubroleAttribute.AXSwitch => VisualElementType.CheckBox,

                    AXSubroleAttribute.AXStandardWindow or
                        AXSubroleAttribute.AXDialog or
                        AXSubroleAttribute.AXSystemDialog or
                        AXSubroleAttribute.AXFloatingWindow or
                        AXSubroleAttribute.AXSystemFloatingWindow => VisualElementType.Panel,

                    _ => VisualElementType.Unknown
                }
            };
        }
    }

    public VisualElementStates States
    {
        get
        {
            var states = VisualElementStates.None;
            if (GetAttribute<NSNumber>(AXAttributeConstants.Enabled)?.BoolValue == false) states |= VisualElementStates.Disabled;
            if (GetAttribute<NSNumber>(AXAttributeConstants.Focused)?.BoolValue == true) states |= VisualElementStates.Focused;
            if (GetAttribute<NSNumber>(AXAttributeConstants.Hidden)?.BoolValue == true) states |= VisualElementStates.Offscreen;
            if (GetAttribute<NSNumber>(AXAttributeConstants.Selected)?.BoolValue == true) states |= VisualElementStates.Selected;
            if (Subrole == AXSubroleAttribute.AXSecureTextField) states |= VisualElementStates.Password;

            // OCCU summarizeTraits parity. Each is best-effort —
            // unsupported attribute returns null, leaving the flag clear.
            if (GetAttribute<NSNumber>(AXAttributeConstants.Expanded)?.BoolValue == true)  states |= VisualElementStates.Expanded;
            if (GetAttribute<NSNumber>(AXAttributeConstants.Required)?.BoolValue == true)  states |= VisualElementStates.Required;
            if (GetAttribute<NSNumber>(AXAttributeConstants.Edited)?.BoolValue == true)    states |= VisualElementStates.Edited;
            if (GetAttribute<NSNumber>(AXAttributeConstants.MainTrait)?.BoolValue == true) states |= VisualElementStates.Main;
            if (GetAttribute<NSNumber>(AXAttributeConstants.MinimizedAttr)?.BoolValue == true) states |= VisualElementStates.Minimized;
            if (GetAttribute<NSNumber>(AXAttributeConstants.GrabbedAttr)?.BoolValue == true)   states |= VisualElementStates.Grabbed;

            // AXValue on toggles / checkboxes: 1 = checked, 0 = unchecked.
            if (Role == AXRoleAttribute.AXCheckBox || Role == AXRoleAttribute.AXRadioButton)
            {
                var v = GetAttribute<NSNumber>(AXAttributeConstants.Value);
                if (v is not null && v.Int32Value > 0) states |= VisualElementStates.Checked;
            }

            return states;
        }
    }

    public string? Name
    {
        get
        {
            // OCCU AccessibilitySnapshot.swift L631-650 cascade:
            //   AXTitle -> AXDescription -> AXHelp -> AXValue (for buttons w/ value)
            //   -> AXTitleUIElement.AXValue -> AXIdentifier
            //   -> first AXStaticText child's AXValue.
            // The SwiftUI rewrite of Calculator parks the digit on a
            // child AXStaticText, so we need the child-text fallback or
            // every digit button reads as nameless.
            var t = GetAttribute<NSString>(AXAttributeConstants.Title);
            if (!string.IsNullOrWhiteSpace(t)) return t;
            var d = GetAttribute<NSString>(AXAttributeConstants.Description);
            if (!string.IsNullOrWhiteSpace(d)) return d;
            var h = GetAttribute<NSString>(AXAttributeConstants.Help);
            if (!string.IsNullOrWhiteSpace(h)) return h;
            // AXValue is *content* for text fields / sliders / progress —
            // using it as the label would make Name == GetText(). Only
            // consult it for roles where it's known to be a label-like
            // state string (radio "On"/"Off", popup button caption,
            // checkbox state).
            if (IsLabelBearingRole(Role))
            {
                var v = GetAttribute<NSObject>(AXAttributeConstants.Value)?.ToString();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            // The next two fallbacks (TitleUIElement, first-child static
            // text) require additional cross-process AX RPCs. They only
            // make sense for label-bearing roles — text fields,
            // sliders, scroll bars etc. don't have separate label
            // controls so paying for those reads on every node would be
            // pure overhead.
            if (IsLabelBearingRole(Role))
            {
                // AXTitleUIElement is a sibling/child label control.
                var titleElement = GetAttribute<AXUIElement>(AXAttributeConstants.TitleUIElement);
                if (titleElement is not null)
                {
                    var tv = titleElement.GetAttribute<NSObject>(AXAttributeConstants.Value)?.ToString();
                    if (!string.IsNullOrWhiteSpace(tv)) return tv;
                    var tt = titleElement.GetAttribute<NSString>(AXAttributeConstants.Title);
                    if (!string.IsNullOrWhiteSpace(tt)) return tt;
                }
                // SwiftUI parks button labels in a child AXStaticText.
                var childText = TryFirstChildStaticTextValue();
                if (!string.IsNullOrWhiteSpace(childText)) return childText;
            }
            // AXIdentifier is the automation hook. UI test ids like "btn7"
            // are common on SwiftUI; better than empty.
            var id = GetAttribute<NSString>(AXAttributeConstants.Identifier);
            if (!string.IsNullOrWhiteSpace(id)) return id;
            return null;
        }
    }

    /// <summary>
    /// Roles where AXValue is a label-like state string and where
    /// AXTitleUIElement / first-child-static-text fallbacks are
    /// worth the extra cross-process AX RPC cost. Covers the OCCU
    /// common set: button-shaped controls, menu items, links, images,
    /// tabs, table/outline cells/rows, switches/toggles, menubar items.
    /// Roles that don't appear here treat AXValue as content (text
    /// fields / sliders) and never have a sibling label control.
    /// </summary>
    internal static bool IsLabelBearingRole(AXRoleAttribute role) => role switch
    {
        AXRoleAttribute.AXButton or AXRoleAttribute.AXMenuButton or AXRoleAttribute.AXPopUpButton
            or AXRoleAttribute.AXCheckBox or AXRoleAttribute.AXRadioButton
            or AXRoleAttribute.AXMenuItem or AXRoleAttribute.AXMenuBarItem
            or AXRoleAttribute.AXLink or AXRoleAttribute.AXImage
            or AXRoleAttribute.AXDisclosureTriangle
            or AXRoleAttribute.AXCell or AXRoleAttribute.AXRow
            => true,
        _ => false,
    };

    private string? TryFirstChildStaticTextValue()
    {
        try
        {
            int seen = 0;
            foreach (var c in Children)
            {
                if (++seen > 12) break;
                if (c is not AXUIElement child) continue;
                if (child.Role != AXRoleAttribute.AXStaticText) continue;
                var v = child.GetAttribute<NSObject>(AXAttributeConstants.Value)?.ToString();
                if (!string.IsNullOrWhiteSpace(v)) return v;
                var t = child.GetAttribute<NSString>(AXAttributeConstants.Title);
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// AX accessible description — Safari / Chrome put hyperlink anchor
    /// text here when AXTitle is empty (icon-only / svg+span anchors).
    /// </summary>
    public string? Description => GetAttribute<NSString>(AXAttributeConstants.Description);

    /// <summary>OCCU AXPlaceholderValue (AccessibilitySnapshot.swift L645).
    /// Returns the placeholder text on AXTextField / AXTextArea / AXSearchField
    /// inputs; null otherwise.</summary>
    public string? Placeholder => GetAttribute<NSString>(AXAttributeConstants.Placeholder);

    /// <summary>OCCU formattedLabelSegment uses AXDescription as the
    /// "label" channel separate from AXTitle. We expose it under the
    /// platform-portable name so renderers can emit a 'Description: ...'
    /// segment without taking a Mac-specific dependency.</summary>
    public string? AccessibleDescription => GetAttribute<NSString>(AXAttributeConstants.Description);

    public string? Url
    {
        get
        {
            // AXURL on Hyperlink elements is *usually* a CFURLRef, but some
            // apps (Electron, older WebKit, non-browser hosts) return a
            // CFString or even AXTextMarker. Type-check before unwrapping
            // so a non-URL handle never reaches CFURLGetString. Wrap the
            // whole thing — AX can throw on apps that have torn down their
            // accessibility tree mid-call.
            try
            {
                if (CopyAttributeValue(Handle, AXAttributeConstants.URL.Handle, out var raw) != AXError.Success
                    || raw == 0)
                    return null;
                try
                {
                    var typeId = CFGetTypeID(raw);
                    if (typeId == CFURLGetTypeID())
                    {
                        var cfStr = CFURLGetString(raw);
                        if (cfStr == 0) return null;
                        using var nsStr = Runtime.GetNSObject<NSString>(cfStr);
                        return nsStr?.ToString();
                    }
                    if (typeId == CFStringGetTypeID())
                    {
                        using var nsStr = Runtime.GetNSObject<NSString>(raw);
                        return nsStr?.ToString();
                    }
                    return null;
                }
                finally { CFInterop.CFRelease(raw); }
            }
            catch
            {
                return null;
            }
        }
    }

    public PixelRect BoundingRectangle
    {
        get
        {
            try
            {
                var posVal = GetAttribute<AXValue>(AXAttributeConstants.Position);
                var sizeVal = GetAttribute<AXValue>(AXAttributeConstants.Size);

                if (posVal is null || sizeVal is null) return default;

                var pos = posVal.Point;
                var size = sizeVal.Size;
                return new PixelRect((int)pos.X, (int)pos.Y, (int)size.Width, (int)size.Height);
            }
            catch
            {
                return default;
            }
        }
    }

    public int ProcessId => GetPid(Handle, out var pid) == AXError.Success ? pid : 0;

    public nint NativeWindowHandle => GetWindow(Handle, out var windowId) == AXError.Success ? (nint)windowId : 0;

    static AXUIElement()
    {
        // default timeout for AX calls is 6s, which is too long for our use case.
        AXUIElementSetMessagingTimeout(SystemWide.Handle.Handle, 1f);
    }

    /// <summary>
    /// Create AXUIElement from NSArray at given index.
    /// </summary>
    /// <param name="array"></param>
    /// <param name="index"></param>
    /// <returns></returns>
    private static AXUIElement? FromCopyArray(NSArray array, nuint index)
    {
        var pValue = array.ValueAt(index);
        if (pValue.Handle == 0) return null;

        CFInterop.CFRetain(pValue);
        return new AXUIElement(pValue.Handle);
    }

    private AXUIElement(NativeHandle handle) : base(handle, true)
    {
        var axRole = GetAttribute<NSString>(AXAttributeConstants.Role);
        Role = Enum.TryParse<AXRoleAttribute>(axRole, true, out var role) ? role : AXRoleAttribute.AXUnknown;
        var axSubrole = GetAttribute<NSString>(AXAttributeConstants.Subrole);
        Subrole = Enum.TryParse<AXSubroleAttribute>(axSubrole, true, out var subrole) ? subrole : AXSubroleAttribute.AXUnknown;
    }

    /// <summary>
    /// OCCU meaningfulActions parity (ComputerUseService.swift L996-1010).
    /// AXUIElementCopyActionNames returns every supported verb; we keep
    /// only the ones that change observable UI state, so the agent's
    /// tool surface isn't polluted with AX-internal verbs like
    /// AXScrollToVisible.
    /// </summary>
    public IReadOnlyList<string> SupportedActions
    {
        get
        {
            var err = CopyActionNames(Handle, out var arrPtr);
            if (err != AXError.Success || arrPtr == 0) return Array.Empty<string>();
            try
            {
                // owns:true — CFArray returned by AXUIElementCopyActionNames
                // is +1 retained per CF "Copy" rule, so the NSArray
                // wrapper must release it on Dispose. Without owns:true
                // we leak the array on every call.
                using var arr = ObjCRuntime.Runtime.GetNSObject<NSArray>(arrPtr, owns: true);
                if (arr is null) return Array.Empty<string>();
                var meaningful = new List<string>(capacity: 4);
                for (nuint i = 0; i < arr.Count; i++)
                {
                    // GetItem<T> on NSArray returns a +1 retained wrapper
                    // (consistent with windowInfoArray.GetItem<NSDictionary>(0)
                    // below and other GetItem call sites). Dispose it
                    // to avoid leaking one NSString per supported action.
                    using var s = arr.GetItem<NSString>(i);
                    var v = s?.ToString();
                    if (string.IsNullOrEmpty(v)) continue;
                    if (IsMeaningful(v)) meaningful.Add(v);
                }
                return meaningful;
            }
            catch (System.Runtime.InteropServices.COMException) { return Array.Empty<string>(); }
            catch (ObjectDisposedException) { return Array.Empty<string>(); }
            catch (InvalidOperationException) { return Array.Empty<string>(); }
        }
    }

    private static bool IsMeaningful(string action) => action switch
    {
        "AXPress" or "AXConfirm" or "AXOpen" or "AXShowMenu"
            or "AXIncrement" or "AXDecrement" or "AXPick" or "AXCancel"
            or "AXDelete" or "AXRaise" => true,
        _ => false,
    };

    public string? GetText(int maxLength = -1)
    {
        // Direct value — works for text fields / labels / sliders.
        var text = GetAttribute<NSObject>(AXAttributeConstants.Value)?.ToString();
        if (!string.IsNullOrEmpty(text))
        {
            if (Role == AXRoleAttribute.AXCheckBox) return text == "0" ? "false" : "true";
            return maxLength > 0 && text.Length > maxLength ? text[..maxLength] : text;
        }

        // Row text flattening (OCCU flattenedRowTexts): for AXRow/AXTableRow/
        // AXOutlineRow nodes the cell texts are exposed only on descendant
        // AXStaticText/AXTextField; concatenate them as the row's text so
        // the agent can match a row by visible content.
        // OCCU AXRow detection: the row role itself plus the AXTableRow /
        // AXOutlineRow subroles that map onto the same AXRow role with
        // a more specific subrole attribute.
        if (Role == AXRoleAttribute.AXRow
            || Subrole == AXSubroleAttribute.AXTableRow
            || Subrole == AXSubroleAttribute.AXOutlineRow)
        {
            var collected = CollectDescendantText(maxDepth: 4, max: maxLength > 0 ? maxLength : 600);
            if (!string.IsNullOrEmpty(collected)) return collected;
        }
        return null;
    }

    /// <summary>
    /// OCCU descendantTexts: walk up to 4 hops, collect AXValue/AXTitle from
    /// AXStaticText / AXTextField nodes, dedup + concat with " | ".
    /// </summary>
    private string? CollectDescendantText(int maxDepth, int max)
    {
        var sb = new System.Text.StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Walk(this, 0);
        return sb.Length == 0 ? null : sb.ToString();

        void Walk(AXUIElement el, int depth)
        {
            if (depth >= maxDepth || sb.Length >= max) return;
            try
            {
                if (el.Role == AXRoleAttribute.AXStaticText || el.Role == AXRoleAttribute.AXTextField)
                {
                    var v = el.GetAttribute<NSObject>(AXAttributeConstants.Value)?.ToString();
                    if (string.IsNullOrEmpty(v))
                        v = el.GetAttribute<NSString>(AXAttributeConstants.Title);
                    if (!string.IsNullOrEmpty(v) && seen.Add(v))
                    {
                        if (sb.Length > 0) sb.Append(" | ");
                        sb.Append(v);
                    }
                }
                foreach (var c in el.Children)
                {
                    if (sb.Length >= max) break;
                    if (c is AXUIElement axc) Walk(axc, depth + 1);
                }
            }
            catch { }
        }
    }

    // OCCU-inspired action chain. AXPress alone misses a lot:
    //   - List rows on Lark/Feishu/Electron-web apps have no Press, only
    //     selection (set AXSelectedChildren on parent list to [self]).
    //   - File / disclosure rows respond to AXOpen.
    //   - Some menu items respond only to AXConfirm (ringing the
    //     activation), and some popovers only to AXShowMenu.
    // The chain mirrors OCCU ComputerUseService.performLeftClick().
    public void Invoke() => Invoke(clickCount: 1);

    /// <summary>
    /// OCCU performAction L897 honors the requested clickCount by
    /// repeating AXUIElementPerformAction with a 50ms gap between
    /// attempts. Used for double-click-to-edit / triple-click-to-select.
    /// </summary>
    public void Invoke(int clickCount)
    {
        // OCCU performPreferredClick (ComputerUseService.swift L699-733)
        // and performAction (L891) require the action to actually be in
        // AXUIElementCopyActionNames before posting it. macOS AX returns
        // .success for unsupported actions on some controls (notably
        // SwiftUI buttons, which advertise no actions but accept AXPress
        // syntactically and treat it as a no-op). The earlier blind chain
        // would 'succeed' on the first call and never trigger the
        // coordinate fallback — Calculator '7' was the canonical
        // regression. Gate every verb on the supported-actions list and
        // throw when nothing in the chain is actually available so the
        // caller can run a real coordinate click.
        var available = SupportedActions;

        // 1. List-item selection — only when not inside a web area, since
        //    web rows often have a Press the parent doesn't expose.
        if (!HasAncestorRole(AXRoleAttribute.AXWebArea))
        {
            if (TrySelectAsListItem()) return;
        }

        // 2. Standard verbs in OCCU order: Press → Confirm → Open. Each
        //    repeats clickCount times with a 50ms gap (OCCU L897), and
        //    the chain itself sleeps 150ms after a successful action so
        //    state changes (panel open, menu mount) can settle before
        //    the next snapshot — matches OCCU CUS L820.
        if (TryPerformActionGated(AXAttributeConstants.Press, available, clickCount)) { Thread.Sleep(150); return; }
        if (TryPerformActionGated(AXAttributeConstants.Confirm, available, clickCount)) { Thread.Sleep(150); return; }
        if (TryPerformActionGated(AXAttributeConstants.Open, available, clickCount)) { Thread.Sleep(150); return; }

        // (ShowMenu intentionally NOT tried here — OCCU reserves it
        // for right-click only, CUS L724-727. Surfacing it on left
        // click would invert the user's intent.)

        // 4. OCCU descendantClickCandidates (CUS L1040): if neither this
        //    element nor its list-item parent advertised a Press, walk
        //    up to 3 levels of descendants and try each one. Common case
        //    is a wrapper Group around the real Button.
        if (TryDescendantClick(this, depth: 0, clickCount)) { Thread.Sleep(150); return; }

        // 5. OCCU includeNearbyHitTesting (CUS L838-869): before
        //    activation/coord fallback, do AXHitTest at clickActionPoints
        //    (center + leading offset, OCCU CUS L173-189) and try each
        //    hit-record. SwiftUI buttons whose own AXPress no-ops often
        //    have a child Group / shape element at the same hit-point
        //    whose AXPress works. Without this retry we drop straight
        //    to coord on Calculator-style apps.
        if (TryNearbyHitTest(clickCount)) { Thread.Sleep(150); return; }

        // 6. OCCU activateClickTarget (CUS L915): for left-click on
        //    activation-only roles (AXWindow), AXRaise + setMain +
        //    setFocused gives the agent a 'click that brings to front'
        //    even when no action verb fits.
        if (canUseActivationOnlyClickFallback(Role) && TryActivate(available)) { Thread.Sleep(150); return; }

        // 1:1 OCCU: when performAXClickSequence returns false (every
        // action verb + descendant + activation fallback rejected),
        // the caller (ComputerUseService L408-416) falls through to
        // performNonAXClickFallback. We mirror: throw a plain failure,
        // dispatcher catches and runs the coord fallback.
        throw new InvalidOperationException(
            $"No supported AX action available (role={Role}, actions=[{string.Join(",", available)}])");
    }

    /// <summary>
    /// OCCU CUS L838-869 + L173-189: hit-test at a small set of points
    /// inside this element's frame (center + a "leading" offset) and
    /// try AX click chain on whatever element AXHitTest returns there.
    /// SwiftUI binds AXPress to a child gesture target whose ref the
    /// snapshot walker never reached; hit-test surfaces it.
    /// </summary>
    private bool TryNearbyHitTest(int clickCount)
    {
        var bounds = BoundingRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;

        var app = ElementFromPid(ProcessId);
        if (app is null) return false;

        // OCCU localClickActionPoints (CUS L173-189): center always,
        // plus a leading point at min(max(w*0.3, 20), max(w-4, 20))
        // from the left edge. Skip leading if it collapses onto center.
        var center = (X: (float)(bounds.X + bounds.Width / 2.0), Y: (float)(bounds.Y + bounds.Height / 2.0));
        var leadingOffset = Math.Min(Math.Max(bounds.Width * 0.3, 20), Math.Max(bounds.Width - 4, 20));
        var leading = (X: (float)(bounds.X + leadingOffset), Y: center.Y);

        var points = Math.Abs(leading.X - center.X) < 1
            ? new[] { center }
            : new[] { center, leading };

        foreach (var (px, py) in points)
        {
            var hit = app.ElementAtPosition(px, py);
            if (hit is null) continue;
            // Avoid recursing into ourselves — same handle, same outcome.
            if (CFInterop.CFHash((nint)hit.Handle) == CFInterop.CFHash((nint)Handle)) continue;

            try
            {
                var hitAvail = hit.SupportedActions;
                if (hitAvail.Count == 0) continue;
                if (hit.TryPerformActionGated(AXAttributeConstants.Press, hitAvail, clickCount)) return true;
                if (hit.TryPerformActionGated(AXAttributeConstants.Confirm, hitAvail, clickCount)) return true;
                if (hit.TryPerformActionGated(AXAttributeConstants.Open, hitAvail, clickCount)) return true;
            }
            catch { /* try next point */ }
        }
        return false;
    }

    private bool TryDescendantClick(AXUIElement node, int depth, int clickCount)
    {
        if (depth >= 3) return false;
        foreach (var c in node.Children)
        {
            if (c is not AXUIElement child) continue;
            try
            {
                var avail = child.SupportedActions;
                if (avail.Count > 0)
                {
                    if (child.TryPerformActionGated(AXAttributeConstants.Press, avail, clickCount)) return true;
                    if (child.TryPerformActionGated(AXAttributeConstants.Confirm, avail, clickCount)) return true;
                    if (child.TryPerformActionGated(AXAttributeConstants.Open, avail, clickCount)) return true;
                }
                if (TryDescendantClick(child, depth + 1, clickCount)) return true;
            }
            catch { /* continue with siblings */ }
        }
        return false;
    }

    private static bool canUseActivationOnlyClickFallback(AXRoleAttribute role)
        => role == AXRoleAttribute.AXWindow;

    private bool TryActivate(IReadOnlyList<string> available)
    {
        var activated = false;
        if (TryPerformActionGated(AXAttributeConstants.RaiseAction, available, 1)) activated = true;
        if (TrySetBoolAttribute(AXAttributeConstants.MainTrait)) activated = true;
        if (TrySetBoolAttribute(AXAttributeConstants.Focused)) activated = true;
        return activated;
    }

    private bool TrySetBoolAttribute(NSString name)
    {
        // Use kCFBooleanTrue indirectly via NSNumber(true). Mac AX
        // accepts CFBoolean here; the .NET wrapper passes the NSNumber
        // boxed handle.
        using var v = NSNumber.FromBoolean(true);
        var err = SetAttributeValue(Handle, name.Handle, v.Handle);
        return err == AXError.Success;
    }

    private bool TryPerformActionGated(NSString actionName, IReadOnlyList<string> available, int clickCount = 1)
    {
        // Only post when the element actually advertises this action.
        // The .success short-circuit on unsupported verbs in macOS AX
        // makes blind posting unreliable.
        var n = actionName.ToString();
        var found = false;
        for (var i = 0; i < available.Count; i++)
        {
            if (string.Equals(available[i], n, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
        }
        if (!found) return false;
        var attempts = Math.Max(clickCount, 1);
        for (var i = 0; i < attempts; i++)
        {
            var error = PerformAction(Handle, actionName.Handle);
            if (error != AXError.Success)
            {
                // Auto-detect SwiftUI signature: the verb WAS advertised
                // but PerformAction came back with a non-Success error.
                // SwiftUI-on-AppKit binds AXPress to the gesture
                // recognizer at hit-test time, so AXUIElement gets a
                // cannotComplete / failure when invoked headlessly.
                // Cache it so the dispatcher's coord fallback can
                // upgrade to GLOBAL automatically without env-var.
                LastInvokeError = error;
                return false;
            }
            if (i < attempts - 1) Thread.Sleep(50);
        }
        LastInvokeError = AXError.Success;
        return true;
    }

    /// <summary>
    /// Thread-local last AX error from TryPerformActionGated. Read by
    /// ElementClickDispatcher to decide whether the click failure
    /// looks like a SwiftUI gesture-recognizer rejection (verb was
    /// available, AXUIElementPerformAction returned non-Success) —
    /// in that case the coord fallback upgrades to GLOBAL even
    /// without EVERYWHERE_ALLOW_GLOBAL_POINTER_FALLBACKS=1.
    /// </summary>
    [ThreadStatic]
    public static AXError LastInvokeError;

    /// <summary>
    /// AX action with explicit verb selector — used by perform_secondary_action
    /// and any caller that wants to distinguish left vs right click.
    /// "press" / "confirm" / "open" / "showmenu" / "increment" / "decrement".
    /// </summary>
    public bool TryInvokeAction(string verb)
    {
        var name = verb?.ToLowerInvariant() switch
        {
            "press" => AXAttributeConstants.Press,
            "confirm" => AXAttributeConstants.Confirm,
            "open" => AXAttributeConstants.Open,
            "showmenu" or "show_menu" => AXAttributeConstants.ShowMenu,
            "increment" => AXAttributeConstants.Increment,
            "decrement" => AXAttributeConstants.Decrement,
            _ => null,
        };
        return name is not null && TryPerformAction(name);
    }

    private bool TryPerformAction(NSString actionName)
    {
        var error = PerformAction(Handle, actionName.Handle);
        return error == AXError.Success;
    }

    private bool HasAncestorRole(AXRoleAttribute role)
    {
        var cursor = Parent as AXUIElement;
        for (var hop = 0; cursor is not null && hop < 32; hop++)
        {
            if (cursor.Role == role) return true;
            cursor = cursor.Parent as AXUIElement;
        }
        return false;
    }

    /// <summary>
    /// Walk up looking for an AXList / AXOutline / AXTable parent, then
    /// set its AXSelectedChildren to [this]. Activates the row on
    /// Electron-web apps (Lark / Feishu) where the row itself has no
    /// Press but the parent list responds to selection.
    /// </summary>
    private bool TrySelectAsListItem()
    {
        // OCCU selectContainingListItem (CUS L761): walk up to the
        // nearest AXList ancestor (NOT AXTable/AXOutline/AXBrowser —
        // those have their own row-selection semantics that
        // setSelectedChildren on the list-shape parent doesn't fire),
        // remember the *direct child* of the list (so we select the
        // list-row itself, not whatever deeply-nested control we
        // started from), then verify AXSelectedChildren is settable
        // before posting. Without the settable gate setSelectedChildren
        // silently returns success but the list never updates its
        // selection, leaving us with a fake-OK on apps that don't
        // support the attribute.
        AXUIElement? cursor = this;
        AXUIElement? directChildOfList = null;
        AXUIElement? list = null;
        for (var hop = 0; cursor is not null && hop < 16; hop++)
        {
            var parent = cursor.Parent as AXUIElement;
            if (parent is null) break;
            if (parent.Role == AXRoleAttribute.AXList)
            {
                list = parent;
                directChildOfList = cursor; // OCCU keeps the direct child as the row to select
                break;
            }
            cursor = parent;
        }
        if (list is null || directChildOfList is null) return false;

        if (!IsSettable(list.Handle, AXAttributeConstants.SelectedChildren.Handle))
            return false;
        cursor = directChildOfList;

        // Build a CFArrayRef containing one CFTypeRef = element (`cursor`).
        // We only need the parent list; cursor is the row that should be selected.
        // ObjCRuntime.NativeHandle implicitly casts to nint via .Handle, but
        // an array of NativeHandle does not, so we project to nint explicitly.
        var listElement = new nint[] { (nint)cursor.Handle };
        var arr = NSArrayFromHandles(listElement);
        if (arr == 0) return false;
        try
        {
            var err = SetAttributeValue(list.Handle, AXAttributeConstants.SelectedChildren.Handle, arr);
            return err == AXError.Success;
        }
        finally
        {
            CFRelease(arr);
        }
    }

    [System.Runtime.InteropServices.LibraryImport(AppServices, EntryPoint = "CFArrayCreate")]
    private static partial nint CFArrayCreate(nint allocator, nint[] values, nint numValues, nint callBacks);

    [System.Runtime.InteropServices.LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
    private static partial void CFRelease(nint cf);

    private static nint NSArrayFromHandles(nint[] handles)
    {
        // kCFTypeArrayCallBacks address is a global symbol; CFArrayCreate
        // accepts NULL callbacks for an array of opaque pointers, which is
        // what AX expects here. Passing 0 is documented as "no retain/release".
        return CFArrayCreate(0, handles, handles.Length, 0);
    }

    public void SetText(string text)
    {
        using var nsText = new NSString(text);
        SetAttributeValue(Handle, AXAttributeConstants.Value.Handle, nsText.Handle);
    }

    public void SendShortcut(KeyboardShortcut shortcut)
    {
        // This is complex on macOS. It usually involves using CoreGraphics CGEventCreateKeyboardEvent
        // to create and post keyboard events to the process that owns the element.
        throw new NotImplementedException();
    }

    /// <summary>
    /// Get the selected text of the visual element.
    /// In case of numeric input fields that return NSNumber, it will be converted to string.
    /// </summary>
    /// <returns></returns>
    public string? GetSelectionText() => GetAttribute<NSObject>(AXAttributeConstants.SelectedText)?.ToString();

    public Task<IVisualElement.ICapturedBitmapData> CaptureAsync(CancellationToken cancellationToken)
    {
        var bounds = BoundingRectangle;
        var rect = new CGRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);

        if (rect.Width < 1f && rect.Height < 1f)
        {
            return Task.FromResult<IVisualElement.ICapturedBitmapData>(CapturedBitmapData.Empty);
        }

        // we use CGSHWCaptureWindowList because it can screenshot minimized windows, which CGWindowListCreateImage can't
        // Use BestResolution to get physical pixel size (matches BackingScaleFactor). 
        // NominalResolution returns 1x logical pixels which causes scaling mismatches when cropping with scale factor.
        using var cgImage = SkyLightInterop.HardwareCaptureWindowList(
            [(uint)NativeWindowHandle],
            SkyLightInterop.CGSWindowCaptureOptions.IgnoreGlobalCLipShape |
            SkyLightInterop.CGSWindowCaptureOptions.BestResolution |
            SkyLightInterop.CGSWindowCaptureOptions.FullSize);

        if (cgImage is null)
        {
            return Task.FromException<IVisualElement.ICapturedBitmapData>(new InvalidOperationException("Failed to capture screen image."));
        }

        var screen = NSScreen.Screens.FirstOrDefault(s => s.Frame.IntersectsWith(rect));
        var scale = screen?.BackingScaleFactor ?? 1.0;

        // cgImage captures the window content starting at (0,0) in Window Local Coordinates.
        // rect contains Screen Coordinates (including Dock/Menu bar offsets).
        // To crop correctly, we must transform rect to Window-Relative coordinates.
        double windowX = 0;
        double windowY = 0;

        // Try to find the parent window to get its screen position.
        var windowRef = GetAttributeAsElement(AXAttributeConstants.Window);
        if (windowRef != null)
        {
            var wRect = windowRef.BoundingRectangle;
            windowX = wRect.X;
            windowY = wRect.Y;
        }
        else if (Role == AXRoleAttribute.AXWindow)
        {
            // Fallback: if we are the window itself
            windowX = rect.X;
            windowY = rect.Y;
        }

        // Check if captured image approximately matches target size (allowing for rounding/shadows).
        // If it matches, we assume full window capture and start at 0,0.
        var targetWidth = rect.Width * scale;
        var isFullWindow = cgImage.Width >= targetWidth - 2 && cgImage.Width <= targetWidth + 100;

        // If full window, offset is 0.
        // If partial (element inside window), offset is (ElementScreenPos - WindowScreenPos).
        var cropX = isFullWindow ? 0 : (rect.X - windowX) * scale;
        var cropY = isFullWindow ? 0 : (rect.Y - windowY) * scale;

        // Clamp invalid values
        if (cropX < 0) cropX = 0;
        if (cropY < 0) cropY = 0;

        using var croppedImage = cgImage.WithImageInRect(
            new CGRect(
                cropX,
                cropY,
                rect.Width * scale,
                rect.Height * scale));

        if (croppedImage is null)
        {
            return Task.FromException<IVisualElement.ICapturedBitmapData>(new InvalidOperationException("Failed to crop image."));
        }

        return Task.FromResult<IVisualElement.ICapturedBitmapData>(new CapturedBitmapData(croppedImage, scale));
    }

    public bool SetAttribute(NSString attributeName, NSObject value)
    {
        var error = SetAttributeValue(Handle, attributeName.Handle, value.Handle);
        return error == AXError.Success;
    }

    public override bool Equals(object? obj)
    {
        return obj is AXUIElement element && CFType.Equal(Handle, element.Handle);
    }

    public override int GetHashCode()
    {
        return CFInterop.CFHash(Handle).GetHashCode();
    }

    #region Helpers

    private T? GetAttribute<T>(NSString attributeName) where T : NSObject
    {
        var error = CopyAttributeValue(Handle, attributeName.Handle, out var value);
        return error == AXError.Success && value != 0 ? Runtime.GetNSObject<T>(value, owns: true) : null;
    }

    private AXUIElement? GetAttributeAsElement(NSString attributeName)
    {
        var error = CopyAttributeValue(Handle, attributeName.Handle, out var value);
        if (error == AXError.Success && value != 0)
        {
            return new AXUIElement(value);
        }

        return null;
    }

    private void PerformAction(NSString actionName)
    {
        var error = PerformAction(Handle, actionName.Handle);
        if (error != AXError.Success)
        {
            throw new InvalidOperationException($"Failed to perform action {actionName}. Error: {error}");
        }
    }

    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    public static AXUIElement SystemWide { get; } = new(CreateSystemWide());

    public AXUIElement? ElementAtPosition(float x, float y)
    {
        var error = CopyElementAtPosition(Handle, x, y, out var element);
        return error == AXError.Success && element != 0 ? new AXUIElement(element) : null;
    }

    public AXUIElement? ElementByAttributeValue(NSString attributeName)
    {
        var error = CopyAttributeValue(Handle, attributeName.Handle, out var value);
        return error == AXError.Success && value != 0 ? new AXUIElement(value) : null;
    }

    public static AXUIElement? ElementFromPid(int pid)
    {
        var handle = CreateApplication(pid);
        return handle != 0 ? new AXUIElement(handle) : null;
    }

    /// <summary>
    /// Gets the AXUIElement corresponding to the specified CGWindowID.
    /// This is a reverse lookup using _AXUIElementGetWindow under the hood.
    /// </summary>
    /// <param name="cgWindowId">The target CGWindowID.</param>
    /// <returns>The matching AXUIElement, or null if not found.</returns>
    public static AXUIElement? ElementFromWindowId(uint cgWindowId)
    {
        if (cgWindowId == 0) return null;

        // 1. Get the owner PID from the CGWindowID using CoreGraphics
        var ownerPid = 0;
        var windowInfoArrayPtr = CGInterop.CGWindowListCopyWindowInfo(CGWindowListOption.IncludingWindow, cgWindowId);

        if (windowInfoArrayPtr != 0)
        {
            // Take ownership of the CFArray returned by Create/Copy rule
            using var windowInfoArray = Runtime.GetNSObject<NSArray>(windowInfoArrayPtr, owns: true);
            if (windowInfoArray is { Count: > 0 })
            {
                using var windowInfo = windowInfoArray.GetItem<NSDictionary>(0);
                using var pidKey = new NSString("kCGWindowOwnerPID");

                if (windowInfo?.ObjectForKey(pidKey) is NSNumber pidNumber)
                {
                    ownerPid = pidNumber.Int32Value;
                }
            }
        }

        if (ownerPid == 0) return null;

        // 2. Create the AXApplication element from the PID
        using var appElement = ElementFromPid(ownerPid);
        if (appElement is null) return null;

        // 3. Get all windows of the application
        // Note: Replace with AXAttributeConstants.Windows if you have it defined.
        using var windowsKey = new NSString("AXWindows");
        using var windows = appElement.GetAttribute<NSArray>(windowsKey);

        if (windows is null) return null;

        // 4. Iterate through the windows and find the matching CGWindowID
        for (nuint i = 0; i < windows.Count; i++)
        {
            var windowElement = FromCopyArray(windows, i);
            if (windowElement is null) continue;

            // Use your existing property which correctly handles the _AXUIElementGetWindow P/Invoke
            if (windowElement.NativeWindowHandle == (nint)cgWindowId)
            {
                // Found it! Return the retained element.
                return windowElement;
            }

            // Not a match: explicitly dispose to release the CFRetain applied in FromCopyArray,
            // avoiding memory leaks during traversal.
            windowElement.Dispose();
        }

        return null;
    }

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCreateSystemWide")]
    private static partial nint CreateSystemWide();

    [LibraryImport(AppServices, EntryPoint = "AXUIElementSetMessagingTimeout")]
    private static partial AXError AXUIElementSetMessagingTimeout(nint element, float timeoutInSeconds);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCopyElementAtPosition")]
    private static partial AXError CopyElementAtPosition(nint application, float x, float y, out nint element);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCopyAttributeValue")]
    private static partial AXError CopyAttributeValue(nint element, nint attribute, out nint value);

    // CoreFoundation path is identical to the one used in MacBrowserUrlReader.
    private const string CoreFoundationLib =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreFoundationLib, EntryPoint = "CFURLGetString")]
    private static partial nint CFURLGetString(nint anUrl);

    [LibraryImport(CoreFoundationLib, EntryPoint = "CFGetTypeID")]
    private static partial nint CFGetTypeID(nint cf);

    [LibraryImport(CoreFoundationLib, EntryPoint = "CFURLGetTypeID")]
    private static partial nint CFURLGetTypeID();

    [LibraryImport(CoreFoundationLib, EntryPoint = "CFStringGetTypeID")]
    private static partial nint CFStringGetTypeID();

    [LibraryImport(AppServices, EntryPoint = "AXUIElementPerformAction")]
    private static partial AXError PerformAction(nint element, nint action);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCopyActionNames")]
    private static partial AXError CopyActionNames(nint element, out nint actions);

    /// <summary>
    /// AXUIElementIsAttributeSettable — OCCU L626 uses this to gate
    /// SetValue/SelectedChildren writes. Reading 'is settable' before
    /// posting prevents fake-success on attributes the element exposes
    /// but doesn't actually accept writes for.
    /// </summary>
    [LibraryImport(AppServices, EntryPoint = "AXUIElementIsAttributeSettable")]
    private static partial AXError IsAttributeSettable(nint element, nint attribute,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.U1)] out bool settable);

    private static bool IsSettable(nint element, nint attribute)
    {
        var err = IsAttributeSettable(element, attribute, out var s);
        return err == AXError.Success && s;
    }

    [LibraryImport(AppServices, EntryPoint = "AXUIElementSetAttributeValue")]
    private static partial AXError SetAttributeValue(nint element, nint attribute, nint value);

    /// <summary>
    /// Private API from https://github.com/lwouis/alt-tab-macos/blob/9761bb91e97646f1c30b43842c4694615e9ad39b/src/api-wrappers/private-apis/ApplicationServices.HIServices.framework.swift#L5
    /// </summary>
    [LibraryImport(AppServices, EntryPoint = "_AXUIElementGetWindow")]
    private static partial AXError GetWindow(nint element, out uint cgWindowId);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementGetPid")]
    private static partial AXError GetPid(nint element, out int pid);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCreateApplication")]
    private static partial nint CreateApplication(int pid);

    #endregion

    private class SiblingAccessorImpl(AXUIElement origin) : VisualElementSiblingAccessor
    {
        private NSArray? _siblings;
        private nint _index;

        protected override void EnsureResources()
        {
            if (_siblings is not null) return;
            if (origin.Parent is not AXUIElement parent) return;

            _siblings = parent.GetAttribute<NSArray>(AXAttributeConstants.Children);
            _index = _siblings is not null ? (nint)_siblings.IndexOf(origin) : nint.MaxValue;
        }

        protected override void ReleaseResources()
        {
            if (_siblings is null) return;

            _siblings.Dispose();
            _siblings = null;
        }

        protected override IEnumerator<IVisualElement> CreateForwardEnumerator()
        {
            if (_siblings is not { } siblings || _index == nint.MaxValue) yield break;

            var count = (nint)siblings.Count;
            for (var i = _index + 1; i < count; i++)
            {
                if (FromCopyArray(siblings, (nuint)i) is { } sibling)
                {
                    yield return sibling;
                }
            }
        }

        protected override IEnumerator<IVisualElement> CreateBackwardEnumerator()
        {
            if (_siblings is not { } siblings || _index == nint.MaxValue) yield break;

            for (var i = _index - 1; i >= 0; i--)
            {
                if (FromCopyArray(siblings, (nuint)i) is { } sibling)
                {
                    yield return sibling;
                }
            }
        }
    }
}