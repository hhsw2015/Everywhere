using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Mcp.Input;
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
    private readonly PickStash _pickStash;
    private readonly IAppActivator _appActivator;
    private readonly IInputSimulator _input;
    private readonly Settings _settings;
    private readonly ILogger<ContextStashWriter> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ContextStashWriter(
        IVisualElementContext context,
        IBrowserUrlReader browserUrl,
        SelectionCache selectionCache,
        PickStash pickStash,
        IAppActivator appActivator,
        IInputSimulator input,
        Settings settings,
        ILogger<ContextStashWriter> logger)
    {
        _context = context;
        _browserUrl = browserUrl;
        _selectionCache = selectionCache;
        _pickStash = pickStash;
        _appActivator = appActivator;
        _input = input;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Manual escape hatch bound to the ClearContextStash hotkey. Wipes the
    /// on-disk stash, any orphaned <c>.tmp</c>, AND any fresh pin so the
    /// next prompt to a Claude Code hook is NOT decorated. Idempotent — safe
    /// to call when nothing is staged.
    /// </summary>
    public void ClearStash()
    {
        try { _pickStash.Clear(); } catch { }
        foreach (var path in new[] { StashPath, StashPath + ".tmp" })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { _logger.LogDebug(ex, "Clear stash: failed to delete {Path}", path); }
        }
        _logger.LogInformation("Context stash cleared by user.");
    }

    public Task CaptureAsync(CancellationToken cancellationToken = default) => CaptureCoreAsync(seed: null, cancellationToken);

    /// <summary>
    /// Capture context anchored to a specific element instead of whatever
    /// happens to be focused right now. Used by pin-driven auto-capture: by
    /// the time the Pinned event fires, focus has already returned to the
    /// chat window, so reading FocusedElement would yield the chat window
    /// (or nothing).
    /// </summary>
    public Task CaptureAsync(IVisualElement seed, CancellationToken cancellationToken = default) => CaptureCoreAsync(seed, cancellationToken);

    private async Task CaptureCoreAsync(IVisualElement? seed, CancellationToken cancellationToken)
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
            var focused = seed ?? _context.FocusedElement;
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

            // Only label pin_pending when this capture was actually triggered
            // by a pin (seed != null). The stale-pin slot from a prior
            // AgentPickElement can still be HasFreshPin=true for up to 5 min,
            // but the user's current SnapshotContext press is unrelated to it
            // — claiming pin_pending would point the LLM at the wrong element.
            var pinPending = seed is not null && _pickStash.HasFreshPin;

            // If we ended up with nothing useful — no app, no title, no url, no
            // selection, AND no fresh pin waiting — refuse to write. An empty
            // stash makes the hook inject a bare "[everywhere-ctx]" envelope,
            // which is worse than not injecting at all.
            if (string.IsNullOrEmpty(appKey)
                && string.IsNullOrEmpty(topLevel?.Name)
                && string.IsNullOrEmpty(url)
                && string.IsNullOrEmpty(selectionText)
                && !pinPending)
            {
                _logger.LogDebug("Context stash capture produced no usable data; skipping write.");
                return;
            }

            var payload = new ContextSnapshotPayload(
                SchemaVersion: CurrentSchemaVersion,
                CapturedAtUtc: DateTimeOffset.UtcNow,
                App: appKey,
                ProcessId: pid > 0 ? pid : null,
                WindowTitle: topLevel?.Name,
                Url: url,
                SelectedText: selectionText,
                SelectedApp: selectionApp,
                PinPending: pinPending ? true : null);

            await WriteAtomicAsync(FormatForHook(payload), cancellationToken);
            _logger.LogInformation("Context stash captured for {App} ({Title}).", appKey, topLevel?.Name);

            ActivateAgentApp();
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
    /// If the user wired an agent app id (Settings → MCP Server → Agent app),
    /// raise that app to the foreground after a successful capture so they
    /// can keep typing without an explicit window switch. Skipped when the
    /// id is empty or when the target is already frontmost — the activator
    /// handles the same-app short-circuit.
    /// </summary>
    private void ActivateAgentApp()
    {
        var id = _settings.McpServer.AgentAppId;
        if (string.IsNullOrWhiteSpace(id)) return;
        bool raised;
        try
        {
            raised = _appActivator.Activate(id);
            if (raised) _logger.LogDebug("Activated agent app {Id} after context capture.", id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to activate agent app {Id}", id);
            return;
        }
        if (!raised) return;

        // Optional post-activation keystroke (e.g. cmd+shift+space to wake a
        // dictation IME, cmd+l to focus the omnibar). macOS activation is
        // async — the focus change is dispatched on the AppKit run loop and
        // arrives a frame or two later, so we wait briefly before sending so
        // the keystroke lands in the agent app, not the previously-focused
        // window. Prefer Main; fall back to Alternative if Main is empty.
        var triggerCombo = _settings.McpServer.AgentTriggerKey;
        if (!triggerCombo.IsEnabled) return;
        var keySpec = triggerCombo.Main.IsValid ? triggerCombo.Main.ToXdotool()
                    : triggerCombo.Alternative.IsValid ? triggerCombo.Alternative.ToXdotool()
                    : string.Empty;
        if (string.IsNullOrEmpty(keySpec)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120);
                _input.PressKey(keySpec);
                _logger.LogDebug("Sent agent trigger key {Key} after activation.", keySpec);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send agent trigger key {Key}", keySpec);
            }
        });
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
            sb.Append("selection=\"").Append(SanitiseUserText(p.SelectedText, 200)).Append('"').Append(' ');
        }
        if (p.PinPending == true) sb.Append("pin_pending=true");
        sb.Append('\n');

        sb.Append("[everywhere-ctx-json] ");
        sb.Append(JsonSerializer.Serialize(p, ContextSnapshotPayload.SerializerOptions));
        sb.Append('\n');

        if (p.PinPending == true)
        {
            sb.Append("[everywhere-hint] The user pinned a UI element for this question. Call the Everywhere MCP `read_pick` tool now to consume it before answering — its bounds/tree/text are NOT in this envelope.\n");
        }
        else
        {
            sb.Append("[everywhere-hint] If the user's question needs more than this pointer, call the relevant Everywhere MCP tool — don't guess.\n");
        }

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

        // Sweep stale claim leftovers from prior aborted hook reads — keeps the
        // stash directory free of files that might confuse a future hook or a
        // user scanning the folder for "what's in there".
        SweepStaleClaimFiles(dir);

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

    private static void SweepStaleClaimFiles(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
            foreach (var leftover in Directory.EnumerateFiles(dir, "context-stash.consumed-*.json"))
            {
                try
                {
                    var info = new FileInfo(leftover);
                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        info.Delete();
                    }
                }
                catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
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
    [property: JsonPropertyName("selected_app")] string? SelectedApp,
    [property: JsonPropertyName("pin_pending")] bool? PinPending)
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
