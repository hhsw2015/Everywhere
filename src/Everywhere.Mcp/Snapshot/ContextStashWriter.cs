using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Interop.Whiteboard;
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
    private readonly WhiteboardStash _whiteboardStash;
    private readonly IAppActivator _appActivator;
    private readonly IInputSimulator _input;
    private readonly IClipboardReader _clipboard;
    private readonly Settings _settings;
    private readonly ILogger<ContextStashWriter> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ContextStashWriter(
        IVisualElementContext context,
        IBrowserUrlReader browserUrl,
        SelectionCache selectionCache,
        PickStash pickStash,
        WhiteboardStash whiteboardStash,
        IAppActivator appActivator,
        IInputSimulator input,
        IClipboardReader clipboard,
        Settings settings,
        ILogger<ContextStashWriter> logger)
    {
        _context = context;
        _browserUrl = browserUrl;
        _selectionCache = selectionCache;
        _pickStash = pickStash;
        _whiteboardStash = whiteboardStash;
        _appActivator = appActivator;
        _input = input;
        _clipboard = clipboard;
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

    /// <summary>
    /// Bring the configured agent app to the front. Public so the LinkRect
    /// hotkey can give the user visible feedback even when the rect picked
    /// zero navigable links (otherwise it looks like the hotkey did
    /// nothing).
    /// </summary>
    public void ActivateAgent() => ActivateAgentApp();

    /// <summary>
    /// LinkRect harvest entry: write a batch of (title, url) pairs into the
    /// agent-state snapshot. Same delivery channel as everything else; the
    /// agent is the one that decides what to do with them.
    /// </summary>
    public async Task CaptureLinksAsync(
        IReadOnlyList<(string Title, string Url)> links,
        CancellationToken cancellationToken = default)
    {
        if (links is null || links.Count == 0) return;
        if (!await _writeLock.WaitAsync(0, cancellationToken))
        {
            _logger.LogDebug("Context capture already in progress; dropping LinkRect batch.");
            return;
        }
        try
        {
            var focused = _context.FocusedElement;
            var topLevel = WalkToTopLevel(focused) ?? focused;
            var pid = topLevel?.ProcessId ?? 0;
            var appKey = pid > 0 ? AppKey.FromProcessId(pid) : null;
            string? url = null;
            if (pid > 0)
            {
                try { url = _browserUrl.GetUrl(pid); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger.LogDebug(ex, "browser url for pid {Pid}", pid); }
            }
            // Bound the batch — drag selection on a long page can return
            // thousands of anchors, which would blow up agent-state size
            // and downstream get_bulk fan-out. Defense-in-depth scheme
            // filter mirrors the platform-side check: even if the picker
            // somehow slipped a javascript:/data: through, we don't write
            // it to the stash.
            const int MaxLinks   = 200;
            const int MaxUrlLen  = 2048;
            const int MaxTitleLen = 200;
            var picked = new List<PickedLink>(Math.Min(links.Count, MaxLinks));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int dropped = 0, capped = 0;
            foreach (var (title, linkUrl) in links)
            {
                if (string.IsNullOrWhiteSpace(linkUrl)) { dropped++; continue; }
                if (linkUrl.Length > MaxUrlLen)         { dropped++; continue; }
                if (!IsAllowedScheme(linkUrl))          { dropped++; continue; }
                var dedupKey = linkUrl + "\0" + (title ?? string.Empty);
                if (!seen.Add(dedupKey)) { dropped++; continue; }
                var trimmedTitle = string.IsNullOrWhiteSpace(title)
                    ? null
                    : (title.Length > MaxTitleLen ? title[..MaxTitleLen] : title);
                picked.Add(new PickedLink(linkUrl, trimmedTitle));
                if (picked.Count >= MaxLinks)
                {
                    capped = links.Count - (picked.Count + dropped);
                    break;
                }
            }
            if (capped > 0 || dropped > 0)
                _logger.LogInformation(
                    "LinkRect batch: kept={Kept} dropped={Dropped} capped_remaining={Capped} total_in={Total}",
                    picked.Count, dropped, capped, links.Count);
            if (picked.Count == 0) return;
            var payload = new ContextSnapshotPayload(
                SchemaVersion: CurrentSchemaVersion,
                CapturedAtUtc: DateTimeOffset.UtcNow,
                App: appKey,
                ProcessId: pid > 0 ? pid : null,
                WindowTitle: topLevel?.Name,
                Url: url,
                SelectedText: null,
                SelectedApp: null,
                PinPending: null,
                PickedLinks: picked);
            await WriteAtomicAsync(FormatForHook(payload), cancellationToken);
            _logger.LogInformation("Context stash captured {Count} links from {App}.", picked.Count, appKey);
            ActivateAgentApp();
        }
        finally
        {
            _writeLock.Release();
        }
    }

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

            // Whiteboard is independent: a fresh whiteboard session always
            // means the user wants the agent to read regions, regardless of
            // whether this capture was pin-triggered or context-triggered.
            var whiteboardRegions = _whiteboardStash.Peek();
            var whiteboardPending = whiteboardRegions is { Count: > 0 };

            if (string.IsNullOrEmpty(appKey)
                && string.IsNullOrEmpty(topLevel?.Name)
                && string.IsNullOrEmpty(url)
                && string.IsNullOrEmpty(selectionText)
                && !pinPending
                && !whiteboardPending)
            {
                _logger.LogDebug("Context stash capture produced no usable data; skipping write.");
                return;
            }

            // Pick up any xlb shift-multi-pick batch sitting on the
            // clipboard (sentinel-prefixed) so a single SnapshotContext
            // press carries both the window snapshot AND the URL list
            // the user just collected. Sentinel guard ensures arbitrary
            // clipboard text is never harvested.
            var clipboardLinks = TryReadXlbMultiPick();
            var payload = new ContextSnapshotPayload(
                SchemaVersion: CurrentSchemaVersion,
                CapturedAtUtc: DateTimeOffset.UtcNow,
                App: appKey,
                ProcessId: pid > 0 ? pid : null,
                WindowTitle: topLevel?.Name,
                Url: url,
                SelectedText: selectionText,
                SelectedApp: selectionApp,
                PinPending: pinPending ? true : null,
                WhiteboardPending: whiteboardPending ? true : null,
                WhiteboardRegionCount: whiteboardPending ? whiteboardRegions!.Count : null,
                PickedLinks: clipboardLinks);

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
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogInformation("Agent app id is empty; skipping activation.");
            return;
        }
        bool raised;
        try
        {
            raised = _appActivator.Activate(id);
            _logger.LogInformation("AppActivator.Activate({Id}) returned {Raised}.", id, raised);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to activate agent app {Id}", id);
            return;
        }
        if (!raised) return;
        TryFireLaunchPhrase(id);
    }

    private const string XlbMultiPickSentinel = "xlb-multi-pick://";

    // Bounds shared between LinkRect's CaptureLinksAsync path and the
    // clipboard sentinel harvest below. Keep both call sites lined up
    // so one limit change propagates everywhere.
    private const int MaxClipboardLinks = 200;
    private const int MaxClipboardUrlLen = 2048;

    /// <summary>
    /// Look at the system clipboard for xlinkBook's shift-multi-pick batch:
    ///     xlb-multi-pick://
    ///     https://...
    ///     https://...
    /// Anything not preceded by the sentinel is ignored — random clipboard
    /// text never lands in the agent context.
    /// </summary>
    private IReadOnlyList<PickedLink>? TryReadXlbMultiPick()
    {
        string? raw;
        try { raw = _clipboard.GetText(); }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith(XlbMultiPickSentinel, StringComparison.OrdinalIgnoreCase))
            return null;
        // Tolerate CRLF / BOM in clipboard text — macOS apps ship both.
        var lines = trimmed.Replace("\r\n", "\n").Replace("﻿", string.Empty)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var picked = new List<PickedLink>(Math.Min(lines.Length, MaxClipboardLinks));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (line.StartsWith(XlbMultiPickSentinel, StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Length > MaxClipboardUrlLen) continue;
            if (!Uri.TryCreate(line, UriKind.Absolute, out var u)) continue;
            // mailto: produces useful agent-context too — keep it. The
            // narrower http/https-only check is for navigable web URLs;
            // here we just ensure no javascript:/file:/data: leaks in.
            if (u.Scheme is not ("http" or "https" or "mailto")) continue;
            var redacted = RedactCredentials(u);
            if (string.IsNullOrEmpty(redacted)) continue;
            // AbsoluteUri preserves percent-encoding so the round-trip is
            // exact; ToString() decodes reserved characters and would
            // corrupt links containing literal spaces or unicode in path.
            if (!seen.Add(redacted)) continue;
            picked.Add(new PickedLink(redacted, redacted));
            if (picked.Count >= MaxClipboardLinks) break;
        }
        return picked.Count == 0 ? null : picked;
    }

    /// <summary>
    /// Strip credentials (userinfo, common token query params) before
    /// the URL lands in agent-state on disk. We don't try to be clever —
    /// a small denylist of well-known param names covers the cases users
    /// actually paste from xlb (api_key, token, access_token, sig, ...).
    /// Returns AbsoluteUri so percent-encoding is preserved exactly.
    /// </summary>
    private static readonly HashSet<string> _redactQueryParams =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "token", "access_token", "id_token", "refresh_token",
            "api_key", "apikey", "key", "secret", "client_secret",
            "auth", "authentication", "password", "pwd",
            "sig", "signature", "session", "sessionid",
        };

    private static string RedactCredentials(Uri u)
    {
        try
        {
            var b = new UriBuilder(u) { UserName = string.Empty, Password = string.Empty };
            if (!string.IsNullOrEmpty(b.Query) && b.Query.Length > 1)
            {
                var pairs = b.Query.TrimStart('?').Split('&');
                var kept = new List<string>(pairs.Length);
                foreach (var pair in pairs)
                {
                    var eq = pair.IndexOf('=');
                    var name = eq < 0 ? pair : pair[..eq];
                    if (_redactQueryParams.Contains(Uri.UnescapeDataString(name))) continue;
                    kept.Add(pair);
                }
                b.Query = string.Join('&', kept);
            }
            return b.Uri.AbsoluteUri;
        }
        catch
        {
            return u.AbsoluteUri;
        }
    }

    /// <summary>
    /// After raising the agent app, optionally type a user-configured phrase
    /// + Enter so the agent immediately acts on whatever Everywhere just
    /// captured. Only reached via ActivateAgentApp() which is only called
    /// after a successful stash write — empty/no-op captures can never
    /// trigger an injection. Skipped when phrase is blank.
    /// </summary>
    private void TryFireLaunchPhrase(string agentAppId)
    {
        var phrase = _settings.McpServer.LaunchPhrase;
        if (string.IsNullOrWhiteSpace(phrase)) return;
        if (!_appActivator.SupportsFrontmostDetection)
        {
            _logger.LogDebug(
                "LaunchPhrase: platform activator lacks frontmost detection; skipping injection to avoid mis-fire.");
            return;
        }
        // Fire-and-forget so the capture path isn't blocked on activation.
        _ = Task.Run(async () =>
        {
            try
            {
                // True frontmost-detection (NSWorkspace.frontmostApplication
                // on macOS) — Activate()'s return value just says "raise was
                // dispatched", not "the app actually got focus". Some apps
                // (Arc, browsers handling their own global hotkeys) raise,
                // then steal focus back when their own hotkey handler
                // finishes. Re-check after raising and require the app to
                // STAY frontmost across two consecutive 150ms ticks before
                // we type. Re-issue raise each tick — cheap when already
                // frontmost. Cap ~2.4s so a misconfigured agent id doesn't
                // hold the simulator forever.
                var stable = 0;
                var settled = false;
                for (var i = 0; i < 16; i++)
                {
                    try { _appActivator.Activate(agentAppId); }
                    catch { /* swallow; checked below */ }
                    await Task.Delay(150);
                    bool front;
                    try { front = _appActivator.IsFrontmost(agentAppId); }
                    catch { front = false; }
                    if (front)
                    {
                        if (++stable >= 2) { settled = true; break; }
                    }
                    else
                    {
                        stable = 0;
                    }
                }
                if (!settled)
                {
                    _logger.LogInformation(
                        "LaunchPhrase: agent {Id} did not stay frontmost (likely focus-stealing app); skipping injection.",
                        agentAppId);
                    return;
                }
                // Last-ditch check immediately before TypeText: focus may
                // have flipped during the final 150ms tick between the
                // settle confirmation and now. A focus-stealing app (e.g.
                // browser handling a global hotkey on key-up) can pull
                // the frontmost slot back here.
                if (!_appActivator.IsFrontmost(agentAppId))
                {
                    _logger.LogInformation(
                        "LaunchPhrase: focus stolen from {Id} just before injection; skipping.",
                        agentAppId);
                    return;
                }
                _input.TypeText(phrase);
                // Confirm again before pressing Return so we don't submit
                // partial typing into the wrong app if focus flipped mid-
                // injection (e.g. Arc grabbing focus on the first keystroke).
                if (!_appActivator.IsFrontmost(agentAppId))
                {
                    _logger.LogInformation(
                        "LaunchPhrase: focus stolen from {Id} during typing; not pressing Return.",
                        agentAppId);
                    return;
                }
                _input.PressKey("Return");
                _logger.LogInformation("LaunchPhrase fired ({Len} chars) into {Id}.", phrase.Length, agentAppId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LaunchPhrase injection failed");
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
    private string FormatForHook(ContextSnapshotPayload p)
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
        if (p.PinPending == true) sb.Append("pin_pending=true ");
        if (p.WhiteboardPending == true)
            sb.Append("whiteboard_pending=true regions=").Append(p.WhiteboardRegionCount ?? 0);
        if (p.PickedLinks is { Count: > 0 } pickedLinks)
            sb.Append("picked_links=").Append(pickedLinks.Count).Append(' ');
        sb.Append('\n');

        if (p.PickedLinks is { Count: > 0 } linkRows)
        {
            // One line per link so the agent can read them without parsing
            // the JSON envelope. Title trimmed; URL kept full.
            for (var i = 0; i < linkRows.Count; i++)
            {
                var l = linkRows[i];
                sb.Append("[everywhere-ctx-link] #").Append(i).Append(' ');
                sb.Append("url=").Append(SanitiseTokenValue(l.Url, 512)).Append(' ');
                if (l.Title is { Length: > 0 })
                    sb.Append("title=\"").Append(SanitiseUserText(l.Title, 120)).Append('"');
                sb.Append('\n');
            }
        }

        sb.Append("[everywhere-ctx-json] ");
        sb.Append(JsonSerializer.Serialize(p, ContextSnapshotPayload.SerializerOptions));
        sb.Append('\n');

        // Per-app discovery hint: only for apps the user has registered in
        // Settings -> MCP -> KnownApps. Each entry maps a title regex to
        // a discovery URL. Everywhere stays ignorant of any specific app.
        var discoveryUrl = ResolveDiscoveryUrl(p.WindowTitle);
        var statePath = discoveryUrl is null ? null : ToStatePath(discoveryUrl);

        // Hint priority:
        //   0. Whiteboard pending -> the user drew gestures on screen for the
        //      agent. Always read it first; gestures carry intent (emphasis /
        //      strike-through / point) that pin/state cannot.
        //   1. Pin + known web app -> the pinned element is almost certainly
        //      reflected in the app's event stream. Prefer the fast-path
        //      state endpoint (returns markdown rendered by the app itself,
        //      same as the user sees), fall back to read_pick only if state
        //      doesn't cover the pin.
        //   2. Pin only -> read_pick (use mode='auto' so popup-of-links pins
        //      come back compact).
        //   3. Known web app, no pin -> state for "what was the user doing",
        //      discover catalog for deeper queries.
        //   4. Neither -> generic MCP hint.
        if (p.WhiteboardPending == true)
        {
            sb.Append("[everywhere-hint] User drew ").Append(p.WhiteboardRegionCount ?? 1)
              .Append(" annotated region(s) on a virtual whiteboard for this agent. ");
            sb.Append("CALL FIRST: mcp__everywhere__read_whiteboard — returns one markdown block ");
            sb.Append("per region with the gesture's kind (circle=emphasis, x=exclude, arrow=point, ");
            sb.Append("underline=focus on a single line) and the text the gesture captured. ");
            sb.Append("This is one-shot; reading consumes the slot. ");
            sb.Append("DO NOT use read_pick for whiteboard content — it's a different stash.\n");
        }
        else if (p.PinPending == true && statePath is not null)
        {
            sb.Append("[everywhere-hint] User pinned a UI element AND this is a known local web app. PREFER: GET ");
            sb.Append(statePath);
            sb.Append("?consume=1 — returns markdown of recent view + pin contents (urls/labels). For deeper exploration of the topic (graph connections, tag groups, curated commands), follow up with the same URL plus &with_meta=1, or call the app's browse skill on the topic. Don't fetch meta unless the user actually needs it.\n");
        }
        else if (p.PinPending == true)
        {
            sb.Append("[everywhere-hint] The user pinned a UI element for this question. Call the Everywhere MCP `read_pick` tool with mode='auto' (default) — for a popup of links it auto-returns a compact url+label list (~30 tokens), not the full a11y tree.\n");
        }
        else if (statePath is not null)
        {
            sb.Append("[everywhere-discover] xlb-style local app self-describes at ");
            sb.Append(discoveryUrl);
            sb.Append(". Fast path: GET ");
            sb.Append(statePath);
            sb.Append("?consume=1 — recent view + interactions as markdown. For deeper exploration (topic graph, tag groups, curated commands), append &with_meta=1 OR fetch the discovery URL and call a skill — only when the user's question actually needs that context.\n");
        }
        else
        {
            sb.Append("[everywhere-hint] If the user's question needs more than this pointer, call the relevant Everywhere MCP tool — don't guess.\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Derive the fast-path "agent-state" URL from a discovery URL by
    /// substituting the well-known segment. Falls back to the raw
    /// discoveryUrl if the convention isn't met (the agent will then
    /// have to fetch the catalog and look up the fast_paths array).
    /// </summary>
    private static string ToStatePath(string discoveryUrl)
    {
        // Common convention: discovery URL ends in '/agent-skills'; the
        // sibling fast path is '/agent-state'. Otherwise leave as-is.
        if (discoveryUrl.EndsWith("/agent-skills", StringComparison.OrdinalIgnoreCase))
            return discoveryUrl[..^"/agent-skills".Length] + "/agent-state";
        if (discoveryUrl.EndsWith("/xlb-perception", StringComparison.OrdinalIgnoreCase))
            return discoveryUrl[..^"/xlb-perception".Length] + "/agent-state";
        return discoveryUrl;
    }

    private static readonly TimeSpan _knownAppRegexTimeout = TimeSpan.FromMilliseconds(100);

    private string? ResolveDiscoveryUrl(string? windowTitle)
    {
        if (string.IsNullOrEmpty(windowTitle)) return null;
        var apps = _settings.McpServer.KnownApps;
        if (apps is null || apps.Count == 0) return null;
        foreach (var app in apps)
        {
            if (string.IsNullOrEmpty(app.TitlePattern) || string.IsNullOrEmpty(app.DiscoverUrl))
                continue;
            // Reject malformed / non-http URLs early — don't even try to
            // match a pattern whose target is unusable. Prevents bad
            // settings.json entries from leaking into stash hints.
            if (!Uri.TryCreate(app.DiscoverUrl, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme is not ("http" or "https")) continue;
            try
            {
                // ReDoS guard: cap regex evaluation; pathological user
                // patterns (e.g. (a+)+$) against long titles must not be
                // able to freeze context capture.
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        windowTitle, app.TitlePattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                        _knownAppRegexTimeout))
                    return app.DiscoverUrl;
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                // pathological pattern, skip silently — next entry may match.
            }
            catch (ArgumentException)
            {
                // invalid pattern syntax, skip.
            }
        }
        return null;
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
    /// Trust-boundary scheme allow-list. Must mirror the platform-side
    /// allow-list — a javascript:/data: URL slipping through to agent-state
    /// could be acted on by a downstream tool.
    /// </summary>
    private static bool IsAllowedScheme(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            return u.Scheme is "http" or "https" or "mailto";
        return false;
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
    [property: JsonPropertyName("pin_pending")] bool? PinPending,
    [property: JsonPropertyName("whiteboard_pending")] bool? WhiteboardPending = null,
    [property: JsonPropertyName("whiteboard_region_count")] int? WhiteboardRegionCount = null,
    // linkclump-plus style harvest: rect-selected hyperlinks. Same delivery
    // channel as everything else — agent reads agent-state, decides what to
    // do with them. Everywhere does not POST anywhere on the user's behalf.
    [property: JsonPropertyName("picked_links")] IReadOnlyList<PickedLink>? PickedLinks = null)
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed record PickedLink(
    [property: JsonPropertyName("url")]    string Url,
    [property: JsonPropertyName("title")]  string? Title);
