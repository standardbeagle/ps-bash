## Imported from the Oils spec tests (prompt.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### \[\] are non-printing
PS1='\[foo\]\$'
echo "${PS1@P}"
## stdout: foo$
## status: 0
## timeout: 10

#### literal escapes
PS1='\a\e\r\n'
echo "${PS1@P}"
## STDOUT:


## END
## status: 0
## timeout: 10

#### special case for $
# NOTE: This might be broken for # but it's hard to tell since we don't have
# root.  Could inject __TEST_EUID or something.
PS1='$'
echo "${PS1@P}"
PS1='\$'
echo "${PS1@P}"
PS1='\\$'
echo "${PS1@P}"
PS1='\\\$'
echo "${PS1@P}"
PS1='\\\\$'
echo "${PS1@P}"
## STDOUT:
$
$
$
\$
\$
## END
## status: 0
## XFAIL ps-bash gap-oils-prompt prompt (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### PS1 evaluation order
x='\'
y='h'
PS1='$x$y'
echo "${PS1@P}"
## stdout: \h
## status: 0
## XFAIL ps-bash gap-oils-prompt prompt (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### \1004
PS1='\1004$'
echo "${PS1@P}"
## stdout: @4$
## status: 0
## timeout: 10

#### \001 octal literals are supported
PS1='[\045]'
echo "${PS1@P}"
## stdout: [%]
## status: 0
## timeout: 10

#### \555 is beyond max octal byte of \377 and wrapped to m
PS1='\555$'
echo "${PS1@P}"
## stdout: m$
## status: 0
## timeout: 10

#### \x55 hex literals not supported
PS1='[\x55]'
echo "${PS1@P}"
## stdout: [\x55]
## status: 0
## timeout: 10

#### Single backslash
PS1='\'
echo "${PS1@P}"
## stdout: \
## status: 0
## timeout: 10

#### Escaped backslash
PS1='\\'
echo "${PS1@P}"
## stdout: \
## status: 0
## timeout: 10

#### \0001 octal literals are not supported
PS1='[\0455]'
echo "${PS1@P}"
## stdout: [%5]
## status: 0
## timeout: 10

#### \u0001 unicode literals not supported
PS1='[\u0001]'
USER=$(whoami)
test "${PS1@P}" = "[${USER}0001]"
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### constant string
PS1='$ '
echo "${PS1@P}"
## stdout: $ 
## status: 0
## timeout: 10

#### hostname

# NOTE: This test is not hermetic.  On my machine the short and long host name
# are the same.

PS1='\h '
test "${PS1@P}" = "$(hostname -s) "  # short name
echo status=$?
PS1='\H '
test "${PS1@P}" = "$(hostname) "
echo status=$?
## STDOUT:
status=0
status=0
## END
## status: 0
## timeout: 10

#### \j for number of jobs
set -m # enable job control
PS1='foo \j bar'
echo "${PS1@P}" | egrep -q 'foo 0 bar'
echo matched=$?
sleep 5 &
echo "${PS1@P}" | egrep -q 'foo 1 bar'
echo matched=$?
kill %%
fg
echo "${PS1@P}" | egrep -q 'foo 0 bar'
echo matched=$?
## STDOUT:
matched=0
matched=0
sleep 5
matched=0
## END
## status: 0
## XFAIL ps-bash gap-oils-prompt prompt (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### \! for history number
set -o history # enable history
PS1='foo \! bar'
history -c # clear history
echo "${PS1@P}" | egrep -q "foo 1 bar"
echo matched=$?
echo "_${PS1@P}" | egrep -q "foo 3 bar"
echo matched=$?
## STDOUT:
matched=0
matched=0
## END
## status: 0
## XFAIL ps-bash gap-oils-prompt prompt (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### \# for command number
PS1='foo \# bar'
prev_cmd_num="$(echo "${PS1@P}" | egrep -o 'foo [0-9]+ bar' | sed -E 's/foo ([0-9]+) bar/\1/')"
echo "${PS1@P}" | egrep -q "foo $((prev_cmd_num + 1)) bar"
echo matched=$?
## stdout: matched=0
## status: 0
## XFAIL ps-bash gap-oils-prompt prompt (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### sh -i
# Notes:
# - OSH prompt goes to stdout and bash goes to stderr
# - This test seems to fail on the system bash, but succeeds with spec-bin/bash
echo 'echo foo' | PS1='[prompt] ' $SH --rcfile /dev/null -i >out.txt 2>err.txt
fgrep -q '[prompt]' out.txt err.txt
echo match=$?
## stdout: match=1
## status: 0
## XFAIL ps-bash gap-oils-prompt prompt (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### username
PS1='\u '
USER=$(whoami)
test "${PS1@P}" = "${USER} "
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### current working dir
PS1='\w '
test "${PS1@P}" = "${PWD} "
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### \W is basename of working dir
PS1='\W '
test "${PS1@P}" = "$(basename $PWD) "
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### \t for 24h time (HH:MM:SS)
PS1='foo \t bar'
echo "${PS1@P}" | egrep -q 'foo [0-2][0-9]:[0-5][0-9]:[0-5][0-9] bar'
echo matched=$?
## stdout: matched=0
## status: 0
## timeout: 10

#### \T for 12h time (HH:MM:SS)
PS1='foo \T bar'
echo "${PS1@P}" | egrep -q 'foo [0-1][0-9]:[0-5][0-9]:[0-5][0-9] bar'
echo matched=$?
## stdout: matched=0
## status: 0
## timeout: 10

#### \@ for 12h time (HH:MM AM/PM)
PS1='foo \@ bar'
echo "${PS1@P}" | egrep -q 'foo [0-1][0-9]:[0-5][0-9] (A|P)M bar'
echo matched=$?
## stdout: matched=0
## status: 0
## timeout: 10

#### \A for 24h time (HH:MM)
PS1='foo \A bar'
echo "${PS1@P}" | egrep -q 'foo [0-2][0-9]:[0-5][0-9] bar'
echo matched=$?
## stdout: matched=0
## status: 0
## timeout: 10

#### \d for date
PS1='foo \d bar'
echo "${PS1@P}" | egrep -q 'foo [A-Z][a-z]+ [A-Z][a-z]+ [0-9]+ bar'
echo matched=$?
## stdout: matched=0
## status: 0
## timeout: 10

#### \D{%H:%M} for strftime
PS1='foo \D{%H:%M} bar'
echo "${PS1@P}" | egrep -q 'foo [0-9][0-9]:[0-9][0-9] bar'
echo matched=$?

PS1='foo \D{%H:%M:%S} bar'
echo "${PS1@P}" | egrep -q 'foo [0-9][0-9]:[0-9][0-9]:[0-9][0-9] bar'
echo matched=$?
## STDOUT:
matched=0
matched=0
## END
## status: 0
## timeout: 10

#### \D{} for locale specific strftime

# In bash y.tab.c uses %X when string is empty
# This doesn't seem to match exactly, but meh for now.

PS1='foo \D{} bar'
echo "${PS1@P}" | egrep -q '^foo [0-9][0-9]:[0-9][0-9]:[0-9][0-9]( ..)? bar$'
echo matched=$?
## stdout: matched=0
## status: 0
## XFAIL ps-bash gap-oils-prompt prompt (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### \s for shell, \v for major.minor version, and \V for full version
PS1='foo \s bar'
echo "${PS1@P}" | egrep -q '^foo (bash|osh) bar$'
echo match=$?

PS1='foo \v bar'
echo "${PS1@P}" | egrep -q '^foo [0-9]+\.[0-9]+ bar$'
echo match=$?

PS1='foo \V bar'
echo "${PS1@P}" | egrep -q '^foo [0-9]+\.[0-9]+\.[0-9]+ bar$'
echo match=$?
## STDOUT:
match=0
match=0
match=0
## END
## status: 0
## timeout: 10

#### @P with array
$SH -c 'echo ${@@P}' dummy a b c
echo status=$?
$SH -c 'echo ${*@P}' dummy a b c
echo status=$?
$SH -c 'a=(x y); echo ${a@P}' dummy a b c
echo status=$?
## STDOUT:
a b c
status=0
a b c
status=0
x
status=0
## END
## status: 0
## timeout: 10
## real-launcher

#### default PS1
#flags='--norc --noprofile'
flags='--rcfile /dev/null'

$SH $flags -i -c 'echo "_${PS1}_"'
## stdout: _${debian_chroot:+($debian_chroot)}\u@\h:\w\$ _
## status: 0
## XFAIL ps-bash gap-oils-prompt prompt (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
