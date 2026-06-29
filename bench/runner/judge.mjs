// LLM-majority judge for bench fixtures. spec §11.6.
// Pre-check: substring match against expected.answer -> 1.0, skip LLM.
// Round 1: 3 Claude votes ∈ {0,1}. 3/3 -> 1.0. 0/3 -> 0.0.
// Tie (1/3 or 2/3) → tie-breaker (4 more votes). 7/7 unanimous required for ≥0.95.
//
// Token ratio: tokens_median(ours) / tokens_median(ab); pass requires
// correctness ≥ 0.95 AND ratio ≤ 1.10 per SPEC §1.

export function precheck(expected, actual) {
  if (!expected || !actual) return false;
  const norm = (s) => String(s).replace(/\s+/g, " ").trim().toLowerCase();
  return norm(actual).includes(norm(expected));
}

export function median(xs) {
  if (!xs || xs.length === 0) return 0;
  if (xs.length < 3) return xs.reduce((a, b) => a + b, 0) / xs.length;
  const s = [...xs].sort((a, b) => a - b);
  const trimmed = s.slice(1, -1);
  const mid = Math.floor(trimmed.length / 2);
  return trimmed.length % 2 === 1
    ? trimmed[mid]
    : (trimmed[mid - 1] + trimmed[mid]) / 2;
}

const JUDGE_SYSTEM =
  "You are a strict bench judge. Compare the agent's answer against the expected answer. " +
  "Return ONLY a single character: 1 if the answer is correct (semantic match, ignore extra wording), 0 otherwise.";

async function llmVote(expected, actual) {
  const baseUrl = process.env.ANTHROPIC_BASE_URL;
  const token = process.env.ANTHROPIC_AUTH_TOKEN || process.env.ANTHROPIC_API_KEY;
  if (!baseUrl || !token) throw new Error("judge: ANTHROPIC_BASE_URL + ANTHROPIC_AUTH_TOKEN required");
  const r = await fetch(baseUrl.replace(/\/$/, "") + "/v1/messages", {
    method: "POST",
    headers: {
      "content-type": "application/json",
      "x-api-key": token,
      "anthropic-version": "2023-06-01",
      "authorization": `Bearer ${token}`,
    },
    body: JSON.stringify({
      model: "claude-sonnet-4-6",
      max_tokens: 4,
      temperature: 0,
      system: JUDGE_SYSTEM,
      messages: [{ role: "user", content: `Expected: ${expected}\n\nActual: ${actual}\n\nCorrect? Reply with 1 or 0.` }],
    }),
  });
  if (!r.ok) throw new Error(`judge: vote HTTP ${r.status}: ${await r.text()}`);
  const j = await r.json();
  const txt = (j.content?.[0]?.text || "").trim();
  return txt.startsWith("1") ? 1 : 0;
}

export async function judge(input) {
  const ratio = input.tokensRuns && input.abMedian
    ? median(input.tokensRuns) / input.abMedian
    : null;
  if (precheck(input.expectedAnswer, input.actualAnswer)) {
    return { correctness: 1.0, judge_votes: [], token_ratio: ratio, pass: ratio === null || ratio <= 1.10 };
  }
  const votes = [];
  for (let i = 0; i < 3; i++) votes.push(await llmVote(input.expectedAnswer, input.actualAnswer));
  const sum = votes.reduce((a, b) => a + b, 0);
  if (sum === 3) return { correctness: 1.0, judge_votes: votes, token_ratio: ratio, pass: ratio === null || ratio <= 1.10 };
  if (sum === 0) return { correctness: 0.0, judge_votes: votes, token_ratio: ratio, pass: false };
  for (let i = 0; i < 4; i++) votes.push(await llmVote(input.expectedAnswer, input.actualAnswer));
  const total = votes.reduce((a, b) => a + b, 0);
  const correctness = total / 7;
  return {
    correctness,
    judge_votes: votes,
    token_ratio: ratio,
    pass: correctness >= 0.95 && (ratio === null || ratio <= 1.10),
  };
}

async function main() {
  const fixtureId = process.argv[2];
  if (!fixtureId) {
    console.error("usage: node judge.mjs <fixture-id>");
    process.exit(2);
  }
  const fs = await import("node:fs/promises");
  const path = await import("node:path");
  const { fileURLToPath } = await import("node:url");
  const here = path.dirname(fileURLToPath(import.meta.url));
  const root = path.resolve(here, "..", "..");
  const expected = JSON.parse(await fs.readFile(path.join(root, "bench/fixtures", fixtureId, "expected.json"), "utf8"));
  const results = JSON.parse(await fs.readFile(path.join(root, "bench/results/bench-results.json"), "utf8"));
  const row = results.find((r) => r.fixture === fixtureId);
  if (!row?.ours) { console.error(`no 'ours' result for ${fixtureId}`); process.exit(3); }
  const verdict = await judge({
    fixture: fixtureId,
    expectedAnswer: expected.answer,
    actualAnswer: row.ours.answer,
    tokensRuns: row.ours.tokens_runs ?? [row.ours.tokens],
    abMedian: expected.tokens_median,
  });
  console.log(JSON.stringify({ fixture: fixtureId, ...verdict }, null, 2));
}

if (process.argv[1] && process.argv[1].endsWith("judge.mjs")) {
  main().catch((e) => { console.error(e); process.exit(1); });
}
