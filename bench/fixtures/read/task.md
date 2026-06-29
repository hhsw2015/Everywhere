---
id: read
ci_tier: manual
kind: static_html
---
Call `agent_browser_read` (or `browser_read` for the ours side) exactly
once with url=`http://127.0.0.1:7977/` and return the single most
prominent word from the resulting markdown — the city name printed in
the first heading. Do NOT call `agent_browser_open`, `agent_browser_snapshot`,
or any other browser tool. Just `read`, then answer with one word.

Expected answer: `Paris`
