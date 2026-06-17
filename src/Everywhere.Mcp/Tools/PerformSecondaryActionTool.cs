using System.ComponentModel;
using Avalonia.Input;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class PerformSecondaryActionTool
{
    [McpServerTool(Name = "perform_secondary_action")]
    [Description(
        "Invoke a secondary action on an indexed element. Supported action values: " +
        "\"press\" (default invoke; same as click element_index), " +
        "\"focus\" (move keyboard focus to the element), " +
        "\"context_menu\" (open right-click menu via the Menu/Apps key shortcut). " +
        "Returns an error if the action is not implemented for this element type.")]
    public static CallToolResult PerformSecondaryAction(
        string app,
        string element_index,
        string action,
        SessionStore sessions)
    {
        if (string.IsNullOrEmpty(element_index)) return ToolErrors.ParameterRequired("element_index");
        if (string.IsNullOrEmpty(action)) return ToolErrors.ParameterRequired("action");

        var (error, element) = ElementResolver.Resolve(sessions, element_index, appHint: app);
        if (error is not null) return error;

        try
        {
            switch (action.ToLowerInvariant())
            {
                case "press":
                case "invoke":
                case "click":
                    return ElementClickDispatcher.Click(element!);

                case "focus":
                    return ToolErrors.Error(
                        "Direct focus action is not supported via a11y. Use click(element_index) " +
                        "on the target — most controls take focus when clicked.");

                case "context_menu":
                case "right_click":
                    element!.SendShortcut(new KeyboardShortcut(Key.Apps, KeyModifiers.None));
                    return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };

                default:
                    return ToolErrors.Error(
                        $"Unsupported action '{action}'. Try: press, focus, context_menu.");
            }
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, $"perform_secondary_action({action})");
        }
    }
}
