# Bench failure report

**Fixture**: <id>
**Date**: <ISO8601>
**Reporter**: <user>

## Failure mode
- [ ] Substrate failure (tool returned wrong data)
- [ ] Routing failure (agent picked the wrong tool)
- [ ] Judge disagreement (humans say correct but LLM judge said 0)
- [ ] Token blowout (tokens > ab * 1.10 but correctness ≥ 0.95)

## Repro
```
bash bench/runner/run-ours.sh <fixture-id>
```

## Expected vs actual
- Expected (from `expected.json.answer`): <…>
- Actual (from `bench-results.json.ours.answer`): <…>

## Suggested next move
- … (e.g. tighten snapshot heuristic in opendia.snapshot, fold edge case
  into `compact_tree`, lower fixture difficulty, etc.)
