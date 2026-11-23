#!/usr/bin/env bash
# Tests for scripts/remote-test.sh: the remote side's worktree lifecycle, against a throwaway scratch root and a
# stub tman that records its arguments, plus the local side's --dry-run plan. Needs bash and git; the
# live-process checks need /proc, so run it on a Linux box, e.g. through the script itself:
#   scripts/remote-test.sh --rm build3 -- 'bash scripts/remote-test.test.sh'
set -u
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
rt=$here/remote-test.sh
tmp=$(mktemp -d); holder=''
trap '[ -n "$holder" ] && kill "$holder" 2>/dev/null; rm -rf "$tmp"' EXIT
fails=0 n=0
ok() { n=$((n + 1)); if [ "$1" = 0 ]; then echo "ok $n - $2"; else echo "not ok $n - $2"; fails=$((fails + 1)); fi; }
check() { local d=$1; shift; "$@" >/dev/null 2>&1; ok $? "$d"; }

export PSBASH_SCRATCH_ROOT=$tmp/root PSBASH_REMOTE_TMAN=$tmp/tman STUB_LOG=$tmp/tman.log
cat > "$tmp/tman" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$STUB_LOG"
while [ "$1" != -- ]; do shift; done; shift; exec "$@"
EOF
chmod +x "$tmp/tman"
b64() { printf %s "$1" | base64 | tr -d '\n'; }
remote() { bash "$rt" --remote "$@"; }
run() { remote run "$1" "$2" "$(b64 "$3")" "${4:-0}" 10m 2h "$(b64 test-origin)"; }

# A source repo with two commits; bin/ is ignored like the real repo's.
src=$tmp/src; git init -q "$src"
git -C "$src" config user.email t@t; git -C "$src" config user.name t
printf 'bin/\n' > "$src/.gitignore"; echo one > "$src/f"; git -C "$src" add -A; git -C "$src" commit -qm one
sha1=$(git -C "$src" rev-parse HEAD)
echo two > "$src/f"; git -C "$src" commit -qam two; sha2=$(git -C "$src" rev-parse HEAD)
root=$PSBASH_SCRATCH_ROOT
push() { git -C "$src" push -q -f "$root/.shared.git" "$1:refs/scratch/$2"; }

remote prep; ok $? "prep creates the shared bare repo"
check "the shared repo is bare" test "$(git -C "$root/.shared.git" rev-parse --is-bare-repository)" = true

push "$sha1" t1
out=$(run t1 "$sha1" 'cat f; exit 7'); rc=$?
ok $([ $rc = 7 ]; echo $?) "a run exits with the command's status (got $rc)"
check "the command ran in the worktree at the pushed commit" test "$(echo "$out" | tail -1)" = one
check "the run joined tman's default queue with the agent label" \
    grep -q -- '^run --queue default --label agent=t1 --label repo=ps-bash --stall 10m --max-time 2h -- bash -c' "$STUB_LOG"
check "the worktree hangs off the shared repo" test -f "$root/t1/.git"

mkdir "$root/t1/bin"; touch "$root/t1/bin/keep" "$root/t1/junk"
push "$sha2" t1
out=$(run t1 "$sha2" 'cat f'); ok $? "a second run under the same name succeeds"
check "the reused worktree moved to the new commit" test "$out" = "$(printf 'remote-test: %s:%s at %s; tman queue default, label agent=t1\ntwo' "$(hostname)" "$root/t1" "${sha2:0:10}")"
check "ignored build output survives a reuse" test -e "$root/t1/bin/keep"
check "untracked files do not survive a reuse" test ! -e "$root/t1/junk"

if [ -d /proc/self ]; then
    (cd "$root/t1" && exec tail -f /dev/null) & holder=$!
    for _ in 1 2 3 4 5 6 7 8 9 10; do [ "$(readlink /proc/$holder/cwd 2>/dev/null)" = "$root/t1" ] && break; read -t 0.1 < /dev/zero 2>/dev/null; done
    run t1 "$sha2" 'true' >/dev/null 2>&1; rc=$?
    ok $([ $rc = 2 ]; echo $?) "a run refuses a worktree a live process is using (got $rc)"
    remote done t1 >/dev/null 2>&1
    check "done keeps a worktree a live process is using" test -d "$root/t1"
    remote prune 0 0 >/dev/null 2>&1
    check "prune keeps a worktree a live process is using" test -d "$root/t1"
    kill "$holder"; wait "$holder" 2>/dev/null; holder=''
else
    echo "# skipped the live-process checks: no /proc"
fi

push "$sha1" t2
run t2 "$sha1" 'true' 1 >/dev/null; ok $? "--rm run succeeds"
check "--rm removes the worktree" test ! -e "$root/t2"
check "--rm drops the scratch ref" bash -c "! git -C '$root/.shared.git' rev-parse -q --verify refs/scratch/t2"

run ../evil "$sha1" 'true' >/dev/null 2>&1; rc=$?
ok $([ $rc = 2 ]; echo $?) "a name that leaves the root is refused (got $rc)"
check "nothing was created outside the root" test ! -e "$tmp/evil"

touch -d '2 days ago' "$root/.meta/t1"
out=$(remote prune 24 1); ok $? "prune --dry-run succeeds"
check "prune --dry-run names the idle worktree" grep -q 'would remove t1 (idle 4[0-9]h)' <<< "$out"
check "prune --dry-run removes nothing" test -d "$root/t1"
push "$sha1" t3; run t3 "$sha1" true >/dev/null
remote prune 24 0 >/dev/null; ok $? "prune succeeds"
check "prune removes the idle worktree" test ! -e "$root/t1"
check "prune keeps a recently used worktree" test -d "$root/t3"
out=$(remote ls); check "ls lists the remaining worktree" grep -q '^t3 ' <<< "$out"
remote done t3 >/dev/null; check "done removes the worktree" test ! -e "$root/t3"
out=$(remote done t3); check "done on a missing worktree is a no-op" grep -q 'no worktree t3' <<< "$out"

# Local side: the plan --dry-run prints for a run from this checkout.
out=$(cd "$here" && bash "$rt" --dry-run --name t9 build3 HEAD -- dotnet test 'a b' 2>&1); ok $? "--dry-run succeeds"
check "--dry-run pushes HEAD to the agent's scratch ref" grep -q "^+ git push -f build3:work/scratch/ps-bash/.shared.git $(git -C "$here" rev-parse HEAD):refs/scratch/t9\$" <<< "$out"
check "--dry-run runs under tman's default queue by full path" grep -q '^+ on build3 in ~/work/scratch/ps-bash/t9: ~/.local/bin/tman run --queue default --label agent=t9 ' <<< "$out"
check "--dry-run keeps the command's quoting" grep -q 'dotnet\\ test\\ a\\\\\\ b' <<< "$out"
out=$(cd "$here" && bash "$rt" --dry-run --name '../x' build3 -- true 2>&1); rc=$?
ok $([ $rc = 2 ]; echo $?) "a bad --name is refused locally (got $rc)"

echo "1..$n"
[ $fails = 0 ]
