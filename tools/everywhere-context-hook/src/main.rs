// Tiny binary invoked by Claude Code's UserPromptSubmit hook.
// Stats ~/Library/Application Support/Everywhere/context-stash.json — if it
// exists and is fresh (<5 min), prints its contents to stdout and deletes it.
// Otherwise exits 0 silently.
//
// Built for ~3 ms cold start so adding it to every Enter key doesn't slow
// terminal interaction at all. No deps, no allocations beyond what stdlib
// inevitably does.

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
        Err(_) => return ExitCode::SUCCESS, // 99% case — file absent, instant exit
    };

    if let Ok(modified) = meta.modified() {
        if SystemTime::now()
            .duration_since(modified)
            .map(|d| d.as_secs() > TTL_SECS)
            .unwrap_or(false)
        {
            // Stale — delete and exit silently.
            let _ = fs::remove_file(&path);
            return ExitCode::SUCCESS;
        }
    }

    let body = match fs::read(&path) {
        Ok(b) => b,
        Err(_) => return ExitCode::SUCCESS,
    };

    // Take semantics: consume on read.
    let _ = fs::remove_file(&path);

    let stdout = io::stdout();
    let _ = stdout.lock().write_all(&body);
    ExitCode::SUCCESS
}

fn stash_path() -> Option<PathBuf> {
    let home = env::var_os("HOME")?;
    let mut p = PathBuf::from(home);
    p.push("Library");
    p.push("Application Support");
    p.push("Everywhere");
    p.push("context-stash.json");
    Some(p)
}
