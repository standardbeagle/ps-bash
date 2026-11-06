## Imported from the Oils spec tests (sh-options-bash.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### SHELLOPTS is readonly
SHELLOPTS=x
echo status=$?

# Setting a readonly variable in osh is a hard failure.
## stdout: status=1
## status: 0
## XFAIL ps-bash gap-oils-sh-options-bash sh-options-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### SHELLOPTS and BASHOPTS are non-empty

# 2024-06 - tickled by Samuel testing Gentoo

if test -v SHELLOPTS; then
  echo 'shellopts is set'
fi
if test -v BASHOPTS; then
	echo 'bashopts is set'
fi

# bash: braceexpand:hashall etc.

echo shellopts ${SHELLOPTS:?} > /dev/null
echo bashopts ${BASHOPTS:?} > /dev/null
## STDOUT:
shellopts is set
bashopts is set
## END
## status: 0
## XFAIL ps-bash gap-oils-sh-options-bash sh-options-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### shopt -s progcomp hostcomplete are stubs (bash-completion)

shopt -s progcomp hostcomplete
echo status=$?

shopt -u progcomp hostcomplete
echo status=$?
## STDOUT:
status=0
status=0
## END
## status: 0
## timeout: 10

#### SHELLOPTS is updated when options are changed
echo $SHELLOPTS | grep -q xtrace
echo $?
set -x
echo $SHELLOPTS | grep -q xtrace
echo $?
set +x
echo $SHELLOPTS | grep -q xtrace
echo $?
## STDOUT:
1
0
1
## END
## status: 0
## XFAIL ps-bash gap-oils-sh-options-bash sh-options-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### SHELLOPTS reflects flags like sh -x

$SH -x -c 'echo $SHELLOPTS' | grep -o xtrace
## stdout: xtrace
## status: 0
## XFAIL ps-bash gap-oils-sh-options-bash sh-options-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### export SHELLOPTS does cross-process tracing

$SH -c '
export SHELLOPTS
set -x
echo 1
$SH -c "echo 2"
' 2>&1 | sed 's/.*sh /sh /g'
## STDOUT:
+ echo 1
1
+ -c 'echo 2'
bash: line 5: -c: command not found
## END
## status: 0
## XFAIL ps-bash gap-oils-sh-options-bash sh-options-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### export SHELLOPTS does cross-process tracing with bash

# calling bash
$SH -c '
export SHELLOPTS
set -x
#echo SHELLOPTS=$SHELLOPTS
echo 1
bash -c "echo 2"
' 2>&1 | sed 's/.*sh /sh /g'
## STDOUT:
+ echo 1
1
sh -c 'echo 2'
+ echo 2
2
## END
## status: 0
## XFAIL ps-bash gap-oils-sh-options-bash sh-options-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### OSH calling bash with SHELLOPTS does not change braceexpand

#echo outside=$SHELLOPTS

# sed pattern to normalize spaces
normalize='s/[ \t]\+/ /g'

bash -c '
#echo bash=$SHELLOPTS
set -o | grep braceexpand | sed "$1"
' unused "$normalize"

env SHELLOPTS= bash -c '
#echo bash2=$SHELLOPTS
set -o | grep braceexpand | sed "$1"
' unused "$normalize"
## STDOUT:
braceexpand on
braceexpand on
## END
## status: 0
## timeout: 10
## real-launcher

#### If shopt --set xtrace is allowed, it should update SHELLOPTS, not BASHOPTS
case $SH in bash) exit ;; esac

shopt --set xtrace
echo SHELLOPTS=$SHELLOPTS
set -x
echo SHELLOPTS=$SHELLOPTS
set +x
echo SHELLOPTS=$SHELLOPTS
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-sh-options-bash sh-options-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
