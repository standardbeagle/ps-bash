---
paths:
  - "CLAUDE.md"
  - "CODE_MAP.md"
  - ".claude/**"
  - "docs/specs/**"
---

# FINDABILITY DOCTRINE (when editing agent instructions/specs)

文言：導航以圖不以技；規範依glob，技司多步；重構貴名近鏈短，勝於增文。

- `CODE_MAP.md` = nav primitive: small, evergreen, always loaded. No codebase map in a skill (skills go uninvoked).
- Placement: fact true everywhere → CLAUDE.md (terse). File-class convention → path-scoped rule (`paths:` glob; verify it matches). Multi-step workflow → skill. Deep reference → `docs/specs/*`, indexed in `docs/specs/README.md`.
- NEVER `@`-link a spec/rule from CLAUDE.md, rules or skills — `@` auto-imports the whole file (blew the 150k instruction budget). Write the path in backticks.
- Prefer search-friendly CODE (names, locality, short call chains) over more docs.
- Every always-loaded line costs every session. Compress; no prose.
