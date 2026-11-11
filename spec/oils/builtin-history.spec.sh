## Imported from the Oils spec tests (builtin-history.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### history usage

history not-a-number
echo status=$?

history 3 too-many
echo status=$?
## STDOUT:
status=1
status=1
## END
## status: 0
## timeout: 10

#### history -a
rm -f tmp

echo '
history -c

HISTFILE=tmp
echo 1
history -a
cat tmp

echo 2

cat tmp
' | $SH -i

# match osh's behaviour of echoing ^D for EOF
case $SH in bash) echo '^D' ;; esac
## STDOUT:
1
HISTFILE=tmp
echo 1
history -a
2
HISTFILE=tmp
echo 1
history -a
^D
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-history builtin-history (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### history -c clears in-memory history

$SH --norc -i <<'EOF'
echo 'foo' > /dev/null
echo 'bar' > /dev/null
history -c 
history | wc -l
EOF

case $SH in bash) echo '^D' ;; esac
## STDOUT:
1
^D
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-history builtin-history (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### history -d to delete history from end
# bash 4 doesn't support negative indices or ranges

rm -f myhist
export HISTFILE=myhist

$SH --norc -i <<'EOF'

echo 42
echo 43
echo 44

history -a

history -d 1
echo status=$?

# Invalid integers
history -d -1
echo status=$?
history -d -2
echo status=$?
history -d 99
echo status=$?

case $SH in bash*) echo '^D' ;; esac

EOF


# bash-4.4 used to give more errors like OSH?  Weird
## STDOUT:
42
43
44
status=0
status=0
status=0
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-history builtin-history (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### HISTFILE is defined initially
echo '
if test -n $HISTFILE; then echo exists; fi
' | $SH -i

# match osh's behaviour of echoing ^D for EOF
case $SH in bash) echo '^D' ;; esac
## STDOUT:
exists
^D
## END
## status: 0
## timeout: 10
## real-launcher

#### HISTFILE must point to a file

rm -f _tmp/does-not-exist

echo '
HISTFILE=_tmp/does-not-exist
history -r
echo status=$?
' | $SH -i

# match osh's behaviour of echoing ^D for EOF
case $SH in bash) echo '^D' ;; esac
## STDOUT:
status=1
^D
## END
## status: 0
## timeout: 10
## real-launcher

#### HISTFILE set to array

echo '
HISTFILE=(a b c)
history -a
echo status=$?
' | $SH -i

case $SH in bash) echo '^D' ;; esac

# note that bash actually writes the file 'a', since that's ${HISTFILE[0]} 
## STDOUT:
status=0
^D
## END
## status: 0
## timeout: 10
## real-launcher

#### HISTFILE unset

echo '
unset HISTFILE
history -a
echo status=$?
' | $SH -i

case $SH in bash) echo '^D' ;; esac
## STDOUT:
status=1
^D
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-history builtin-history (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### shopt histappend toggle check

shopt -s histappend
echo status=$?
shopt -p histappend
shopt -u histappend
echo status=$?
shopt -p histappend

# match osh's behaviour of echoing ^D for EOF
case $SH in bash) echo '^D' ;; esac
## STDOUT:
status=0
shopt -s histappend
status=0
shopt -u histappend
^D
## END
## status: 0
## timeout: 10
## real-launcher
