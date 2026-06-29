using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// Dispatches a native [McpServerTool] method by name. Used by the
/// <see cref="MetaTools"/>.<c>call_tool</c> meta when the agent wants to
/// invoke a tool that the gate hides from <c>tools/list</c>.
///
/// The MCP SDK's <c>McpServerToolCollection</c> isn't exposed at the public
/// surface, so we re-do the discovery ourselves: scan the Everywhere.Mcp
/// assembly for classes carrying <c>[McpServerToolType]</c>, index every
/// method whose <c>[McpServerTool]</c> attribute names a tool. At dispatch
/// time, resolve the host class via DI (or activate it on the fly), bind
/// argument-name params from the JSON object, and resolve everything else
/// from DI by parameter type. This mirrors how the SDK builds its own tool
/// runners — without that, a host method like
/// <c>list_apps(IServiceProvider services)</c> or
/// <c>get_idle_time(IIdleTimeReader reader)</c> would receive null
/// because the agent doesn't pass services through call_tool's
/// arguments_json.
/// </summary>
internal static class NativeToolDispatcher
{
    private static readonly Lazy<IReadOnlyDictionary<string, MethodInfo>> Index =
        new(() => BuildIndex(typeof(NativeToolDispatcher).Assembly));

    public static bool TryGetMethod(string toolName, out MethodInfo? method)
    {
        method = Index.Value.TryGetValue(toolName, out var m) ? m : null;
        return method is not null;
    }

    public static async Task<string> InvokeAsync(
        IServiceProvider services,
        string toolName,
        JsonObject? arguments,
        CancellationToken ct)
    {
        if (!TryGetMethod(toolName, out var method) || method is null)
        {
            return ErrorEnvelope($"unknown native tool: {toolName}");
        }

        // Resolve the [McpServerToolType] host instance. Prefer DI singleton
        // (matches the SDK's normal path for our existing instance hosts);
        // fall back to ActivatorUtilities so future tool classes that the
        // user forgets to register still work via call_tool.
        object? target = null;
        if (!method.IsStatic)
        {
            var hostType = method.DeclaringType
                ?? throw new InvalidOperationException(
                    $"native tool {toolName}: method has no declaring type");
            target = services.GetService(hostType);
            if (target is null)
            {
                try
                {
                    target = ActivatorUtilities.CreateInstance(services, hostType);
                }
                catch (Exception ex)
                {
                    return ErrorEnvelope(
                        $"native tool {toolName}: cannot construct {hostType.Name} ({ex.Message})");
                }
            }
        }

        // Bind parameters by name (from arguments_json) and by type (from DI).
        // Cancellation tokens are forwarded explicitly. This matches the SDK's
        // own conventions: arguments fill agent-supplied params; DI fills
        // service params; neither matches → fall back to default.
        var parameters = method.GetParameters();
        var call = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];

            if (p.ParameterType == typeof(CancellationToken))
            {
                call[i] = ct;
                continue;
            }

            if (arguments is not null
                && p.Name is not null
                && arguments.TryGetPropertyValue(p.Name, out var node)
                && node is not null)
            {
                try
                {
                    call[i] = JsonSerializer.Deserialize(node.ToJsonString(), p.ParameterType);
                    continue;
                }
                catch (Exception ex)
                {
                    return ErrorEnvelope(
                        $"native tool {toolName}: failed to bind '{p.Name}' to {p.ParameterType.Name}: {ex.Message}");
                }
            }

            // Service-typed params: resolve from DI. This is what makes
            // `list_apps(IServiceProvider)`, `get_idle_time(IIdleTimeReader)`,
            // `pick_element(IVisualElementContext, SessionStore, ...)` work.
            // Reference / interface types only — value types fall through
            // to default.
            if (p.ParameterType.IsClass || p.ParameterType.IsInterface)
            {
                var resolved = services.GetService(p.ParameterType);
                if (resolved is not null)
                {
                    call[i] = resolved;
                    continue;
                }
            }

            call[i] = p.HasDefaultValue
                ? p.DefaultValue
                : (p.ParameterType.IsValueType
                    ? Activator.CreateInstance(p.ParameterType)
                    : null);
        }

        // Invoke. Tools may return sync, Task, Task<T>, ValueTask,
        // ValueTask<T>, plus CallToolResult / arbitrary POCOs / strings.
        // Normalise everything to a JSON string so call_tool gives the
        // agent a uniform envelope.
        object? raw;
        try
        {
            raw = method.Invoke(target, call);
        }
        catch (TargetInvocationException tie)
        {
            return ErrorEnvelope(tie.InnerException?.Message ?? tie.Message);
        }
        catch (Exception ex)
        {
            return ErrorEnvelope(ex.Message);
        }

        var awaited = await UnwrapAwaitable(raw).ConfigureAwait(false);
        return SerialiseResult(awaited);
    }

    /// <summary>
    /// Walks the awaitable shapes the SDK supports — sync, Task,
    /// Task&lt;T&gt;, ValueTask, ValueTask&lt;T&gt; — and returns the
    /// final unwrapped value (or null for void awaits).
    /// </summary>
    private static async Task<object?> UnwrapAwaitable(object? value)
    {
        if (value is null) return null;

        var t = value.GetType();

        if (value is Task task)
        {
            await task.ConfigureAwait(false);
            // Task<T>: walk up the type chain looking for the generic
            // Task<> definition (subclasses of Task<> like
            // Task<MyResult> still have BaseType chains terminating at
            // object — we want the closed Task<TResult> in between).
            for (var probe = t; probe is not null && probe != typeof(object); probe = probe.BaseType)
            {
                if (probe.IsGenericType && probe.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    return probe.GetProperty("Result")?.GetValue(task);
                }
            }
            return null;
        }

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            // Convert ValueTask<T> → Task<T> via AsTask, then re-enter.
            var asTask = t.GetMethod("AsTask")!.Invoke(value, null);
            return await UnwrapAwaitable(asTask).ConfigureAwait(false);
        }
        if (t == typeof(ValueTask))
        {
            await ((ValueTask)value).ConfigureAwait(false);
            return null;
        }

        return value;
    }

    /// <summary>
    /// Serialise a tool's return value. Native tools commonly return
    /// <see cref="CallToolResult"/> (with its TextContentBlock list); for
    /// those, lift the textual content out so call_tool's caller sees the
    /// same payload they would have seen calling the tool directly,
    /// rather than a CallToolResult envelope. Other shapes serialise as
    /// the SDK's normal default.
    /// </summary>
    private static string SerialiseResult(object? awaited)
    {
        switch (awaited)
        {
            case null:
                return "{}";
            case string s:
                return s;
            case CallToolResult ctr:
            {
                // Concatenate text blocks (almost always exactly one).
                // Fall back to empty object when there are no text blocks.
                var sb = new System.Text.StringBuilder();
                foreach (var block in ctr.Content)
                {
                    if (block is TextContentBlock tcb && tcb.Text is { Length: > 0 })
                    {
                        sb.Append(tcb.Text);
                    }
                }
                return sb.Length > 0 ? sb.ToString() : "{}";
            }
            default:
                try
                {
                    return JsonSerializer.Serialize(awaited);
                }
                catch (Exception ex)
                {
                    return ErrorEnvelope($"result not JSON-serialisable: {ex.Message}");
                }
        }
    }

    private static IReadOnlyDictionary<string, MethodInfo> BuildIndex(Assembly assembly)
    {
        var map = new ConcurrentDictionary<string, MethodInfo>(StringComparer.Ordinal);

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException rtle)
        {
            // Some types failed to load (e.g. platform-specific tool that
            // depends on a missing native dep). Index whatever did load —
            // skipping the broken ones is strictly better than throwing.
            types = rtle.Types.Where(t => t is not null).ToArray()!;
        }

        foreach (var type in types)
        {
            if (type is null) continue;
            McpServerToolTypeAttribute? typeAttr;
            try
            {
                typeAttr = type.GetCustomAttribute<McpServerToolTypeAttribute>();
            }
            catch
            {
                continue;
            }
            if (typeAttr is null) continue;

            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                var attr = m.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is null) continue;
                var name = attr.Name;
                if (string.IsNullOrEmpty(name)) continue;

                // First wins. Two methods registering the same name is a
                // user bug; we keep the first for determinism. SDK would
                // reject this at build time; we don't have that hook from
                // out here, so silently keep first instead of throwing.
                map.TryAdd(name, m);
            }
        }
        return map;
    }

    private static string ErrorEnvelope(string message) =>
        new JsonObject
        {
            ["ok"] = false,
            ["error"] = message,
        }.ToJsonString();
}
