// LLM-majority judge for bench fixtures. spec §11.6.
// Pre-check: substring match against expected.answer -> 1.0, skip LLM.
// Round 1: 3 Claude-sonnet-4-6 votes ∈ {0,1}. 3/3 -> 1.0. 0/3 -> 0.0.
// 1/3 or 2/3 -> tie-breaker (4 more votes). 7/7 unanimous required for ≥0.95.
//
// Stub: real LLM calls are wired in Phase 3. The shape below mirrors the
// row schema in §11.4 so downstream tooling (lint Rule 14, SUMMARY render)
// can consume judge output today.

export type JudgeInput = {
  fixture: string;
  expectedAnswer: string;
  actualAnswer: string;
  tokensRuns: number[];
};
export type JudgeOutput = {
  correctness: number;
  judge_votes: number[];
  token_ratio: number | null;
  pass: boolean;
};

export function precheck(expected: string, actual: string): boolean {
  if (!expected || !actual) return false;
  const norm = (s: string) => s.replace(/\s+/g, " ").trim().toLowerCase();
  return norm(actual).includes(norm(expected));
}

export function median(xs: number[]): number {
  if (xs.length === 0) return 0;
  if (xs.length < 3) return xs.reduce((a, b) => a + b, 0) / xs.length;
  const s = [...xs].sort((a, b) => a - b);
  // §11.4: drop min + max, take median of remaining.
  const trimmed = s.slice(1, -1);
  const mid = Math.floor(trimmed.length / 2);
  return trimmed.length % 2 === 1
    ? trimmed[mid]
    : (trimmed[mid - 1] + trimmed[mid]) / 2;
}

// LLM call shim — implemented in Phase 3.
async function llmVote(_expected: string, _actual: string): Promise<0 | 1> {
  throw new Error("judge: LLM vote not yet implemented (Phase 3)");
}

export async function judge(input: JudgeInput): Promise<JudgeOutput> {
  if (precheck(input.expectedAnswer, input.actualAnswer)) {
    return { correctness: 1.0, judge_votes: [], token_ratio: null, pass: true };
  }
  const votes: number[] = [];
  for (let i = 0; i < 3; i++) votes.push(await llmVote(input.expectedAnswer, input.actualAnswer));
  const sum = votes.reduce((a, b) => a + b, 0);
  if (sum === 3) return { correctness: 1.0, judge_votes: votes, token_ratio: null, pass: true };
  if (sum === 0) return { correctness: 0.0, judge_votes: votes, token_ratio: null, pass: false };
  // tie-breaker N=4
  for (let i = 0; i < 4; i++) votes.push(await llmVote(input.expectedAnswer, input.actualAnswer));
  const total = votes.reduce((a, b) => a + b, 0);
  const correctness = total / 7;
  return { correctness, judge_votes: votes, token_ratio: null, pass: correctness >= 0.95 };
}
