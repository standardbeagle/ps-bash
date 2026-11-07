# Platform-independence linters: task list

Goal: let an agent (or CI) verify that a bash script or `-c` command behaves the same on Windows, Linux and macOS,
and that a ps-bash difference is classified as a platform difference rather than a gap. Source of truth for what
differs is `docs/specs/intentional-differences.md` (section "Platform (Windows)"). Spec XFAILs caused by those
differences carry the id `platform-windows-<topic>` (not `gap-oils-*`), e.g. `platform-windows-env-case`.

Each task is independent and ships with tests at its own layer. Order is by value to agents.

## T1. Platform-difference registry (data, no code behaviour)
- One machine-readable list (`docs/specs/platform-differences.json`): id, topic, bash construct pattern, platforms
  affected, severity (silent-wrong vs error), link to the intentional-differences row.
- The XFAIL id validator (Spec.Tests) requires every `platform-*` id to exist in the registry, and every `gap-*` id
  to exist in spec-gaps.md. Today ids are free-form.
- Done when: a spec with an unknown `platform-` id fails the corpus test.

## T2. `ps-bash lint --portable` static linter (transpiler layer)
- Walks the parsed AST (BashParser output, no execution) and reports constructs whose result depends on the OS.
- Rules, each one a pure function over the AST with its own unit tests:
  - R1 variable names differing only in case within one script (`x=1; X=2`): one variable on Windows.
  - R2 literal Windows or Unix absolute paths (`C:\...`, `/mnt/c`, `/home/x`, `/tmp`, `/etc`, `/proc`, `/dev/shm`).
  - R3 `$PATH`/`$IFS`-style splitting on `:` of values that may contain drive letters.
  - R4 permission-dependent tools (`stat -c %a`, `ls -l` parsing, `find -perm`, `chmod` then test `-x`).
  - R5 owner/inode/link-count output used in logic (`stat %u %i %h`, `ls -l` column 3).
  - R6 case-sensitive file names (two paths differing only in case, `[ -e foo ] && [ -e FOO ]`).
  - R7 symlink use without a fallback, `ln -s` on a path the script later reads.
  - R8 `/dev/fd`, `/proc/self`, `/dev/tcp`, `mkfifo`, `$$`-derived temp names across processes.
  - R9 raw bytes in variables (`x=$(printf '\xe9')`): lost on Linux/macOS, kept on Windows.
  - R10 line endings: `\r` in here-docs or `read` input assuming LF, `$'\r'`.
  - R11 sub-second time (`date +%N`, `%3N`), `sleep` fractions, `date -d` formats.
  - R12 external tools not present everywhere (GNU-only flags: `sed -i ''` vs `sed -i`, `date -d`, `readlink -f`,
    `stat -c`, `xargs -r`, `grep -P`); table keyed by tool and flag.
- Output: `file:line:col rule-id message`, exit 1 on findings, `--json` for agents, `--rule`/`--disable` selectors.
- Done when: each rule has a positive and a negative test, and the 641-file real-world corpus runs without a crash.

## T3. Cross-platform differential runner (oracle layer)
- Run the same snippet under the bash oracle on Linux (WSL Ubuntu-24.04), ps-bash on Windows, and, in CI, ps-bash on
  Linux and macOS; diff stdout, stderr and exit status.
- A difference is allowed only when the snippet is tagged with a registry id (T1); an untagged difference fails.
- Reuses `AssertOracle.GoldenAsync`; the Linux/macOS legs are goldens recorded once, so local runs need no WSL.
- Done when: a deliberate `x=1; X=2` snippet is reported as the registered `platform-windows-env-case` difference
  and an unregistered divergence fails the run.

## T4. Spec-corpus platform classifier
- A script that reruns every `## XFAIL ps-bash gap-*` case, then re-runs it with Windows-only features neutralised
  (case-distinct names, no `printenv.py`, no `/tmp`) to decide whether the diff is platform-caused.
- Proposes re-tagging to `platform-windows-*`; a human confirms. Output is a diff of marker lines, not an edit.
- Done when: it proposes the two `assign.spec` cases already re-tagged by hand and no `gap-*` case that passes the
  neutralised run on both platforms.

## T5. Runtime portability guard (opt-in `set -o portable` / `PSBASH_PORTABLE=1`)
- At run time, warn on stderr (never change behaviour) when a script performs a platform-dependent operation the
  linter could not decide statically (dynamic names differing in case, path rewriting applied, mode bits synthesised).
- Warning text names the registry id, so an agent can look it up.
- Done when: the warning fires once per rule per run, is silent by default, and costs nothing when off.

## T6. Docs and agent guidance
- `docs/agent-setup.md`: when to run `ps-bash lint --portable`, how to read a rule id, how to suppress with a
  `# portable: ignore R2` comment on the line.
- `docs/specs/xfail-triage.md`: replace the loose WINDOWS label with the `platform-windows-<topic>` id convention.
- Done when: both docs link the registry and the linter command.
