// Tiny binary invoked by Claude Code's UserPromptSubmit hook.
// Stats the platform-specific Everywhere context-stash file — if it exists
// and is fresh (<5 min), prints its contents to stdout and deletes it.
// Otherwise exits 0 silently so a routine Enter key has zero overhead.
//
// Built for ~3 ms cold start. Path resolution mirrors
// `Everywhere.Mcp.Snapshot.StashPaths` on the C# side; both must agree.

use std::env;
use std::fs;
use std::io::{self, Write};
use std::path::PathBuf;
use std::process::ExitCode;
use std::time::SystemTime;

const TTL_SECS: u64 = 5 * 60;

fn main() -> ExitCode {
    let path = match stash_path() {
        Some(p) => p,
        None => return ExitCode::SUCCESS,
    };

    let meta = match fs::metadata(&path) {
        Ok(m) => m,
        Err(e) if e.kind() == io::ErrorKind::NotFound => {
            // 99% case — file absent, instant exit
            return ExitCode::SUCCESS;
        }
        Err(e) => {
            // Unexpected I/O error: stat permissioned-out, FS unmounted, etc.
            // Surface to stderr (Claude Code captures it for hook diagnostics)
            // but still succeed so the user's prompt isn't blocked.
            let _ = writeln!(io::stderr(), "everywhere-context-hook: stat failed: {e}");
            return ExitCode::SUCCESS;
        }
    };

    if let Ok(modified) = meta.modified() {
        if SystemTime::now()
            .duration_since(modified)
            .map(|d| d.as_secs() > TTL_SECS)
            .unwrap_or(false)
        {
            let _ = fs::remove_file(&path);
            return ExitCode::SUCCESS;
        }
    }

    // Atomic Take: rename to a unique sibling first, so concurrent Claude Code
    // sessions or rapid Enter-spam can't both consume the same stash file.
    // Only the process that successfully renames owns the bytes; everyone else
    // hits ENOENT on the rename and exits silently.
    let claimed = path.with_file_name(format!(
        "context-stash.consumed-{}-{}.json",
        std::process::id(),
        SystemTime::now()
            .duration_since(SystemTime::UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0)
    ));
    if let Err(e) = fs::rename(&path, &claimed) {
        if e.kind() != io::ErrorKind::NotFound {
            let _ = writeln!(io::stderr(), "everywhere-context-hook: claim failed: {e}");
        }
        return ExitCode::SUCCESS;
    }

    let body = match fs::read(&claimed) {
        Ok(b) => b,
        Err(e) => {
            let _ = writeln!(io::stderr(), "everywhere-context-hook: read failed: {e}");
            let _ = fs::remove_file(&claimed);
            return ExitCode::SUCCESS;
        }
    };

    // Always remove the claimed copy — we own it.
    if let Err(e) = fs::remove_file(&claimed) {
        let _ = writeln!(io::stderr(), "everywhere-context-hook: remove failed: {e}");
    }

    // Sanity-check the claimed payload before injecting. Reject:
    //  - empty file (writer crashed mid-write)
    //  - bytes that don't start with the expected envelope header
    //  - oversized payloads (>64 KB; ours is ~1 KB)
    if !is_valid_payload(&body) {
        let _ = writeln!(io::stderr(),
            "everywhere-context-hook: discarded malformed stash ({} bytes)", body.len());
        return ExitCode::SUCCESS;
    }

    let stdout = io::stdout();
    let _ = stdout.lock().write_all(&body);

    // Surface a short, user-visible confirmation on stderr — Claude Code shows
    // hook stderr inline above the user's prompt so the user knows context was
    // injected without seeing the raw [everywhere-ctx] line in their UI.
    let summary = summarise_first_ctx_line(&body);
    let _ = writeln!(io::stderr(), "✓ everywhere context injected: {summary}");

    ExitCode::SUCCESS
}

/// Reject obviously-broken stash files so they don't leak garbage into the
/// agent's prompt. Real payloads are 200-1500 bytes and start with the
/// `[everywhere-ctx] ` envelope.
fn is_valid_payload(body: &[u8]) -> bool {
    if body.is_empty() || body.len() > 64 * 1024 {
        return false;
    }
    let prefix = b"[everywhere-ctx] ";
    body.starts_with(prefix)
}

/// Pick the salient bits out of the `[everywhere-ctx] app=… title="…" url=…
/// selection="…"` line so the stderr message is one short summary, not the
/// raw bytes the agent sees.
fn summarise_first_ctx_line(body: &[u8]) -> String {
    let line = std::str::from_utf8(body)
        .unwrap_or("")
        .lines()
        .find(|l| l.starts_with("[everywhere-ctx] "))
        .unwrap_or("");
    let kv = line.trim_start_matches("[everywhere-ctx] ");

    let app = extract_simple(kv, "app=").unwrap_or_else(|| "?".into());
    let title = extract_quoted(kv, "title=\"");
    let has_selection = kv.contains("selection=\"");

    let mut out = format!("app={app}");
    if let Some(t) = title {
        let trimmed: String = t.chars().take(60).collect();
        out.push_str(&format!(" title=\"{trimmed}\""));
    }
    if has_selection {
        out.push_str(" +selection");
    }
    out
}

fn extract_simple(s: &str, key: &str) -> Option<String> {
    let start = s.find(key)? + key.len();
    let rest = &s[start..];
    let end = rest.find(' ').unwrap_or(rest.len());
    Some(rest[..end].to_string())
}

fn extract_quoted(s: &str, key: &str) -> Option<String> {
    let start = s.find(key)? + key.len();
    let rest = &s[start..];
    let end = rest.find('"')?;
    Some(rest[..end].to_string())
}

fn stash_path() -> Option<PathBuf> {
    #[cfg(target_os = "macos")]
    {
        let home = env::var_os("HOME")?;
        let mut p = PathBuf::from(home);
        p.push("Library");
        p.push("Application Support");
        p.push("Everywhere");
        p.push("context-stash.json");
        Some(p)
    }
    #[cfg(target_os = "windows")]
    {
        // %APPDATA% mirrors C# SpecialFolder.ApplicationData on Windows.
        let appdata = env::var_os("APPDATA")?;
        let mut p = PathBuf::from(appdata);
        p.push("Everywhere");
        p.push("context-stash.json");
        Some(p)
    }
    #[cfg(all(unix, not(target_os = "macos")))]
    {
        // Linux / freedesktop XDG: prefer $XDG_DATA_HOME, fall back to ~/.local/share.
        if let Some(xdg) = env::var_os("XDG_DATA_HOME") {
            let mut p = PathBuf::from(xdg);
            p.push("Everywhere");
            p.push("context-stash.json");
            return Some(p);
        }
        let home = env::var_os("HOME")?;
        let mut p = PathBuf::from(home);
        p.push(".local");
        p.push("share");
        p.push("Everywhere");
        p.push("context-stash.json");
        Some(p)
    }
}
