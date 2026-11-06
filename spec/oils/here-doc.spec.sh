## Imported from the Oils spec tests (here-doc.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Here string
cat <<< 'hi'
## stdout: hi
## status: 0
## timeout: 10

#### Here string with $
cat <<< $'one\ntwo\n'
## STDOUT:
one
two

## END
## status: 0
## XFAIL ps-bash gap-oils-here-doc here-doc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Here redirect with explicit descriptor
# A space between 0 and <<EOF causes it to pass '0' as an arg to cat.
cat 0<<EOF
one
EOF
## stdout: one
## status: 0
## timeout: 10

#### Here doc from another input file descriptor
# NOTE: OSH fails on descriptor 9, but not descriptor 8?  Is this because of
# the Python VM?  How  to inspect state?
read_from_fd.py 8  8<<EOF
here doc on descriptor
EOF
## stdout:
## status: 127
## timeout: 10

#### Multiple here docs with different descriptors
read_from_fd.py 0 3 <<EOF 3<<EOF3
fd0
EOF
fd3
EOF3
## stdout:
## status: 127
## timeout: 10

#### Here doc with bad var delimiter
# Most shells accept this, but OSH is stricter.
cat <<${a}
here
${a}
## stdout: here
## status: 0
## timeout: 10

#### Here doc with bad comsub delimiter
# bash is OK with this; dash isn't.  Should be a parse error.
cat <<$(a)
here
$(a)
## stdout: here
## status: 0
## timeout: 10

#### Here doc with var sub, command sub, arith sub
var=v
cat <<EOF
var: ${var}
command: $(echo hi)
arith: $((1+2))
EOF
## STDOUT:
var: v
command: hi
arith: 3
## END
## status: 0
## timeout: 10

#### Here doc in middle.  And redirects in the middle.
# This isn't specified by the POSIX grammar, but it's accepted by both dash and
# bash!
echo foo > foo.txt
echo bar > bar.txt
cat <<EOF 1>&2 foo.txt - bar.txt
here
EOF
## stdout:
## status: 0
## timeout: 10

#### Here doc line continuation
cat <<EOF \
; echo two
one
EOF
## STDOUT:
one
two
## END
## status: 0
## timeout: 10

#### Here doc with quote expansion in terminator
cat <<'EOF'"2"
one
two
EOF2
## STDOUT:
one
two
## END
## status: 0
## XFAIL ps-bash gap-oils-here-doc here-doc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Here doc with multiline double quoted string
cat <<EOF; echo "two
three"
one
EOF
## STDOUT:
one
two
three
## END
## status: 0
## timeout: 10

#### Two here docs -- first is ignored; second ones wins!
<<EOF1 cat <<EOF2
hello
EOF1
there
EOF2
## stdout: there
## status: 0
## timeout: 10

#### Here doc with line continuation, then pipe.  Syntax error.
cat <<EOF \
1
2
3
EOF
| tac
## stdout:
## status: 2
## timeout: 10

#### Here doc with pipe on first line
cat <<EOF | tac
1
2
3
EOF
## STDOUT:
3
2
1
## END
## status: 0
## timeout: 10

#### Here doc with pipe continued on last line
cat <<EOF |
1
2
3
EOF
tac
## STDOUT:
3
2
1
## END
## status: 0
## timeout: 10

#### Here doc with builtin 'read'
# read can't be run in a subshell.
read v1 v2 <<EOF
val1 val2
EOF
echo =$v1= =$v2=
## stdout: =val1= =val2=
## status: 0
## timeout: 10

#### Compound command here doc
while read line; do
  echo X $line
done <<EOF
1
2
3
EOF
## STDOUT:
X 1
X 2
X 3
## END
## status: 0
## timeout: 10

#### Here doc in while condition and here doc in body
while cat <<E1 && cat <<E2; do cat <<E3; break; done
1
E1
2
E2
3
E3
## STDOUT:
1
2
3
## END
## status: 0
## timeout: 10

#### Here doc in while condition and here doc in body on multiple lines
while cat <<E1 && cat <<E2
1
E1
2
E2
do
  cat <<E3
3
E3
  break
done
## STDOUT:
1
2
3
## END
## status: 0
## timeout: 10

#### Here doc in while loop split up more
while cat <<E1
1
E1

cat <<E2
2
E2

do
  cat <<E3
3
E3
  break
done
## STDOUT:
1
2
3
## END
## status: 0
## timeout: 10

#### Mixing << and <<-
cat <<-EOF; echo --; cat <<EOF2
	one
EOF
two
EOF2
## STDOUT:
one
--
two
## END
## status: 0
## timeout: 10

#### Two compound commands with two here docs
while read line; do echo X $line; done <<EOF; echo ==;  while read line; do echo Y $line; done <<EOF2
1
2
EOF
3
4
EOF2
## STDOUT:
X 1
X 2
==
Y 3
Y 4
## END
## status: 0
## timeout: 10

#### Function def and execution with here doc
fun() { cat; } <<EOF; echo before; fun; echo after 
1
2
EOF
## STDOUT:
before
1
2
after
## END
## status: 0
## timeout: 10

#### Here doc as command prefix
<<EOF tac
1
2
3
EOF

  # NOTE that you can have redirection AFTER the here doc thing.  And you don't
  # need a space!  Those are operators.
  #
  # POSIX doesn't seem to have this?  They have io_file, which is for
  # filenames, and io_here, which is here doc.  But about 1>&2 syntax?  Geez.
## STDOUT:
3
2
1
## END
## status: 0
## timeout: 10

#### Redirect after here doc
cat <<EOF 1>&2
out
EOF
## stdout:
## status: 0
## timeout: 10

#### here doc stripping tabs
cat <<-EOF
	1
	2
		3  # 2 tabs are both stripped
  4  # spaces are preserved
	EOF
## STDOUT:
1
2
3  # 2 tabs are both stripped
  4  # spaces are preserved
## END
## status: 0
## timeout: 10

#### Here doc within subshell with boolean
[[ $(cat <<EOF
foo
EOF
) == foo ]]; echo $?
## stdout: 0
## status: 0
## timeout: 10

#### Here Doc in if condition
if cat <<EOF; then
here doc in IF CONDITION
EOF
  echo THEN executed
fi
## STDOUT:
here doc in IF CONDITION
THEN executed
## END
## status: 0
## timeout: 10

#### Nested here docs which are indented
cat <<- EOF
	outside
	$(cat <<- INSIDE
		inside
INSIDE
)
EOF
## STDOUT:
outside
inside
## END
## status: 0
## timeout: 10

#### Here doc and backslash double quote
cat <<EOF
a \"quote\"
EOF
## stdout: a \"quote\"
## status: 0
## timeout: 10

#### Here doc escapes
# these are the chars from _DQ_ESCAPED_CHAR
cat <<EOF
\\ \" \$ \`
EOF
## stdout: \ \" $ `
## status: 0
## timeout: 10

#### Multiple here docs in pipeline
case $SH in *osh) exit ;; esac

# The second instance reads its stdin from the pipe, and fd 5 from a here doc.
read_from_fd.py 3 3<<EOF3 | read_from_fd.py 0 5 5<<EOF5
fd3
EOF3
fd5
EOF5

echo ok
## stdout: ok
## status: 0
## timeout: 10
## real-launcher

#### Multiple here docs in pipeline on multiple lines
case $SH in *osh) exit ;; esac

# SKIPPED: hangs with osh on Debian
# The second instance reads its stdin from the pipe, and fd 5 from a here doc.
read_from_fd.py 3 3<<EOF3 |
fd3
EOF3
read_from_fd.py 0 5 5<<EOF5
fd5
EOF5

echo ok
## stdout: ok
## status: 0
## timeout: 10
## real-launcher
