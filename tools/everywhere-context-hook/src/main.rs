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

    let body = match fs::read(&path) {
        Ok(b) => b,
        Err(e) => {
            let _ = writeln!(io::stderr(), "everywhere-context-hook: read failed: {e}");
            return ExitCode::SUCCESS;
        }
    };

    // Take semantics: consume on read.
    if let Err(e) = fs::remove_file(&path) {
        let _ = writeln!(io::stderr(), "everywhere-context-hook: remove failed: {e}");
    }

    let stdout = io::stdout();
    let _ = stdout.lock().write_all(&body);
    ExitCode::SUCCESS
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
