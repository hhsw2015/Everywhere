using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// On a Snapshot-Context hotkey press, captures a MINIMAL pointer to whatever
/// the user was looking at — focused app key, window title, browser URL,
/// selected text — and writes it atomically to a well-known JSON file. The
/// Claude Code UserPromptSubmit hook reads + deletes the file the next time
/// the user hits Enter.
/// </summary>
public sealed class ContextStashWriter
{
    /// <summary>
    /// Bumped whenever the on-disk schema changes incompatibly. Newer hooks /
    /// older payloads can use this to refuse stale formats.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly string StashPath = StashPaths.ContextStash();
    private static readonly char[] ControlCharsToStrip = ['\0', '\n', '\r', '\t', '\v', '\f', '\b'];

    private readonly IVisualElementContext _context;
    private readonly IBrowserUrlReader _browserUrl;
    private readonly SelectionCache _selectionCache;
    private readonly ILogger<ContextStashWriter> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ContextStashWriter(
        IVisualElementContext context,
        IBrowserUrlReader browserUrl,
        SelectionCache selectionCache,
        ILogger<ContextStashWriter> logger)
    {
        _context = context;
        _browserUrl = browserUrl;
        _selectionCache = selectionCache;
        _logger = logger;
    }

    public async Task CaptureAsync(CancellationToken cancellationToken = default)
    {
        // Single-flight: rapid hotkey double-presses must serialise on the file
        // write, otherwise both invocations race on the same .tmp path.
        if (!await _writeLock.WaitAsync(0, cancellationToken))
        {
            _logger.LogDebug("Context capture already in progress; ignoring second hotkey press.");
            return;
        }

        try
        {
            var focused = _context.FocusedElement;
            var topLevel = WalkToTopLevel(focused);
            if (topLevel is null)
            {
                topLevel = focused;
                _logger.LogDebug("WalkToTopLevel returned null; using focused element directly.");
            }
            var pid = topLevel?.ProcessId ?? 0;
            var appKey = pid > 0 ? AppKey.FromProcessId(pid) : null;

            string? url = null;
            if (pid > 0)
            {
                try
                {
                    url = _browserUrl.GetUrl(pid);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "browser url resolution failed for pid {Pid}", pid);
                }
            }

            string? selectionText = null;
            string? selectionApp = null;
            if (_selectionCache.GetFresh() is { } cached)
            {
                selectionText = cached.Text;
                selectionApp = cached.AppKey;
            }
            else if (focused?.GetSelectionText() is { Length: > 0 } liveSel)
            {
                selectionText = liveSel;
                selectionApp = appKey;
            }

            var payload = new ContextSnapshotPayload(
                SchemaVersion: CurrentSchemaVersion,
                CapturedAtUtc: DateTimeOffset.UtcNow,
                App: appKey,
                ProcessId: pid > 0 ? pid : null,
                WindowTitle: topLevel?.Name,
                Url: url,
                SelectedText: selectionText,
                SelectedApp: selectionApp);

            await WriteAtomicAsync(FormatForHook(payload), cancellationToken);
            _logger.LogInformation("Context stash captured for {App} ({Title}).", appKey, topLevel?.Name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Context stash capture failed");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// What lands in the stash file: a single human-readable [everywhere-ctx]
    /// line for trivial parsing plus the structured JSON below it. Untrusted
    /// substrings (window title, selection) are aggressively sanitised:
    /// control bytes stripped, brackets neutralised, length capped on grapheme
    /// clusters (no surrogate splits).
    /// </summary>
    private static string FormatForHook(ContextSnapshotPayload p)
    {
        var sb = new StringBuilder();
        sb.Append("[everywhere-ctx] ");
        if (p.App is { Length: > 0 }) sb.Append("app=").Append(SanitiseTokenValue(p.App, 64)).Append(' ');
        if (p.WindowTitle is { Length: > 0 })
        {
            sb.Append("title=\"").Append(SanitiseUserText(p.WindowTitle, 80)).Append("\" ");
        }
        if (p.Url is { Length: > 0 })
        {
            sb.Append("url=").Append(SanitiseTokenValue(p.Url, 256)).Append(' ');
        }
        if (p.SelectedText is { Length: > 0 })
        {
            sb.Append("selection=\"").Append(SanitiseUserText(p.SelectedText, 200)).Append('"');
        }
        sb.Append('\n');

        sb.Append("[everywhere-ctx-json] ");
        sb.Append(JsonSerializer.Serialize(p, ContextSnapshotPayload.SerializerOptions));
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Truncate to <paramref name="maxChars"/> grapheme clusters (so we never
    /// split a surrogate pair / emoji); strip control chars; neutralise the
    /// `[…]` brackets that bound the prompt-injection envelope.
    /// </summary>
    private static string SanitiseUserText(string s, int maxChars)
    {
        var truncated = TruncateGraphemes(s, maxChars);
        var buf = new StringBuilder(truncated.Length);
        foreach (var c in truncated)
        {
            if (Array.IndexOf(ControlCharsToStrip, c) >= 0) { buf.Append(' '); continue; }
            if (char.IsControl(c)) { buf.Append(' '); continue; }
            // Neutralise [ ] " — these would let attacker-controlled text close
            // our envelope and inject a fake [everywhere-ctx] line.
            switch (c)
            {
                case '[': buf.Append('('); break;
                case ']': buf.Append(')'); break;
                case '"': buf.Append('\''); break;
                default:  buf.Append(c);   break;
            }
        }
        return buf.ToString();
    }

    /// <summary>
    /// For values that should look "tokeny" (no spaces, no brackets) — app key,
    /// URL. URLs we keep as-is for utility but still strip control chars and
    /// the bracket pair.
    /// </summary>
    private static string SanitiseTokenValue(string s, int maxChars)
    {
        var truncated = TruncateGraphemes(s, maxChars);
        var buf = new StringBuilder(truncated.Length);
        foreach (var c in truncated)
        {
            if (char.IsControl(c) || c == ' ' || c == '\t') continue;
            if (c == '[' || c == ']') continue;
            buf.Append(c);
        }
        return buf.ToString();
    }

    private static string TruncateGraphemes(string s, int maxGraphemes)
    {
        if (string.IsNullOrEmpty(s) || maxGraphemes <= 0) return string.Empty;
        var enumerator = StringInfo.GetTextElementEnumerator(s);
        var sb = new StringBuilder();
        var count = 0;
        var truncated = false;
        while (enumerator.MoveNext())
        {
            if (count >= maxGraphemes) { truncated = true; break; }
            sb.Append((string)enumerator.Current);
            count++;
        }
        if (truncated) sb.Append('…');
        return sb.ToString();
    }

    private static async Task WriteAtomicAsync(string content, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(StashPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = StashPath + ".tmp";
        await File.WriteAllTextAsync(tmp, content, cancellationToken);

        // Tighten file permissions on POSIX so other local users can't read the
        // selection / URL (which may carry tokens). 0600 = owner read/write.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var perms = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                File.SetUnixFileMode(tmp, perms);
            }
            catch { /* best-effort */ }
        }

        File.Move(tmp, StashPath, overwrite: true);
    }

    private static IVisualElement? WalkToTopLevel(IVisualElement? element)
    {
        var current = element;
        for (var i = 0; current != null && i < 32; i++)
        {
            if (current.Type == VisualElementType.TopLevel) return current;
            current = current.Parent;
        }
        return null;
    }
}

internal sealed record ContextSnapshotPayload(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("captured_at_utc")] DateTimeOffset CapturedAtUtc,
    [property: JsonPropertyName("app")] string? App,
    [property: JsonPropertyName("process_id")] int? ProcessId,
    [property: JsonPropertyName("window_title")] string? WindowTitle,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("selected_text")] string? SelectedText,
    [property: JsonPropertyName("selected_app")] string? SelectedApp)
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
