#!/usr/bin/env bash
# Build and test this checkout on a fleet Linux box, inside that box's tman machine queue.
#
#   scripts/remote-test.sh [--name N] [--dirty] [--rm] [--dry-run] <host> [<ref>] -- <cmd...>
#   scripts/remote-test.sh done  [--name N] [--dry-run] <host>       remove this agent's worktree (end of its work)
#   scripts/remote-test.sh ls    <host>                              every scratch worktree: idle hours, size, live
#   scripts/remote-test.sh prune [--older-than H] [--dry-run] <host> remove worktrees idle >= H hours (default 24)
#
#   scripts/remote-test.sh build3 -- 'dotnet test src/PsBash.Core.Tests -c Debug --filter "FullyQualifiedName~Sed"'
#   scripts/remote-test.sh --dirty build3 -- 'dotnet build ps-bash.sln -c Debug'
#
# Hosts: build3 (32 cores, 28 GB) first, build2 (= beagle-inspiron, 8 cores) second. Neither has pwsh, so the
# .tman.kdl aliases and Pester do not run there; <cmd> is plain bash in the worktree root.
#
# Why (2026-11-23): agents cloned or rsynced their own ~/work/agent-* checkouts and never removed them, chose a
# host by sleep-polling /proc/loadavg (one agent slept 14 minutes while build3 sat at load 3), and polled
# marker files for completion. Every fleet box already runs a tman machine queue "default" that admits runs by
# measured cores and memory and queues the rest, so:
#  - <cmd> runs as `~/.local/bin/tman run --queue default --label agent=<name>`. tman waits for room; never
#    check load or sleep first. (Full path: ~/.local/bin is not on a non-interactive ssh PATH.)
#  - This command stays in the foreground and exits with <cmd>'s status (75: tman stopped it, e.g. a queue
#    timeout or `tman kill`; re-run). Start it with the Bash tool's run_in_background and wait for the
#    notification. No sleep loops, no marker files.
#  - Code travels by `git push` into one bare repo per host, ~/work/scratch/ps-bash/.shared.git, and each
#    agent gets a worktree ~/work/scratch/ps-bash/<name> off it. A push sends only missing objects and a
#    worktree shares the object store: no clones, tarballs or bundles. Later runs under the same name reuse
#    the worktree (bin/obj survive, so builds are incremental) until `done` removes it. tman cannot reap
#    directories, so the lifecycle is this script's: `done` at the end of each agent's work, `prune` for the
#    ones nobody finished. Neither touches a worktree a process is still using.
#
# <ref> defaults to HEAD. --dirty ships the working tree instead: tracked changes plus untracked files under
# src docs spec tests scripts, as a commit made through a temporary index (no local ref, index or branch moves).
# --name defaults to this checkout's directory name (an agent worktree's agent-xxxx); agents sharing one
# checkout pass distinct names. --rm removes the worktree after this run. --stall/--max-time pass to tman
# (default 10m / 2h). --dry-run prints what would run and touches nothing.
set -u

ROOT_REL=work/scratch/ps-bash              # relative to the remote $HOME; git push URLs use it as is
die() { echo "remote-test: $*" >&2; exit 2; }
valid_name() { [[ $1 =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; }

# ---------------------------------------------------------------- remote side (runs on the fleet box)
remote_main() {
    local root=${PSBASH_SCRATCH_ROOT:-$HOME/$ROOT_REL}
    local tman=${PSBASH_REMOTE_TMAN:-$HOME/.local/bin/tman}
    local shared=$root/.shared.git meta=$root/.meta
    local op=$1; shift

    lock() { mkdir -p "$root"; exec 9>"$root/.lock"; if command -v flock >/dev/null; then flock 9; fi; }
    unlock() { exec 9>&-; }
    # A process whose cwd is inside $1: a run, a shell, an agent's probe. /proc only; elsewhere nothing is live.
    live_in() {
        local p c
        [ -d /proc/self ] || return 1
        for p in /proc/[0-9]*; do
            c=$(readlink "$p/cwd" 2>/dev/null) || continue
            case $c in "$1" | "$1"/*) return 0 ;; esac
        done
        return 1
    }
    idle_hours() { # name -> hours since its last run (meta mtime, else the directory's)
        local f=$meta/$1; [ -e "$f" ] || f=$root/$1
        echo $(( ($(date +%s) - $(stat -c %Y "$f")) / 3600 ))
    }
    remove() { # name: worktree, ref, meta
        local wt=$root/$1
        if live_in "$wt"; then echo "remote-test: $wt is in use by a live process; kept" >&2; return 1; fi
        lock
        if [ -e "$wt/.git" ]; then git -C "$shared" worktree remove --force "$wt" 2>/dev/null || rm -rf "$wt"; else rm -rf "$wt"; fi
        git -C "$shared" worktree prune 2>/dev/null
        git -C "$shared" update-ref -d "refs/scratch/$1" 2>/dev/null
        rm -f "$meta/$1"
        unlock
        echo "remote-test: removed $(hostname):$wt"
    }
    names() { local d; for d in "$root"/*/; do [ -d "$d" ] && basename "$d"; done; }

    case $op in
    prep)
        [ -d "$shared" ] && return 0
        lock; [ -d "$shared" ] || git init -q --bare "$shared"; mkdir -p "$meta"; unlock ;;
    run) # name sha cmd-b64 rm stall max-time from-b64
        local name=$1 sha=$2 cmd rm=$4 stall=$5 maxtime=$6 from rc
        valid_name "$name" || die "bad name '$name'"
        cmd=$(printf %s "$3" | base64 -d) from=$(printf %s "$7" | base64 -d)
        local wt=$root/$name
        if live_in "$wt"; then die "a run is still active in $(hostname):$wt; wait for it or pass another --name"; fi
        lock
        if [ -e "$wt/.git" ]; then
            git -C "$wt" checkout -q -f --detach "$sha" && git -C "$wt" clean -fdq   # ignored bin/obj stay
        elif [ -e "$wt" ]; then
            unlock; die "$wt exists but is not a scratch worktree; remove it by hand"
        else
            git -C "$shared" worktree prune && git -C "$shared" worktree add -q -f --detach "$wt" "$sha"
        fi || { unlock; die "could not put $wt at $sha"; }
        mkdir -p "$meta"; printf 'sha=%s\nfrom=%s\ncmd=%s\n' "$sha" "$from" "$cmd" > "$meta/$name"
        unlock
        echo "remote-test: $(hostname):$wt at ${sha:0:10}; tman queue default, label agent=$name"
        cd "$wt" || die "cannot enter $wt"
        # Node reuse and the shared compiler server would leave processes holding bin/obj after the run.
        export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
        "$tman" run --queue default --label "agent=$name" --label repo=ps-bash --stall "$stall" --max-time "$maxtime" \
            -- bash -c "$cmd"
        rc=$?
        cd "$root" || true
        touch "$meta/$name"
        if [ "$rm" = 1 ]; then remove "$name"; fi
        return $rc ;;
    done)
        valid_name "$1" || die "bad name '$1'"
        if [ -e "$root/$1" ] || [ -e "$meta/$1" ]; then remove "$1"; else echo "remote-test: no worktree $1 on $(hostname)"; fi ;;
    ls)
        local n live
        printf '%-28s %6s %6s %-10s %s\n' NAME IDLE_H SIZE SHA LIVE
        for n in $(names); do
            live=no; live_in "$root/$n" && live=yes
            printf '%-28s %6s %6s %-10s %s\n' "$n" "$(idle_hours "$n")" "$(du -sh "$root/$n" 2>/dev/null | cut -f1)" \
                "$(git -C "$root/$n" rev-parse --short=10 HEAD 2>/dev/null)" "$live"
        done ;;
    prune) # hours dry
        local hours=$1 dry=$2 n h
        for n in $(names); do
            h=$(idle_hours "$n")
            if [ "$h" -lt "$hours" ]; then continue; fi
            if [ "$dry" = 1 ]; then echo "remote-test: would remove $n (idle ${h}h)"; else remove "$n"; fi
        done
        [ "$dry" = 1 ] || git -C "$shared" gc --auto --quiet 2>/dev/null
        return 0 ;;
    *) die "unknown remote op '$op'" ;;
    esac
}

if [ "${1-}" = --remote ]; then shift; remote_main "$@"; exit; fi

# ---------------------------------------------------------------- local side (an agent's checkout)
usage() { sed -n '2,/^set -u/p' "${BASH_SOURCE[0]}" | sed '$d; s/^# \{0,1\}//'; exit "${1:-0}"; }

op=run
case ${1-} in done | ls | prune) op=$1; shift ;; -h | --help | '') usage ;; esac
name='' dirty=0 rm=0 dry=0 hours=24 stall=10m maxtime=2h pos=()
while [ $# -gt 0 ]; do
    case $1 in
    --name) name=${2-}; shift 2 ;;
    --dirty) dirty=1; shift ;;
    --rm) rm=1; shift ;;
    --dry-run) dry=1; shift ;;
    --older-than) hours=${2-}; shift 2 ;;
    --stall) stall=${2-}; shift 2 ;;
    --max-time) maxtime=${2-}; shift 2 ;;
    -h | --help) usage ;;
    --) shift; break ;;
    -*) die "unknown option $1 (see --help)" ;;
    *) pos+=("$1"); shift ;;
    esac
done
host=${pos[0]-}
[ -n "$host" ] || die "no host (build3 first, build2 second)"
[[ $hours =~ ^[0-9]+$ ]] || die "--older-than takes whole hours"

top=$(git rev-parse --show-toplevel 2>/dev/null) || die "run inside a ps-bash checkout"
[ -n "$name" ] || name=$(basename "$top")
valid_name "$name" || die "bad name '$name' (letters, digits, . _ -)"

script_b64=$(tr -d '\r' < "${BASH_SOURCE[0]}" | base64 | tr -d '\r\n')
SSH=(ssh -n -o BatchMode=yes -o ConnectTimeout=15)
remote() { # op args... -> the same script, run on $host from its source text (no copy left behind)
    if [ "$dry" = 1 ]; then echo "+ ssh $host remote-test.sh --remote $*"; return 0; fi
    "${SSH[@]}" "$host" "bash -c \"\$(echo $script_b64 | base64 -d)\" remote-test.sh --remote $*"
}

case $op in
done) remote done "$name"; exit ;;
ls) remote ls; exit ;;
prune) d=$dry; dry=0; remote prune "$hours" "$d"; exit ;;   # a prune dry run asks the host what it would remove
esac

[ $# -gt 0 ] || die "no command after -- (see --help)"
if [ $# -eq 1 ]; then cmd=$1; else cmd=$(printf '%q ' "$@"); fi
ref=${pos[1]-HEAD}

if [ "$dirty" = 1 ]; then
    idx="$(git rev-parse --absolute-git-dir)/remote-test-$$.idx"
    tree=$(cd "$top" && export GIT_INDEX_FILE="$idx" && git read-tree HEAD \
        && git -c core.safecrlf=false add -u -- . \
        && git -c core.safecrlf=false add -- src docs spec tests scripts \
        && git write-tree)
    rc=$?; rm -f "$idx"
    [ $rc -eq 0 ] && [ -n "$tree" ] || die "could not snapshot the working tree"
    sha=$(git commit-tree "$tree" -p HEAD -m "remote-test snapshot for $name") || die "commit-tree failed"
else
    sha=$(git rev-parse --verify -q "$ref^{commit}") || die "unknown ref '$ref'"
fi

b64() { printf %s "$1" | base64 | tr -d '\r\n'; }
url="$host:$ROOT_REL/.shared.git"
if [ "$dry" = 1 ]; then
    echo "+ git push -f $url $sha:refs/scratch/$name"
    echo "+ on $host in ~/$ROOT_REL/$name: ~/.local/bin/tman run --queue default --label agent=$name --label repo=ps-bash --stall $stall --max-time $maxtime -- bash -c $(printf %q "$cmd")"
    [ "$rm" = 1 ] && echo "+ then remove ~/$ROOT_REL/$name"
    exit 0
fi
remote prep || die "cannot reach $host"
git push -q -f "$url" "$sha:refs/scratch/$name" || die "git push to $url failed"
remote run "$name" "$sha" "$(b64 "$cmd")" "$rm" "$stall" "$maxtime" "$(b64 "$(hostname):$top")"
