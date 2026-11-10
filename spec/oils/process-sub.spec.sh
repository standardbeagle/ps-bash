## Imported from the Oils spec tests (process-sub.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Process sub input
f=process-sub.txt
{ echo 1; echo 2; echo 3; } > $f
cat <(head -n 2 $f) <(tail -n 2 $f)
## STDOUT:
1
2
2
3
## END
## status: 0
## timeout: 10

#### Process sub from external process to stdin
seq 3 > >(tac)
## STDOUT:
3
2
1
## END
## status: 0
## XFAIL ps-bash gap-oils-process-sub process-sub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Process sub from shell to stdin
{ echo 1; echo 2; echo 3; } > >(tac)
## STDOUT:
3
2
1
## END
## status: 0
## XFAIL ps-bash gap-oils-process-sub process-sub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### $(<file) idiom with process sub
echo FOO >foo

# works in bash and zsh
echo $(<foo)

# this works in zsh, but not in bash
tr A-Z a-z < <(<foo)

cat < <(<foo; echo hi)
## STDOUT:
FOO
foo
hi
## END
## status: 0
## timeout: 10

#### process sub in background &

cat <(seq 3; sleep 0.1) & wait

echo sync

# This one escapes, and the shell should still exit
cat <(sleep 0.1) &

echo fork
## STDOUT:
1
2
3
sync
fork
## END
## status: 0
## timeout: 10

#### status code is available

shopt --set parse_at

cat <(seq 2; exit 2) <(seq 3; exit 3)

case $SH in bash*|zsh) exit ;; esac

echo status @_process_sub_status
echo done
## STDOUT:
1
2
1
2
3
## END
## status: 0
## timeout: 10
## real-launcher

#### shopt -s process_sub_fail

case $SH in bash*|zsh) exit ;; esac

shopt --set parse_at

cat <(echo a; exit 2) <(echo b; exit 3)
echo status=$? ps @_process_sub_status

echo __
shopt -s process_sub_fail

cat <(echo a; exit 2) <(echo b; exit 3)
echo status=$? ps @_process_sub_status

# Now exit because of it
set -o errexit

cat <(echo a; exit 2) <(echo b; exit 3)
echo status=$? ps @_process_sub_status
## stdout:
## status: 0
## timeout: 10
## real-launcher

#### process subs and pipelines together

# zsh is very similar to bash, but don't bother with the assertions
case $SH in bash*|zsh) exit ;; esac

shopt --set parse_at

f() {
  cat <(seq 1; exit 1) | {
    cat <(seq 2; exit 2) <(seq 3; exit 3)

    # 2022-11 workaround for race condition: sometimes we get pipeline=141 4
    # instead of pipeline=0 4, which means that the first 'cat' got SIGPIPE.
    # If we make this part of the pipeline take longer, then 'cat' should have
    # a chance to finish.

    sleep 0.01

    (exit 4)
  }
  echo status=$?
  echo process_sub @_process_sub_status
  echo pipeline @_pipeline_status
  echo __
}

f
## stdout:
## status: 0
## timeout: 10
## real-launcher
