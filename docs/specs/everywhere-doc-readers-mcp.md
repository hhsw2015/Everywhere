# GOAL: Document-Reader MCP Tools for Everywhere

## What this spec is

A self-contained specification for an autonomous coding loop. Claude (or any
capable code agent) reads this file once via `/goal` and runs to completion
without human intervention. The loop terminates when the **Done criteria**
are met or the failure-mode escape hatch fires.

The deliverable is a set of MCP tools on the Everywhere server that let an
LLM agent read PDF / DOCX / XLSX / PPTX / EPUB / HTML / TXT files when the
user has selected them in Finder (Everywhere already exposes
`get_finder_selection`). This is inspired by AnythingLLM's `collector/`
package — we borrow its behavioural conventions but reimplement everything
in idiomatic .NET. No Node.js dependency.

This is a *closed-loop* task: input is files on disk, output is plain text,
correctness is measured by similarity to golden text. **No human review of
intermediate states is required**; the agent reads CI logs to decide what to
fix next.

---

## Done criteria (the loop exits when ALL of these are true)

| # | Criterion | How to verify (agent-runnable command) |
|---|-----------|----------------------------------------|
| 1 | New project `tests/Everywhere.DocReaders.Tests/` exists, builds, runs. | `dotnet test` (in CI) returns exit 0 |
| 2 | Corpus directory `tests/doc-corpus/` contains ≥ 50 files. | `find tests/doc-corpus -type f -not -name '*.golden.txt' -not -name '*.sh' \| wc -l` ≥ 50 |
| 3 | Every corpus file has a sibling `.golden.txt`. | `tests/doc-corpus/check-goldens.sh` (the agent writes this script) returns 0 |
| 4 | `tests-doc-readers.yml` workflow runs and finishes ≤ 5 min on `ubuntu-latest`. | `gh run view <ID> --json conclusion,durationMs` |
| 5 | Test pass rate ≥ 95% on the most recent CI run. | Parse the `.trx` artifact: `passed / (passed + failed) ≥ 0.95` |
| 6 | All 7 MCP tools registered and listed in the Everywhere MCP server. | grep `[McpServerTool]` count under `src/Everywhere.Mcp/Tools/Doc*` ≥ 7 |
| 7 | `get_finder_selection` augmented with `mime` + `kind_hint`. | grep `kind_hint` in `src/Everywhere.Mcp/Tools/GetFinderSelection*` |
| 8 | PR `experiment/doc-readers` → `main` is open with required body sections. | `gh pr view --json body \| jq -r .body \| grep -E "^## (Tools added\|Pass rate\|Dependencies\|Known limitations)"` 4 matches |

If all 8 verifications return success, the loop exits with success. The PR
is left open for human review; **the agent does NOT merge.**

---

## Hard constraints (violating any of these is a failure, not a tradeoff)

- **No local build on user's machine.** All compilation and testing
  happens in GitHub Actions on the `experiment/doc-readers` branch.
  The user's machine has no `dotnet` SDK installed.
- **No git tags during the loop.** All release workflows
  (`macos-release.yml`, `linux-release.yml`, `windows-release.yml`)
  trigger ONLY on tag pushes matching `v*.*.*`. Branch pushes never
  trigger them. → Don't push tags.
- **Push budget: ≤ 20 commits total** to `experiment/doc-readers`.
  Each push runs CI (~1-2 min). Batch fixes; don't push one-line
  tweaks. Target distribution: Phase 1 (1) + Phase 2 (2) + Phase 3
  (~14, avg 2.3 per format × 6 formats) + Phase 4 (1) + Phase 5 (2).
- **No new heavy dependencies.** Allowed nuget packages (pick from):
  - `DocumentFormat.OpenXml` (DOCX/XLSX/PPTX, MIT license)
  - `ClosedXML` (XLSX with formula, MIT license)
  - `VersOne.Epub` (EPUB, MIT license)
  - `AngleSharp` or `HtmlAgilityPack` (HTML parsing, MIT license)
  - `UglyToad.PdfPig` (PDF text extraction, Apache 2.0) — recommended
    over `iText7` (AGPL). The nuget package id is `PdfPig` (without
    namespace) but the actual package is published under
    `UglyToad.PdfPig`. Use whichever id `dotnet add package` resolves.
  Plus what's already referenced in `Directory.Packages.props`.
  Adding anything else requires writing a justification paragraph
  in the PR body AND must be MIT/Apache/BSD licensed.
- **No vector DB, no embeddings, no LLM-side processing.** Tools
  return plain text + structured metadata. Chunking and embedding
  are agent-host concerns, not Everywhere's.
- **Golden text source must be deterministic.** Use battle-tested
  CLI tools (`pdftotext`, `pandoc`, `xlsx2csv`) to generate
  `.golden.txt`. Do NOT use AnythingLLM output as the golden — it
  has its own quirks. Don't run our own implementation to derive
  goldens (circular).
- **Similarity metric**: token-set Jaccard or normalized Levenshtein,
  threshold ≥ **0.92**. Same threshold applies to every corpus file.
  **Lowering the threshold to make tests pass is forbidden** —
  exempt the file via SUMMARY.md instead, with a written reason.
- **Agent does not merge the PR.** Final merge is a human decision.

---

## Project conventions to follow

Confirmed by inspecting `tests/Everywhere.Mcp.Tests/Everywhere.Mcp.Tests.csproj`:

- **Test framework**: NUnit (NOT xunit). Use `[Test]`, `[TestCase]`,
  `[TestCaseSource]`. The new csproj should mirror the existing test
  csproj package list:
  - `Microsoft.NET.Test.Sdk`
  - `NUnit`
  - `NUnit.Analyzers`
  - `NUnit3TestAdapter`
  - `coverlet.collector`
  Plus the new doc-reader nuget packages (`PdfPig`, `OpenXml`,
  `ClosedXML`, `VersOne.Epub`, `AngleSharp`).

- **Package versions**: managed centrally in `Directory.Packages.props`.
  Add new packages there with a `<PackageVersion>` element, then
  reference without `Version=` in the csproj.

- **Solution file**: add the new test project to BOTH `Everywhere.slnx`
  AND `Everywhere.Linux.slnx` (CI runs on ubuntu).

- **Target framework**: same as existing test projects — read from
  `Directory.Build.props` to confirm. Likely `net10.0`.

- **MCP tool registration**: existing tools live under
  `src/Everywhere.Mcp/Tools/`. Read 2-3 examples (e.g.
  `GetFinderSelectionTool.cs`, `ReadWhiteboardTool.cs`) before
  writing new ones to learn the `[McpServerToolType]` /
  `[McpServerTool]` attribute pattern, JSON return shape, etc.

### Minimal NUnit test pattern (use this verbatim, parameterised)

```csharp
public class ReadPdfTests
{
    public static IEnumerable<TestCaseData> PdfCases() =>
        Directory.EnumerateFiles("tests/doc-corpus", "*.pdf")
            .Select(f => new TestCaseData(f).SetName(Path.GetFileName(f)));

    [TestCaseSource(nameof(PdfCases))]
    public async Task ReadPdf_MatchesGolden(string pdfPath)
    {
        var actual = await ReadPdfTool.RunAsync(pdfPath);
        var golden = await File.ReadAllTextAsync(pdfPath + ".golden.txt");
        var sim = Similarity.NormalizedTokenJaccard(actual, golden);
        Assert.That(sim, Is.GreaterThanOrEqualTo(0.92),
            $"sim={sim:F3} on {pdfPath}\n--- DIFF ---\n{Diff(actual, golden, 1000)}");
    }
}
```

---

## Workflow

### Phase 0: Preflight (no push)

Before touching anything:

1. `git fetch && git status` — confirm clean working tree on `main`.
2. Read `tests/Everywhere.Mcp.Tests/Everywhere.Mcp.Tests.csproj` —
   confirm csproj structure.
3. Read `Directory.Packages.props` — confirm centrally managed versions.
4. Read `Directory.Build.props` — confirm `<TargetFramework>`.
5. Read 2 existing MCP tools (e.g. `GetAppStateTool.cs` showing
   `[McpServerToolType]` on the static class and
   `[McpServerTool(Name = "snake_case_name", ReadOnly = true)]` on each
   method, returning `CallToolResult`).
6. Read `.github/workflows/macos-release.yml` to see how the project
   currently installs .NET 10 SDK in CI — copy that exact step to
   guarantee compatibility (prevents Phase 1 from blowing up on
   `setup-dotnet@v4` quirks for preview SDKs).
7. Read AnythingLLM converter source for the 7 formats. URLs:
   ```
   https://raw.githubusercontent.com/Mintplex-Labs/anything-llm/master/collector/processSingleFile/convert/asPDF/index.js
   https://raw.githubusercontent.com/Mintplex-Labs/anything-llm/master/collector/processSingleFile/convert/asPDF/PDFLoader.js
   https://raw.githubusercontent.com/Mintplex-Labs/anything-llm/master/collector/processSingleFile/convert/asDocx.js
   https://raw.githubusercontent.com/Mintplex-Labs/anything-llm/master/collector/processSingleFile/convert/asXlsx.js
   https://raw.githubusercontent.com/Mintplex-Labs/anything-llm/master/collector/processSingleFile/convert/asOfficeMime.js
   https://raw.githubusercontent.com/Mintplex-Labs/anything-llm/master/collector/processSingleFile/convert/asEPub.js
   https://raw.githubusercontent.com/Mintplex-Labs/anything-llm/master/collector/processSingleFile/convert/asTxt.js
   ```
   Fetch via `curl -s <URL>`; capture the encoding-fallback,
   tokenize, and metadata schema patterns.

### Phase 1: Setup (1 push)

1. `git checkout -b experiment/doc-readers`
2. Create `tests/Everywhere.DocReaders.Tests/Everywhere.DocReaders.Tests.csproj`
   mirroring `tests/Everywhere.Mcp.Tests/`. Add a placeholder
   `SmokeTest.cs` with one passing assertion.
3. Add new nuget packages to `Directory.Packages.props`.
4. Add the test project to `Everywhere.slnx` and `Everywhere.Linux.slnx`.
5. Write `.github/workflows/tests-doc-readers.yml`:
   ```yaml
   name: (Tests) DocReaders
   on:
     push:
       branches: [experiment/doc-readers]
       paths:
         - 'tests/Everywhere.DocReaders.Tests/**'
         - 'tests/doc-corpus/**'
         - 'src/Everywhere.Mcp/Tools/Doc**'
         - 'src/Everywhere.Mcp/Tools/GetFinderSelection*'
         - 'Directory.Packages.props'
         - '.github/workflows/tests-doc-readers.yml'
   jobs:
     test:
       runs-on: ubuntu-latest
       steps:
         - uses: actions/checkout@v4
         - uses: actions/setup-dotnet@v4
           with:
             # .NET 10 may still be in preview at the time this runs.
             # If 'preview' channel fails, fall back to a specific
             # daily build (look at existing macos-release.yml for
             # the version that works for the project).
             dotnet-version: '10.0.x'
             dotnet-quality: 'preview'
           continue-on-error: false
         - run: |
             sudo apt-get update -qq
             sudo apt-get install -y poppler-utils pandoc python3-openpyxl xlsx2csv
         - run: dotnet restore tests/Everywhere.DocReaders.Tests
         - run: dotnet test tests/Everywhere.DocReaders.Tests
                  --logger "trx;LogFileName=test-results.trx"
                  --logger "console;verbosity=normal"
         - uses: actions/upload-artifact@v4
           if: always()
           with:
             name: test-results
             path: '**/test-results.trx'
   ```
6. Push `experiment/doc-readers`. Verify CI green:
   ```
   gh run list -R hhsw2015/Everywhere --workflow=tests-doc-readers.yml --branch experiment/doc-readers --limit 1
   ```

### Phase 2: Corpus + goldens (≤ 2 pushes)

**Push A**: Add corpus files + the goldens-generation script.

1. Curate corpus. Specific public-domain sources (the agent should
   `curl` these into `tests/doc-corpus/`):
   - PDFs: arxiv abstracts (e.g. `https://arxiv.org/pdf/1706.03762`),
     Project Gutenberg PDF dumps, sample PDF/A test files
     (`https://github.com/py-pdf/sample-files`).
   - DOCX/XLSX/PPTX: `https://github.com/dotnet/Office/tree/main/...`
     sample files, the OfficeDev OOXMLValidatorTool fixtures, or
     LibreOffice template gallery.
   - EPUBs: Project Gutenberg
     (`https://www.gutenberg.org/ebooks/?formats=EPUB`).
   - HTML: Wikipedia article dumps (small ones), MDN docs samples.
   - TXT/MD: README files from popular OSS projects.
   Cap each file at 2MB; total corpus ≤ 50MB.

2. Write `tests/doc-corpus/generate-goldens.sh`:
   ```bash
   #!/usr/bin/env bash
   set -euo pipefail
   cd "$(dirname "$0")"
   for f in *.pdf; do [ -f "$f" ] && pdftotext -layout "$f" "$f.golden.txt"; done
   for f in *.docx *.pptx *.epub *.html *.htm; do [ -f "$f" ] && pandoc -t plain "$f" -o "$f.golden.txt"; done
   for f in *.xlsx; do [ -f "$f" ] && xlsx2csv "$f" "$f.golden.txt"; done
   for f in *.txt *.md; do [ -f "$f" ] && cp "$f" "$f.golden.txt"; done
   ```
3. Write `tests/doc-corpus/check-goldens.sh`:
   ```bash
   #!/usr/bin/env bash
   set -euo pipefail
   cd "$(dirname "$0")"
   missing=0
   for f in *.pdf *.docx *.xlsx *.pptx *.epub *.html *.htm *.txt *.md; do
     [ -f "$f" ] || continue
     [ -f "$f.golden.txt" ] || { echo "missing golden: $f"; missing=1; }
   done
   exit $missing
   ```
4. Add a one-shot `bootstrap-goldens.yml` workflow:
   ```yaml
   name: (Tests) Bootstrap goldens
   on:
     workflow_dispatch:
   jobs:
     gen:
       runs-on: ubuntu-latest
       steps:
         - uses: actions/checkout@v4
           with: { ref: experiment/doc-readers, token: ${{ secrets.GITHUB_TOKEN }} }
         - run: |
             sudo apt-get update -qq
             sudo apt-get install -y poppler-utils pandoc python3-openpyxl xlsx2csv
         - run: bash tests/doc-corpus/generate-goldens.sh
         - run: |
             git config user.name "github-actions"
             git config user.email "github-actions@github.com"
             git add tests/doc-corpus/*.golden.txt
             # [skip ci] in commit message prevents tests-doc-readers.yml
             # from triggering a redundant CI run on this auto-commit.
             git diff --cached --quiet || (git commit -m "chore: regenerate goldens [skip ci]" && git push)
   ```
5. Commit corpus files + scripts + bootstrap workflow. Push.

**Push B (if needed)**: Trigger goldens generation:
```
gh workflow run bootstrap-goldens.yml -R hhsw2015/Everywhere --ref experiment/doc-readers

# Find the run ID and watch it to completion
sleep 5  # let GitHub register the run
RUN=$(gh run list -R hhsw2015/Everywhere --workflow=bootstrap-goldens.yml --branch experiment/doc-readers --limit 1 --json databaseId | jq -r '.[0].databaseId')
gh run watch $RUN -R hhsw2015/Everywhere --exit-status

# Pull the auto-committed goldens
git pull origin experiment/doc-readers
```
This counts as +1 commit (the goldens commit by github-actions, which
is `[skip ci]`-tagged so it doesn't trigger a redundant test run) but
does NOT consume agent push budget.

### Phase 3: Implement readers, format-by-format (≤ 14 pushes total)

For each format `X` in [PDF, DOCX, XLSX, PPTX, EPUB, HTML]:

1. Implement `src/Everywhere.Mcp/Tools/DocReadXTool.cs`. Follow
   conventions in existing tools. The tool returns:
   ```json
   {
     "text": "...",
     "metadata": { "pages": 12, "truncated": false, "source": "..." }
   }
   ```
2. Encoding fallback chain (mirroring AnythingLLM): try UTF-8, then
   GB18030, then Latin-1. Don't trust mime alone.
3. For PDF: text extraction first (PdfPig). If text length < 100
   chars, mark `metadata.likely_scanned = true` but don't OCR on the
   ubuntu CI job. The macOS Vision OCR fallback is exempt
   (documented in SUMMARY.md as "macOS-only").
4. Add 5-10 NUnit tests using the `[TestCaseSource]` pattern above.
5. Push. Read CI log:
   ```
   RUN=$(gh run list -R hhsw2015/Everywhere --workflow=tests-doc-readers.yml --branch experiment/doc-readers --limit 1 --json databaseId | jq -r '.[0].databaseId')
   gh run view $RUN -R hhsw2015/Everywhere --log-failed
   gh run download $RUN -R hhsw2015/Everywhere -n test-results
   # parse test-results.trx for per-test failure details
   ```
6. For each failing case: read the dumped `Diff(actual, golden, 1000)`
   from the assert message, identify root cause (encoding / column
   order / formula handling / etc.), fix in batch, push.
7. Move to next format only when current format has ≥ 95% pass rate
   on its corpus subset. Maximum 3 pushes per format on average.

### Phase 4: Augment `get_finder_selection` (1 push)

1. Add `mime` (from file extension; libmagic optional) and
   `kind_hint` ("pdf"/"docx"/"xlsx"/"pptx"/"epub"/"html"/"image"/
   "text"/"unknown") fields to the tool's response.
2. Update existing GetFinderSelection unit tests if any; add 2-3
   new ones covering the augmentation.
3. Push. CI green.

### Phase 5: PR (1 push max, plus the PR open)

1. Update `tests/doc-corpus/SUMMARY.md` with:
   - total corpus size
   - pass rate (e.g. "47/50 = 94% — 3 files exempted")
   - exemption list with reasons
2. Push if SUMMARY.md was modified after the last green CI. Confirm
   the latest CI run on `experiment/doc-readers` is green.
3. Open PR via `gh pr create --base main --head experiment/doc-readers`.
   PR body MUST contain these section headers (verified in done #8):
   - `## Tools added`
   - `## Pass rate`
   - `## Dependencies`
   - `## Known limitations`
4. Loop ends. Agent waits for human merge.

---

## Failure modes & escape hatches

- **After 15 commits, pass rate < 80%**: stop. Append "Status:
  blocked" section to `SUMMARY.md` with what's failing and why.
  Then exit cleanly (no PR).
- **`dotnet restore` flakes** (nuget down, sdk image change):
  retry the same push up to 3 times before treating as real
  failure. Use `gh run rerun <ID>` to retry, costs no commits.
- **A nuget library doesn't work on .NET 10**: pick another
  from the constrained list. Document the swap in SUMMARY.md.
- **CI log > 500 KB**: don't grep the raw log. Download the
  `.trx` artifact and parse the XML for failure details.
- **Push budget reached at 20**: stop. Write SUMMARY.md "Status:
  budget exhausted" with current pass rate, then exit.
- **Bootstrap-goldens workflow doesn't auto-commit**: if the
  agent has no shell access to the runner's filesystem, fall
  back to running goldens generation locally if the agent's host
  has `pdftotext`/`pandoc`/`xlsx2csv` available; otherwise add a
  step that commits via the workflow itself (already specified).

---

## Tools the agent uses (no MCP setup needed beyond defaults)

- `Bash` for `git`, `gh`, `curl`, file editing (NEVER `dotnet build`
  locally — the user's machine has no SDK)
- `gh run list` / `gh run view --log-failed` / `gh run download`
  to read CI results
- `Bash + curl` to inspect AnythingLLM converter source from
  raw.githubusercontent.com (preferred over WebFetch — WebFetch
  may be blocked in some environments)
- `Edit` / `Write` for code

The agent does NOT need:
- Local `dotnet` SDK
- Local Node.js
- Browser MCP / Lightpanda
- Any user-machine state

---

## Inputs already known to the agent

- Repo: `/Users/wowdd1/Dev/Everywhere` (current working directory)
- Remote: `origin = https://github.com/hhsw2015/Everywhere`
- Default branch: `main` (currently at v0.9.238 head)
- CI provider: GitHub Actions, secrets already configured
- Existing MCP tools live in `src/Everywhere.Mcp/Tools/`. Read 2-3
  before writing new ones.
- Existing test layout: `tests/Everywhere.Mcp.Tests/` for reference
  csproj + NUnit conventions.

---

## Anti-patterns the agent must avoid

- **Lowering the 0.92 similarity threshold** to make tests pass.
  Use SUMMARY.md exemptions instead.
- **Pushing one-line fixes** in a tight loop. Batch.
- **Pushing git tags.** Triggers release workflows.
- **Adding npm/Node dependencies.** This is a .NET project.
- **Implementing OCR from scratch.** PDF OCR fallback is macOS-only
  (Vision); on ubuntu it's an exempted limitation.
- **Re-deriving golden text from our own implementation.** Circular.
  Goldens come from `pdftotext`/`pandoc`/etc only.
- **Auto-merging the PR.** Open it, leave for human.
- **Running `dotnet build` locally.** No SDK on user's machine.
- **Triggering the release workflow.** Don't push tags. Branch
  pushes never trigger release workflows; that's safe.
- **Polling CI in tight loops.** Use a single `gh run watch <ID>`
  call per push, or wait the natural ~1-2 minute build time before
  checking once.

---

## Communication

The agent does not message the user during execution. The PR body
and SUMMARY.md are the only channels. If blocked per the
failure-modes section, write to SUMMARY.md and stop.

---

## Self-loop entry contract (for `/goal` command)

When the agent (re-)reads this spec to decide its next action:

1. Run `git branch --list experiment/doc-readers` — if missing, start
   from Phase 0.
2. If branch exists, run `git log experiment/doc-readers --oneline
   | wc -l` and check current PR state via `gh pr list --head
   experiment/doc-readers --state open`.
3. If PR is open and CI is green, evaluate Done criteria #1-#8 in
   order. The first one that fails tells you what to do next.
4. If push count ≥ 20, check failure-mode escape hatch.
5. Resume from the most advanced phase whose work is incomplete.

The agent should be **resumable**: stopping and restarting `/goal` on
this spec at any point should converge on the same end state. All
intermediate state lives in git (branch state) + the test artifact
(`test-results.trx` from the most recent CI run).
