# Spec gaps (oils corpus)

Real behaviour differences behind `## XFAIL ps-bash gap-oils-*` markers. Classification of the whole backlog: see
`docs/plans/2026-11-06-xfail-and-memory-strategy.md` (Phase 1).

## Arithmetic expansion error aborts the current command list (fatal-errors)

bash: `case $(( 42 / 0 )) in (*) echo hi;; esac` prints the error, skips the REST OF THE LINE/LIST (no `hi`) and
leaves `$?` = 1; the next line runs. ps-bash only sets `$?` = 1 and carries on into the `case`. Fixing it needs a
per-top-level-list catch emitted only for lists that contain an arithmetic expansion (about ten emit sites call
`Invoke-BashArith`); not started. `${x?msg}` already ends the script via `BashParamErrorException`.

## Associative array iteration order (assign-extended `declare -p arr`)

bash walks its hash table (FNV-1 hash over 1024 buckets, newest entry first in a bucket), so `declare -A a=([a]=1
[b]=2 [c]=3)` prints `c b a`. ps-bash keeps insertion order. Matching it means emulating that table for `declare -p`,
`${!A[@]}` and `${A[@]}`; scripts must not depend on the order, so this is low priority.

## Job start skew (background wait -n)

Background jobs run on pooled runspaces that are created one after another, so the second & starts ~140 ms after the first on a cold pool; cases that rely on a 60 ms completion gap (wait -n) can see the opposite order. wait -n itself now reaps in completion order. A fix is a pre-warmed pool (cost on every script that uses &) or process-mode jobs (PSBASH_BG_MODE=process, background-jobs.md section 11).
