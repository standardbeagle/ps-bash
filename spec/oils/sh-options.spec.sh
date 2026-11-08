## Imported from the Oils spec tests (sh-options.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### $- with pipefail
set -o pipefail -o nounset
echo $-
## stdout: huB
## status: 0
## timeout: 10

#### $- and more options
set -efuC
o=$-
[[ $o == *e* ]]; echo yes
[[ $o == *f* ]]; echo yes
[[ $o == *u* ]]; echo yes
[[ $o == *C* ]]; echo yes
## STDOUT:
yes
yes
yes
yes
## END
## status: 0
## timeout: 10

#### set -o vi/emacs
set -o vi
echo $?
set -o emacs
echo $?
## STDOUT:
0
0
## END
## status: 0
## timeout: 10

#### vi and emacs are mutually exclusive
show() {
  shopt -o -p | egrep 'emacs$|vi$'
  echo ___
};
show

set -o emacs
show

set -o vi
show
## STDOUT:
set +o emacs
set +o vi
___
set -o emacs
set +o vi
___
set +o emacs
set -o vi
___
## END
## status: 0
## timeout: 10

#### nounset
echo "[$unset]"
set -o nounset
echo "[$unset]"
echo end  # never reached
## stdout: []
## status: 1
## timeout: 10

#### -u is nounset
echo "[$unset]"
set -u
echo "[$unset]"
echo end  # never reached
## stdout: []
## status: 1
## timeout: 10

#### -n for no execution (useful with --ast-output)
# NOTE: set +n doesn't work because nothing is executed!
echo 1
set -n
echo 2
set +n
echo 3
# osh doesn't work because it only checks -n in bin/oil.py?
## stdout: 1
## status: 0
## XFAIL ps-bash gap-oils-sh-options sh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### pipefail
# NOTE: the sleeps are because osh can fail non-deterministically because of a
# bug.  Same problem as PIPESTATUS.
{ sleep 0.01; exit 9; } | { sleep 0.02; exit 2; } | { sleep 0.03; }
echo $?
set -o pipefail
{ sleep 0.01; exit 9; } | { sleep 0.02; exit 2; } | { sleep 0.03; }
echo $?
## STDOUT:
0
2
## END
## status: 0
## XFAIL ps-bash gap-oils-sh-options sh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### shopt -p prints 'shopt' options
shopt -p nullglob
shopt -s nullglob
shopt -p nullglob
## STDOUT:
shopt -u nullglob
shopt -s nullglob
## END
## status: 0
## timeout: 10

#### noclobber off
set -o errexit

echo foo > can-clobber
echo status=$?
set +C

echo foo > can-clobber
echo status=$?
set +o noclobber

echo foo > can-clobber
echo status=$?
cat can-clobber
## STDOUT:
status=0
status=0
status=0
foo
## END
## status: 0
## timeout: 10

#### noclobber on

rm -f no-clobber
set -C

echo foo > no-clobber
echo create=$?

echo overwrite > no-clobber
echo overwrite=$?

echo force >| no-clobber
echo force=$?

cat no-clobber
## STDOUT:
create=0
overwrite=1
force=0
force
## END
## status: 0
## timeout: 10

#### set without args lists variables
__GLOBAL=g
f() {
  local __mylocal=L
  local __OTHERLOCAL=L
  __GLOBAL=mutated
  set | grep '^__'
}
g() {
  local __var_in_parent_scope=D
  f
}
g
## STDOUT:
__GLOBAL=mutated
__OTHERLOCAL=L
__mylocal=L
__var_in_parent_scope=D
## END
## status: 0
## timeout: 10

#### set without args and array variables
declare -a __array
__array=(1 2 '3 4')
set | grep '^__'
## stdout: __array=([0]="1" [1]="2" [2]="3 4")
## status: 0
## XFAIL ps-bash gap-oils-sh-options sh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### set without args and assoc array variables (not in OSH)
typeset -A __assoc
__assoc['k e y']='v a l'
__assoc[a]=b
set | grep '^__'
## stdout: __assoc=([a]="b" ["k e y"]="v a l" )
## status: 0
## XFAIL ps-bash gap-oils-sh-options sh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### shopt -q
shopt -q nullglob
echo nullglob=$?

# set it
shopt -s nullglob

shopt -q nullglob
echo nullglob=$?

shopt -q nullglob failglob
echo nullglob,failglob=$?

# set it
shopt -s failglob
shopt -q nullglob failglob
echo nullglob,failglob=$?
## STDOUT:
nullglob=1
nullglob=0
nullglob,failglob=1
nullglob,failglob=0
## END
## status: 0
## timeout: 10

#### shopt -q invalid
shopt -q invalidZZ
echo invalidZZ=$?
## stdout: invalidZZ=1
## status: 0
## timeout: 10

#### shopt -s strict:all
n=2

show-strict() {
  shopt -p | grep 'strict_' | head -n $n
  echo -
}

show-strict
shopt -s strict:all
show-strict
shopt -u strict_argv
show-strict
## STDOUT:
-
-
-
## END
## status: 0
## timeout: 10

#### shopt allows for backward compatibility like bash

# doesn't have to be on, but just for testing
set -o errexit

shopt -p nullglob || true  # bash returns 1 here?  Like -q.

# This should set nullglob, and return 1, which can be ignored
shopt -s nullglob strict_OPTION_NOT_YET_IMPLEMENTED 2>/dev/null || true
echo status=$?

shopt -p nullglob || true
## STDOUT:
shopt -u nullglob
status=0
shopt -s nullglob
## END
## status: 0
## timeout: 10

#### shopt -p -o validates option names
shopt -p -o errexit invalid nounset
echo status=$?
## STDOUT:
set +o errexit
set +o nounset
status=1
## END
## status: 0
## timeout: 10

#### stubbed out bash options
shopt -s ignore_shopt_not_impl
for name in foo autocd cdable_vars checkwinsize; do
  shopt -s $name
  echo $?
done
## STDOUT:
1
0
0
0
## END
## status: 0
## timeout: 10

#### no-ops not shown by shopt -p

shopt -p | grep xpg
echo --
## STDOUT:
shopt -u xpg_echo
--
## END
## status: 0
## timeout: 10

#### $- with -c
# dash's behavior seems most sensible here?
$SH -o nounset -c 'echo $-'
## stdout: huBc
## status: 0
## XFAIL ps-bash gap-oils-sh-options sh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### $- with interactive shell
$SH -c 'echo $-' | grep i || echo FALSE
$SH -i -c 'echo $-' | grep -q i && echo TRUE
## STDOUT:
FALSE
TRUE
## END
## status: 0
## XFAIL ps-bash gap-oils-sh-options sh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### pass short options like sh -e
$SH -e -c 'false; echo status=$?'
## stdout:
## status: 1
## timeout: 10
## real-launcher

#### pass long options like sh -o errexit
$SH -o errexit -c 'false; echo status=$?'
## stdout:
## status: 1
## timeout: 10
## real-launcher

#### pass shopt options like sh -O nullglob
$SH +O nullglob -c 'echo foo *.nonexistent bar'
$SH -O nullglob -c 'echo foo *.nonexistent bar'
## STDOUT:
foo *.nonexistent bar
foo bar
## END
## status: 0
## timeout: 10
## real-launcher

#### interactive shell starts with emacs mode on
case $SH in dash) exit ;; esac
case $SH in bash|*osh) flag='--rcfile /dev/null' ;; esac

code='test -o emacs; echo $?; test -o vi; echo $?'

echo non-interactive
$SH $flag -c "$code"

echo interactive
$SH $flag -i -c "$code"
## STDOUT:
non-interactive
1
1
interactive
0
1
## END
## status: 0
## XFAIL ps-bash gap-oils-sh-options sh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### shopt -p -o prints 'set' options
case $SH in dash|mksh) exit ;; esac

shopt -po nounset
set -o nounset
shopt -po nounset

echo --

shopt -po | egrep -o 'errexit|noglob|nounset'
## STDOUT:
set +o nounset
set -o nounset
--
errexit
noglob
nounset
## END
## status: 0
## timeout: 10
## real-launcher

#### shopt -o prints 'set' options
case $SH in dash|mksh) exit ;; esac

shopt -o | egrep -o 'errexit|noglob|nounset'
echo --
## STDOUT:
errexit
noglob
nounset
--
## END
## status: 0
## timeout: 10
## real-launcher

#### shopt -s nounset works in YSH, not in bash
case $SH in
  *dash|*mksh)
    echo N-I
    exit
    ;;
esac
shopt -s nounset
echo status=$?

# get rid of extra space in bash output
set -o | grep nounset | sed 's/[ \t]\+/ /g'
## STDOUT:
status=1
nounset off
## END
## status: 0
## timeout: 10
## real-launcher

#### Unimplemented options - print, query, set, unset
case $SH in dash|mksh) exit ;; esac

opt_name=xpg_echo

shopt -p xpg_echo
shopt -q xpg_echo; echo q=$?

shopt -s xpg_echo
shopt -p xpg_echo

shopt -u xpg_echo
shopt -p xpg_echo
echo p=$?  # weird, bash also returns a status

shopt xpg_echo >/dev/null
echo noflag=$?

shopt -o errexit >/dev/null
echo set=$?
## STDOUT:
shopt -u xpg_echo
q=1
shopt -s xpg_echo
shopt -u xpg_echo
p=1
noflag=1
set=1
## END
## status: 0
## timeout: 10
## real-launcher

#### Unimplemented options - OSH shopt -s ignore_shopt_not_impl
case $SH in dash|mksh) exit ;; esac

shopt -s ignore_shopt_not_impl

opt_name=xpg_echo

shopt -p xpg_echo
shopt -q xpg_echo; echo q=$?

shopt -s xpg_echo
shopt -p xpg_echo

shopt -u xpg_echo
shopt -p xpg_echo
echo p=$?  # weird, bash also returns a status

shopt xpg_echo >/dev/null
echo noflag=$?

shopt -o errexit >/dev/null
echo set=$?
## STDOUT:
shopt -u xpg_echo
q=1
shopt -s xpg_echo
shopt -u xpg_echo
p=1
noflag=1
set=1
## END
## status: 0
## timeout: 10
## real-launcher

#### shopt -p exit code (regression)
case $SH in dash|mksh) exit ;; esac

shopt -p > /dev/null
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10
## real-launcher
