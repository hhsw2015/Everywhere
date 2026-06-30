# OpenCLI parity matrix

Auto-rendered from `parity-matrix-opencli.json` (do not edit).
Upstream sha: `9161d99d96ec107cd77f13a30315614129179a1a` (`v1.8.5`).

**Totals**: have=2, wont-do=1, blocked=3.

| site | name | strategy | browser | tier | status | acceptance | notes |
|------|------|----------|---------|------|--------|------------|-------|
| 36kr | hot | public | yes | value-add | blocked | bench:36kr-hot | DOM scrape; lands in Phase 2 once OpenDiaPageBridge is wired. |
| bilibili | hot | public | yes | value-add | blocked | bench:bilibili-hot | Second DOM scrape; Phase 2. |
| bilibili | me | cookie | yes | niche | blocked | manual:agent-host | Cookie-via-evaluate; manual tier (requires a logged-in session). |
| 36kr | news | public | no | core | have | bench:36kr-news | RSS feed; PUBLIC strategy; bench CI tier. |
| pypi | downloads | public | no | core | have | bench:pypi-downloads | JSON API; PUBLIC strategy; replaces hackernews/top which moved to pipeline DSL in v1.8.5. |
| hackernews | top | public | no | core | wont-do | none | v1.8.5 moved HN adapters to the pipeline DSL; SPEC §2.4 #1 keeps the pipeline runner out-of-scope. Revisit if upstream restores the func shape. |
