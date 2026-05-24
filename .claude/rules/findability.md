# FINDABILITY DOCTRINE. GLOBAL. KEEP TINY (loaded every session).

文言：導航以圖不以技；規範依glob，技司多步；重構貴名近鏈短，勝於增文。

## 1. NAVIGATION = STATIC MAP, NOT RETRIEVAL, NOT SKILLS.

- `CODE_MAP.md` is the nav primitive. Top-of-context, small, EVERGREEN. Edit on structure move, not detail.
- A compressed static index beats on-demand retrieval. Vercel evals (Jan 2026): 8KB compressed docs index = 100% pass; skills = 79% (nudged) / 53% (default = no docs). Aider PageRank repo map = token-budget baseline.
- DO NOT put a codebase map in a skill. Skills go uninvoked (~56% of cases). Activation problem.
- Small evergreen map = justified. Large prose map = NOT. Detail → `docs/specs/*` (`@`-linked in CLAUDE.md, never orphan).

## 2. KNOWLEDGE PLACEMENT. RIGHT SURFACE OR IT ROTS.

- FACT TRUE EVERYWHERE → passive global context (CLAUDE.md, this rule). Keep terse.
- FILE-CLASS CONVENTION → path-scoped rule (`paths:` glob frontmatter). Loads only in that dir. Verify the glob matches reality (stale glob = dead rule).
- MULTI-STEP WORKFLOW THE AGENT EXECUTES → skill.
- DEEP REFERENCE → `docs/specs/*`, `@`-linked. Not auto-loaded if huge (say so, like runtime-migrated-cmdlets).

## 3. REFACTOR THE CODE, NOT THE DOCS AROUND IT.

Highest-leverage findability = search-friendly CODE. Not more documentation.

- NAMES carry meaning. LLMs lean hard on identifiers (obfuscation study arXiv 2510.03178). Name for search.
- LOCALITY. Keep related code together; one concern per file. God-class = unfindable.
- SHORT CALL CHAINS. Perf drops 16.8–45.7% as call-graph complexity grows (Lost-in-the-Middle, Liu TACL 2024; DynaCode, ACL 2025 Findings).
- Scaffolding ≠ cure. Agent scaffolding yields only marginal context-retrieval gains (ContextBench arXiv 2602.05892). Fix the code.

## 4. KEEP IT SMALL.

Every global rule re-enters context each session. Terse invariants here; detail in `@`-specs. Compress (caveman/文言). No prose.
