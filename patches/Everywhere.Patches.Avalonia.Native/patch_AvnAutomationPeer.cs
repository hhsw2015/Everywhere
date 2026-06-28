#nullable disable
#if MACOS

// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global
using Avalonia.Native.Interop;
using MonoMod;

namespace Everywhere.Patches.Avalonia.Native;

[MonoModPatch("Avalonia.Native.AvnAutomationPeer")]
internal class patch_AvnAutomationPeer : IAvnAutomationPeer
{
    [MonoModReplace]
    public void SetNode(IAvnAutomationNode peer)
    {
        // Skip original method
    }

    [MonoModIgnore]
    public int HasKeyboardFocus()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsContentElement()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsControlElement()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsEnabled()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsKeyboardFocusable()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void SetFocus()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int ShowContextMenu()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsInteropPeer()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IntPtr InteropPeer_GetNativeControlHandle()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsRootProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnWindowBase RootProvider_GetWindow()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnAutomationPeer RootProvider_GetFocus()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnAutomationPeer RootProvider_GetPeerFromPoint(AvnPoint point)
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsEmbeddedRootProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnAutomationPeer EmbeddedRootProvider_GetFocus()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnAutomationPeer EmbeddedRootProvider_GetPeerFromPoint(AvnPoint point)
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsExpandCollapseProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int ExpandCollapseProvider_GetIsExpanded()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int ExpandCollapseProvider_GetShowsMenu()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void ExpandCollapseProvider_Expand()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void ExpandCollapseProvider_Collapse()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsInvokeProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void InvokeProvider_Invoke()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsRangeValueProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public double RangeValueProvider_GetValue()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public double RangeValueProvider_GetMinimum()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public double RangeValueProvider_GetMaximum()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public double RangeValueProvider_GetSmallChange()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public double RangeValueProvider_GetLargeChange()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void RangeValueProvider_SetValue(double value)
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsSelectionItemProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int SelectionItemProvider_IsSelected()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsToggleProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int ToggleProvider_GetToggleState()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void ToggleProvider_Toggle()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsValueProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnString ValueProvider_GetValue()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void ValueProvider_SetValue(string value)
    {
        throw new NotSupportedException();
    }

    // ---- Avalonia 12 additions -------------------------------------
    // These members were added to IAvnAutomationPeer in Avalonia 12;
    // the patch must implement them so the assembly type-checks. We
    // mirror the original implementations' "delegate to the underlying
    // peer" shape with throw-stubs since this patch is replaced at
    // load-time by Avalonia's real method body via MonoMod.

    [MonoModIgnore]
    public void BringIntoView()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int RangeValueProvider_IsReadOnly()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void SelectionItemProvider_Select()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void SelectionItemProvider_AddToSelection()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void SelectionItemProvider_RemoveFromSelection()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnAutomationPeer ScrollProvider_GetHorizontalScrollBar()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnAutomationPeer ScrollProvider_GetVerticalScrollBar()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int ValueProvider_IsReadOnly()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore] public IAvnAutomationPeer TemplatedParent { get; set; }
    [MonoModIgnore] public IAvnString PlaceholderText { get; set; }

    [MonoModIgnore] public IAvnAutomationNode Node { get; set; }
    [MonoModIgnore] public IAvnString AcceleratorKey { get; set; }
    [MonoModIgnore] public IAvnString AccessKey { get; set; }
    [MonoModIgnore] public AvnAutomationControlType AutomationControlType { get; set; }
    [MonoModIgnore] public IAvnString AutomationId { get; set; }
    [MonoModIgnore] public AvnRect BoundingRectangle { get; set; }
    [MonoModIgnore] public IAvnAutomationPeerArray Children { get; set; }
    [MonoModIgnore] public IAvnString ClassName { get; set; }
    [MonoModIgnore] public IAvnAutomationPeer LabeledBy { get; set; }
    [MonoModIgnore] public IAvnString Name { get; set; }
    [MonoModIgnore] public IAvnAutomationPeer Parent { get; set; }
    [MonoModIgnore] public IAvnAutomationPeer VisualRoot { get; set; }
    [MonoModIgnore] public IAvnAutomationPeer RootPeer { get; set; }
    [MonoModIgnore] public IAvnString HelpText { get; set; }
    [MonoModIgnore] public AvnLandmarkType LandmarkType { get; set; }
    [MonoModIgnore] public int HeadingLevel { get; set; }
    [MonoModIgnore] public AvnLiveSetting LiveSetting { get; set; }

    [MonoModIgnore]
    public void Dispose() { }
}

#endif