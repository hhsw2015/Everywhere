using Everywhere.Interop;
using ModelContextProtocol.Protocol;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// Routes "click this element" to the right semantic action based on the element's type.
/// Calling <see cref="IVisualElement.Invoke"/> on a slider / text field / list item often
/// returns a silent no-op while reporting success — surface that as a typed error instead
/// of fake "ok" so the agent can switch tools.
/// </summary>
internal static class ElementClickDispatcher
{
    public static CallToolResult Click(IVisualElement element)
    {
        try
        {
            switch (element.Type)
            {
                case VisualElementType.Button:
                case VisualElementType.Hyperlink:
                case VisualElementType.MenuItem:
                case VisualElementType.HeaderItem:
                case VisualElementType.TabItem:
                case VisualElementType.ListViewItem:
                case VisualElementType.TreeViewItem:
                case VisualElementType.DataGridItem:
                case VisualElementType.RadioButton:
                case VisualElementType.CheckBox:
                case VisualElementType.Image:
                case VisualElementType.Header:
                case VisualElementType.Label:
                case VisualElementType.Unknown:
                case VisualElementType.Panel:
                    element.Invoke();
                    return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };

                case VisualElementType.TextEdit:
                case VisualElementType.Document:
                    return ToolErrors.Error(
                        $"Cannot click element of type '{element.Type}'. Use set_value to change its text, " +
                        "or use coordinate click(x,y) to put the caret at a specific position.");

                case VisualElementType.Slider:
                case VisualElementType.Spinner:
                    return ToolErrors.Error(
                        $"Cannot click element of type '{element.Type}'. Use set_value with a numeric value.");

                case VisualElementType.ComboBox:
                    element.Invoke();
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = "ok" }],
                    };

                default:
                    element.Invoke();
                    return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
            }
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "invoke element");
        }
    }
}
