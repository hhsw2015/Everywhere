using System.Text.RegularExpressions;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.OpenCli.Analysis;

/// <summary>
/// SPEC §Phase 2 tech-stack detection. Port (concept) of jshookmcp
/// CodeAnalyzer. Regex heuristics over JS URLs + captured bodies —
/// no AST walk yet (SPEC calls for @babel/parser@7 bundled via
/// _fileRoutes; that upgrade path is documented on the module).
/// </summary>
public sealed record TechStackVerdict(
    string? Framework, string? FrameworkVersion, string? UiLib,
    string? StateLib, string? BuildTool, List<string> Hints);

public static class TechStack
{
    private static readonly Regex ReactRuntime = new(
        @"react(-dom)?(?:@|/)([\d.]+)", RegexOptions.IgnoreCase);
    private static readonly Regex NextData = new(
        @"__NEXT_DATA__|window\.__NEXT_", RegexOptions.IgnoreCase);
    private static readonly Regex VueRuntime = new(
        @"vue(?:\.runtime)?(?:\.esm-browser)?", RegexOptions.IgnoreCase);
    private static readonly Regex SvelteRuntime = new(
        @"svelte-kit|/svelte/", RegexOptions.IgnoreCase);
    private static readonly Regex AngularRuntime = new(
        @"@angular/(?:core|platform-browser)", RegexOptions.IgnoreCase);
    private static readonly Regex Redux = new(
        @"@reduxjs/toolkit|redux-devtools|createStore", RegexOptions.IgnoreCase);
    private static readonly Regex Vite = new(
        @"/vite/dist|__vite__", RegexOptions.IgnoreCase);
    private static readonly Regex Webpack = new(
        @"webpackJsonp|__webpack_require__", RegexOptions.IgnoreCase);
    private static readonly Regex Turbopack = new(
        @"__TURBOPACK__", RegexOptions.IgnoreCase);

    public static TechStackVerdict Detect(CaptureSession session)
    {
        string? framework = null, version = null, buildTool = null, stateLib = null, uiLib = null;
        var hints = new List<string>();
        var haystack = new List<string>();
        foreach (var req in session.Network.Requests)
        {
            haystack.Add(req.Url);
            if (session.Network.BodiesByHash.TryGetValue(req.ResponseBodySha256, out var body))
                haystack.Add(body);
        }
        var joined = string.Join('\n', haystack);

        var m = ReactRuntime.Match(joined);
        if (m.Success) { framework = "react"; version = m.Groups[2].Value; hints.Add("react_runtime_url"); }
        if (NextData.IsMatch(joined)) { framework = framework ?? "react"; hints.Add("next_data_present"); buildTool ??= "next.js"; }
        if (framework is null && VueRuntime.IsMatch(joined)) { framework = "vue"; hints.Add("vue_runtime"); }
        if (framework is null && SvelteRuntime.IsMatch(joined)) { framework = "svelte"; hints.Add("svelte_runtime"); }
        if (framework is null && AngularRuntime.IsMatch(joined)) { framework = "angular"; hints.Add("angular_platform"); }

        if (Redux.IsMatch(joined)) { stateLib = "redux"; hints.Add("redux_hint"); }
        if (Vite.IsMatch(joined)) { buildTool = "vite"; hints.Add("vite_hint"); }
        else if (Webpack.IsMatch(joined)) { buildTool ??= "webpack"; hints.Add("webpack_hint"); }
        else if (Turbopack.IsMatch(joined)) { buildTool ??= "turbopack"; hints.Add("turbopack_hint"); }

        return new TechStackVerdict(framework, version, uiLib, stateLib, buildTool, hints);
    }
}
