# Oracle cassettes: bash version is not pinned (future task)

Status: open, deliberately NOT started (decision: lock down ps-bash output first; recording corrections later).

## Finding (2026-11-05, from the v0.14.1 release run)
- CI replays `src/PsBash.Differential.Tests/Cassettes/*.cassette` on every OS (`PSBASH_ORACLE_LIVE=0`), so the
  bash side of the oracle is whatever was recorded.
- 1978 of 1984 cassettes carry `bash-version: 5.2.21(1)-release` (Ubuntu-24.04 WSL).
- 6 carry `bash-version: wsl` (21eb665592, 2d00e80b0b, a9a9d299d7, af5d81528a, dbfc6102f8, ef5b952224).
  Cause: `BashLocator` falls back to the literal `"wsl"` when its 3 s `bash --version` query times out
  (`BashLocator.cs`, the `string.IsNullOrEmpty(wslVersion) ? "wsl" : wslVersion` fallback), and
  `AssertOracle` records `host.Version` unchecked.
- Nothing enforces a single oracle bash.

## To do
1. Re-record the 6 cassettes against the verified 5.2.21 bash (`PSBASH_ORACLE_RECORD=1`), not a hand edit.
2. Pin the version (`OracleCassette.PinnedBashVersion`) and refuse to record when `host.Version` differs
   (empty / `wsl` / Git Bash 4.4.x must fail loudly, not fall back).
3. Guard test: every cassette's `bash-version:` equals the pin (assert the corpus is non-empty).
4. Document the pinned oracle in `docs/testing/` so "which bash is correct" has one answer.
