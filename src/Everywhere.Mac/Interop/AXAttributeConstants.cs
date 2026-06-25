namespace Everywhere.Mac.Interop;

/// <summary>
/// Defines common accessibility attribute and action constants.
/// </summary>
public static class AXAttributeConstants
{
    public static readonly NSString Role = new("AXRole");
    public static readonly NSString Subrole = new("AXSubrole");
    public static readonly NSString Parent = new("AXParent");
    public static readonly NSString Children = new("AXChildren");
    public static readonly NSString VisibleChildren = new("AXVisibleChildren");
    public static readonly NSString Title = new("AXTitle");
    public static readonly NSString Description = new("AXDescription");
    public static readonly NSString Value = new("AXValue");
    public static readonly NSString Position = new("AXPosition");
    public static readonly NSString Size = new("AXSize");
    public static readonly NSString Enabled = new("AXEnabled");
    public static readonly NSString Focused = new("AXFocused");
    public static readonly NSString Window = new("AXWindow");
    public static readonly NSString Windows = new("AXWindows");
    public static readonly NSString TopLevelUIElement = new("AXTopLevelUIElement");
    public static readonly NSString FocusedUIElement = new("AXFocusedUIElement");
    public static readonly NSString SelectedText = new("AXSelectedText");
    public static readonly NSString Selected = new("AXSelected");
    public static readonly NSString Hidden = new("AXHidden");
    public static readonly NSString FocusedWindow = new("AXFocusedWindow");

    // Additional attributes can be added here as needed
    public static readonly NSString EnhancedUserInterface = new("AXEnhancedUserInterface");
    public static readonly NSString ManualAccessibility = new("AXManualAccessibility");

    // Hyperlink href — Safari / Chrome / Firefox expose this as a CFURLRef
    // on AXLink nodes.
    public static readonly NSString URL = new("AXURL");

    // Actions — Mac AX exposes element-specific verbs. Different element
    // types respond to different ones (button responds to Press, menu
    // toggles respond to Confirm, files respond to Open, context menus
    // are surfaced via ShowMenu). Calling the wrong verb returns
    // AXError.ActionUnsupported, so we walk a fallback chain.
    // ponytail: Press is special — pre-allocated as a static field
    // its underlying CFString handle came back as 0x0 in production
    // (v0.9.100 perform.log). Cause unknown; possibly AOT init order
    // or trim removal. Allocate lazily via a property; trades one
    // alloc per click chain for a guaranteed live handle. Confirm /
    // Open / ShowMenu are kept as static fields — they work — until
    // proven they hit the same issue.
    private static NSString? _press;
    public static NSString Press => _press ??= new NSString("AXPress");
    public static readonly NSString Confirm = new("AXConfirm");
    public static readonly NSString Open = new("AXOpen");
    public static readonly NSString ShowMenu = new("AXShowMenu");
    public static readonly NSString Increment = new("AXIncrement");
    public static readonly NSString Decrement = new("AXDecrement");

    // Selection / list-item activation. Setting AXSelectedChildren on the
    // parent list to [item] activates the row even when the row itself
    // has no Press action (Electron/web Lark/Feishu rows do this).
    public static readonly NSString SelectedChildren = new("AXSelectedChildren");

    // Extended label / state vocabulary mirrored from OCCU
    // AccessibilitySnapshot.swift. SwiftUI controls / AppKit icon
    // buttons / VoiceOver-friendly views park their human label on
    // AXDescription or AXHelp; many controls expose their state via
    // AXValue (toggles, sliders), AXPlaceholder (text fields),
    // AXIdentifier (test/automation hook), or AXSubrole. We need all
    // of them to produce a tree the agent can match against.
    public static readonly NSString Help = new("AXHelp");
    public static readonly NSString Placeholder = new("AXPlaceholderValue");
    public static readonly NSString Identifier = new("AXIdentifier");
    public static readonly NSString TitleUIElement = new("AXTitleUIElement");
    public static readonly NSString RoleDescription = new("AXRoleDescription");

    // State traits — read as NSNumber/Bool. Used by summarizeTraits.
    public static readonly NSString Expanded = new("AXExpanded");
    public static readonly NSString Required = new("AXRequired");
    public static readonly NSString Edited = new("AXEdited");
    public static readonly NSString MainTrait = new("AXMain");
    public static readonly NSString MinimizedAttr = new("AXMinimized");
    public static readonly NSString GrabbedAttr = new("AXGrabbed");

    // Actions — for meaningful-actions filter in snapshot output.
    public static readonly NSString PickAction = new("AXPick");
    public static readonly NSString CancelAction = new("AXCancel");
    public static readonly NSString DeleteAction = new("AXDelete");
    public static readonly NSString RaiseAction = new("AXRaise");
    public static readonly NSString PressAlt = new("AXScrollToVisible");

    // Child traversal alternates — OCCU childTraversalAttributes
    // (AccessibilitySnapshot.swift L866). AXTable / AXOutline / AXList /
    // AXBrowser surface their rows on AXRows, not on AXChildren;
    // AXList additionally uses AXVisibleChildren (already declared
    // above on L12). AXContents covers some SwiftUI / web container
    // cases.
    public static readonly NSString Rows = new("AXRows");
    public static readonly NSString Contents = new("AXContents");
}