## Imported from the Oils spec tests (interactive.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases needing the rest of the Oils harness ($REPO_ROOT, ysh, ...) are omitted; `$SH` cases run on the real launcher.
#### fatal errors continue
# NOTE: tried here doc, but sys.stdin.isatty() fails.  Could we fake it?
$SH --rcfile /dev/null -i -c '
echo $(( 1 / 0 ))
echo one
exit 42
'
## stdout: one
## status: 42
## timeout: 10
## real-launcher

#### interactive shell runs PROMPT_COMMAND after each command
export PS1=''  # OSH prints prompt to stdout

case $SH in
  *bash|*osh)
    $SH --rcfile /dev/null -i << EOF
PROMPT_COMMAND='echo PROMPT'
echo one
echo two
EOF
    ;;
esac

# Paper over difference with OSH
case $SH in *bash) echo '^D' ;; esac
## STDOUT:
PROMPT
one
PROMPT
two
PROMPT
^D
## END
## status: 0
## XFAIL ps-bash gap-oils-interactive interactive (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### parse error in PROMPT_COMMAND
export PS1=''  # OSH prints prompt to stdout

case $SH in
  *bash|*osh)
    $SH --rcfile /dev/null -i << EOF
PROMPT_COMMAND=';'
echo one
echo two
EOF
    ;;
esac

# Paper over difference with OSH
case $SH in *bash) echo '^D' ;; esac
## STDOUT:
one
two
^D
## END
## status: 0
## XFAIL ps-bash gap-oils-interactive interactive (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### runtime error in PROMPT_COMMAND
export PS1=''  # OSH prints prompt to stdout

case $SH in
  *bash|*osh)
    $SH --rcfile /dev/null -i << 'EOF'
PROMPT_COMMAND='echo PROMPT $(( 1 / 0 ))'
echo one
echo two
EOF
    ;;
esac

# Paper over difference with OSH
case $SH in *bash) echo '^D' ;; esac
## STDOUT:
one
two
^D
## END
## status: 0
## XFAIL ps-bash gap-oils-interactive interactive (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### PROMPT_COMMAND can see $?, like bash

# bug fix #853

export PS1=''  # OSH prints prompt to stdout

case $SH in
  *bash|*osh)
    $SH --rcfile /dev/null -i << 'EOF'
myfunc() { echo last_status=$?;  }
PROMPT_COMMAND='myfunc'
( exit 42 )
( exit 43 )
echo ok
EOF
    ;;
esac

# Paper over difference with OSH
case $SH in *bash) echo '^D' ;; esac
## STDOUT:
last_status=0
last_status=42
last_status=43
ok
last_status=0
^D
## END
## status: 0
## XFAIL ps-bash gap-oils-interactive interactive (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### PROMPT_COMMAND that writes to BASH_REMATCH
export PS1=''

case $SH in
  *bash|*osh)
    $SH --rcfile /dev/null -i << 'EOF'
PROMPT_COMMAND='[[ clobber =~ (.)(.)(.) ]]; echo ---'
echo one
[[ bar =~ (.)(.)(.) ]]
echo ${BASH_REMATCH[@]}
EOF
    ;;
esac

# Paper over difference with OSH
case $SH in *bash) echo '^D' ;; esac
## STDOUT:
---
one
---
---
clo c l o
---
^D
## END
## status: 0
## XFAIL ps-bash gap-oils-interactive interactive (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### NO ASSERTIONS: Are startup files sourced before or after job control?

cat >myrc <<'EOF'

# from test/process-table-portable.sh
PS_COLS='pid,ppid,pgid,sid,tpgid,comm'

show-shell-state() {
  local prefix=$1

  echo -n "$prefix: "

  echo "pid = $$"

  # Hm TPGID has changed in both OSH and bash
  # I guess that's because because ps itself becomes the leader of the process
  # group

  ps -o $PS_COLS $$
}

show-shell-state myrc


EOF

$SH --rcfile myrc -i -c 'show-shell-state main'


# No assertions
# TODO: spec test framework should be expanded to properly support these
# comparisons.
# The --details flag is useful
## STDOUT:
myrc: pid = 2694333
    PID    PPID    PGID     SID   TPGID COMMAND
2694333 2694325 2694333 2694321 2694333 bash
main: pid = 2694333
    PID    PPID    PGID     SID   TPGID COMMAND
2694333 2694325 2694333 2694321 2694341 bash
## END
## status: 0
## XFAIL ps-bash gap-oils-interactive interactive (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### HISTFILE is written in interactive shell

rm -f myhist
export HISTFILE=myhist
echo 'echo hist1; echo hist2' | $SH --norc -i

if test -n "$BASH_VERSION"; then
  echo '^D'  # match OSH for now
fi

cat myhist
# cat ~/.config/oil/history_osh
## STDOUT:
hist1
hist2
^D
echo hist1; echo hist2
## END
## status: 0
## XFAIL ps-bash gap-oils-interactive interactive (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### HISTFILE default value

# it ends with _history
$SH --norc -i -c 'echo HISTFILE=$HISTFILE' | egrep -q '_history$'
echo status=$?
## stdout: status=0
## status: 0
## XFAIL ps-bash gap-oils-interactive interactive (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### HISTFILE=my-history loads history from that file, and writes back to it

echo 'echo 1' > my-history

# paper over OSH difference by deleting the ^D line
echo 'echo 2
history' | HISTFILE=my-history $SH --rcfile /dev/null -i | sed '/\^D/d'

echo
echo '-- after shell exit --'
cat my-history
## STDOUT:
2
    1  echo 1
    2  echo 2
    3  history

-- after shell exit --
echo 1
echo 2
history
## END
## status: 0
## XFAIL ps-bash gap-oils-interactive interactive (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### HISTFILE=my-history with history -a

echo 'echo 1' > my-history

# paper over OSH difference by deleting the ^D line
echo 'history -a
echo 2' | HISTFILE=my-history $SH --rcfile /dev/null -i | sed '/\^D/d'

echo
echo '-- after shell exit --'
cat my-history
## STDOUT:
2

-- after shell exit --
echo 1
history -a
echo 2
## END
## status: 0
## XFAIL ps-bash gap-oils-interactive interactive (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
