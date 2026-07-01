using System.Text.RegularExpressions;

namespace Everywhere.Mcp.OpenCli.Gates;

/// <summary>
/// Lightweight lexical scanner used by G3..G6 + G8. SPEC calls for
/// Acorn AST for tight fidelity; this ponytail implementation walks
/// tokens (strings/comments stripped) with anchored regexes covering
/// the patterns the SPEC enumerates. Upgrade path: bundle acorn via
/// ModuleLoader._fileRoutes and delegate parse.
/// </summary>
public static class AdapterSourceScan
{
    /// <summary>Strip line + block comments and string literals, returning code positions preserved.</summary>
    public static string StripCommentsAndStrings(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            // Line comment
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                var end = source.IndexOf('\n', i);
                if (end < 0) break;
                sb.Append(' ', end - i);
                i = end;
                continue;
            }
            // Block comment
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) break;
                for (var j = i; j < end + 2; j++) sb.Append(source[j] == '\n' ? '\n' : ' ');
                i = end + 2;
                continue;
            }
            // String literal: single quotes, double quotes, backticks
            if (c == '\'' || c == '"' || c == '`')
            {
                var quote = c;
                sb.Append(quote);
                i++;
                while (i < source.Length && source[i] != quote)
                {
                    if (source[i] == '\\' && i + 1 < source.Length) { sb.Append(' '); sb.Append(' '); i += 2; continue; }
                    sb.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < source.Length) { sb.Append(quote); i++; }
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    public static IEnumerable<(int Line, string Content)> Lines(string source)
    {
        var l = 0;
        foreach (var line in source.Replace("\r\n", "\n").Split('\n'))
        {
            l++;
            yield return (l, line);
        }
    }

    /// <summary>Extract the `browser: true|false` value from cli({...}). Returns null when missing.</summary>
    public static bool? DeclaredBrowser(string source)
    {
        var m = Regex.Match(source, @"\bbrowser\s*:\s*(true|false)");
        if (!m.Success) return null;
        return m.Groups[1].Value == "true";
    }

    /// <summary>Detect the function signature form: `async (page, args)` vs `async (args)`.</summary>
    public static string? SignatureForm(string source)
    {
        var m = Regex.Match(source, @"async\s*\(([^)]*)\)\s*=>");
        if (!m.Success) return null;
        var raw = m.Groups[1].Value.Trim();
        if (string.IsNullOrEmpty(raw)) return "async ()";
        var args = raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        if (args.Length == 2 && args[0] == "page") return "async (page, args)";
        if (args.Length == 1) return "async (args)";
        return $"async ({string.Join(", ", args)})";
    }

    // POST/PUT/DELETE/PATCH keywords in various common shapes — used by G7.
    // The literal-quote form catches the common case; the identifier-value
    // form (`method: variable`) trips the guard even when the caller tried
    // to launder the verb through concatenation, since we can't statically
    // prove the variable isn't a mutating verb.
    private static readonly Regex MutationLiteralMethod = new(
        @"method\s*:\s*['""](POST|PUT|DELETE|PATCH)['""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MethodDynamicValue = new(
        @"method\s*:\s*(?!['""](GET|HEAD|OPTIONS)['""])[^,}\n]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool HasMutationCall(string source)
    {
        if (MutationLiteralMethod.IsMatch(source)) return true;
        // Dynamic method value that isn't a bare literal 'GET'/'HEAD'/'OPTIONS'
        // is treated as potentially-mutating (cautious side of §2.6).
        return MethodDynamicValue.IsMatch(source);
    }
}
