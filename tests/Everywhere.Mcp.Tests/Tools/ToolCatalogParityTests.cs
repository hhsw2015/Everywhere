using System.Reflection;
using Everywhere.Mcp;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tests.Tools;

[TestFixture]
public class ToolCatalogParityTests
{
    // OCCU-parity surface: macOS automation tools that mirror the upstream
    // open-codex-computer-use Swift kit method-for-method.
    private static readonly string[] UpstreamNames =
    [
        "list_apps", "get_app_state", "click", "drag", "type_text",
        "press_key", "scroll", "set_value", "perform_secondary_action",
    ];

    // Everywhere-only perception + clipboard + doc-reader + annotation +
    // meta surface. Keep alphabetised inside each group for easy diffing.
    private static readonly string[] EverywhereOnlyNames =
    [
        // perception
        "get_focused_context", "get_selected_text", "pick_element",
        "expand_element", "get_terminal_output", "screenshot", "read_pick",
        "read_whiteboard", "read_whiteboard_image",
        "get_app_context",
        "get_clipboard", "get_idle_time", "get_browser_url",
        "get_finder_selection", "get_browser_tabs",
        // clipboard (SPEC parity, four aliases)
        "clipboard_read", "clipboard_paste", "clipboard_write", "clipboard_copy",
        // document readers
        "doc_read_pdf", "doc_read_docx", "doc_read_xlsx", "doc_read_pptx",
        "doc_read_epub", "doc_read_html", "doc_read_txt",
        // annotations (user-left ✓ marks for the agent)
        "add_annotation", "read_annotations", "clear_annotations",
        // composition
        "batch",
        // meta (long-tail discovery for tools hidden by CoreToolGate)
        "list_more_tools", "call_tool",
        // web (routed through user-configured search provider + Jina reader)
        "web_search", "web_fetch_url",
        // opencli surface
        "opencli_list", "opencli_describe", "opencli_run",
        // open-connector surface (docs/specs/everywhere-connector.md)
        "connector_list", "connector_describe", "connector_run",
        "connector_connect", "connector_disconnect", "connector_list_connections",
        // self-expanding platform (docs/specs/everywhere-self-expanding.md)
        // Phase 1 observation
        "capture_start", "capture_stop", "capture_current", "capture_export",
        "browser_captcha_present", "page_extract_by_rule", "page_save_extraction_rule",
        // Phase 3 memory
        "memory_read", "memory_read_endpoint", "memory_write_endpoint",
        "memory_write_field_map", "memory_write_verify_fixture",
        "memory_append_note", "memory_freshness", "memory_snapshot",
        // Phase 4 gates
        "strategy_note_write", "strategy_note_get", "adapter_lint",
        // Phase 2 analysis
        "web_verdict_score", "web_signature_scheme", "web_techstack",
        "web_js_search", "web_crypto_scan",
        "web_sourcemap_list_candidates", "web_sourcemap_resolve",
        "web_js_fetch_same_origin",
        // Phase 5 generator
        "adapter_scaffold", "adapter_save", "adapter_verify",
        "adapter_list_local", "adapter_drift_check", "adapter_delete_local",
        "adapter_regenerate", "opendia_smoke_check",
        // Phase 6 tier
        "search_tools", "activate_domain", "list_domains",
        // Phase 6.5 adapter catalog search
        "search_adapters",
        // OpenDia + Cebian merge Phase 4 chat bus
        // (docs/specs/opendia-cebian-merge.md §Phase 4). Tools proxy chat
        // state to/from the OpenDia extension's sidepanel; gated under the
        // "chat" domain in TierGate.
        "chat_list", "chat_read", "chat_send",
        "chat_create", "chat_delete", "chat_subscribe",
    ];

    [Test]
    public void DeclaredTools_MatchExpectedCatalog_AndAreUnique()
    {
        var declared = DiscoverDeclaredToolNames();
        var expected = UpstreamNames.Concat(EverywhereOnlyNames).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(declared, Is.Unique, "Tool names must be globally unique across [McpServerToolType] classes.");
            Assert.That(declared, Is.EquivalentTo(expected),
                "Tool catalog drifted: every upstream + Everywhere-only tool must be declared, and no extras.");
        });
    }

    [Test]
    public void EveryToolMethod_DeclaresAnExplicitName()
    {
        var assembly = typeof(EverywhereMcpServiceExtensions).Assembly;
        var missing = LoadableTypes(assembly)
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is { } a && string.IsNullOrEmpty(a.Name))
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .ToArray();

        Assert.That(missing, Is.Empty, "Every [McpServerTool] must declare an explicit Name = \"…\".");
    }

    private static IReadOnlyList<string> DiscoverDeclaredToolNames()
    {
        var assembly = typeof(EverywhereMcpServiceExtensions).Assembly;
        var names = new List<string>();
        foreach (var type in LoadableTypes(assembly))
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr?.Name is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
        }
        return names;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
