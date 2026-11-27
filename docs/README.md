# docs/

Current, durable reference for ps-bash:

- `specs/`: per-subsystem specs; `specs/README.md` is the index a guard test checks.
- `install.md`, `agent-setup.md`, `shell-guide.md`, `sessions.md`, `strata-integration.md`, `known-issues.md`: operating guides.
- `planning/production-user-journeys.md` and the `production-readiness-review-*` reports: the product contract and its release evidence.
- `bugs/`: incident write-ups that code, CI or other docs cite; `testing/`: the QA rubric and parity audit.
- `spikes/`: the active XFAIL backlog brief and the fleet gate tools CLAUDE.md runs; `research/`: on-cd hook patterns.
- `fleet/prompts/`: the review gate contract `.worktrack/templates.json` loads; `solutions/`: compound-engineering learnings.

The website (Astro Starlight) is in `site/`. Plans, specs-in-progress and in-flight work are tracked
in worktrack workspace `ps-bash`. A plan a task cites is committed under `docs/plans/` while the task
is open, and removed once it lands.
