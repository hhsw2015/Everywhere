using System.Reflection;
using Everywhere.Mcp;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tests.Tools;

[TestFixture]
public class ToolCatalogParityTests
{
    private static readonly string[] UpstreamNames =
    [
        "list_apps", "get_app_state", "click", "drag", "type_text",
        "press_key", "scroll", "set_value", "perform_secondary_action",
    ];

    private static readonly string[] EverywhereOnlyNames =
    [
        "get_focused_context", "get_selected_text", "pick_element",
        "expand_element", "get_terminal_output", "screenshot",
    ];

    [Test]
    public void EveryUpstreamToolName_HasMcpServerToolMethodInAssembly()
    {
        var declared = DiscoverDeclaredToolNames();
        Assert.Multiple(() =>
        {
            foreach (var name in UpstreamNames)
            {
                Assert.That(declared, Contains.Item(name), $"Missing upstream tool: {name}");
            }
        });
    }

    [Test]
    public void EveryEverywhereOnlyTool_IsRegistered()
    {
        var declared = DiscoverDeclaredToolNames();
        Assert.Multiple(() =>
        {
            foreach (var name in EverywhereOnlyNames)
            {
                Assert.That(declared, Contains.Item(name), $"Missing Everywhere-only tool: {name}");
            }
        });
    }

    private static IReadOnlyList<string> DiscoverDeclaredToolNames()
    {
        var assembly = typeof(EverywhereMcpServiceExtensions).Assembly;
        var names = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
            {
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is null) continue;

                var name = attr.Name ?? method.Name;
                names.Add(name);
            }
        }
        return names;
    }
}
