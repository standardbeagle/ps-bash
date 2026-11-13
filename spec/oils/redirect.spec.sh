## Imported from the Oils spec tests (redirect.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### >& and <& are the same

echo one 1>&2

echo two 1<&2
## stdout:
## status: 0
## timeout: 10

#### 2>&1 with no command
( exit 42 )  # status is reset after this
echo status=$?
2>&1
echo status=$?
## STDOUT:
status=42
status=0
## END
## status: 0
## timeout: 10

#### 2&>1 (is it a redirect or is it like a&>1)
2&>1
echo status=$?
## stdout: status=127
## status: 0
## timeout: 10

#### Descriptor redirect with spaces
# Hm this seems like a failure of lookahead!  The second thing should look to a
# file-like thing.
# I think this is a posix issue.
# tag: posix-issue
echo one 1>&2
echo two 1 >&2
echo three 1>& 2
## stdout:
## status: 0
## timeout: 10

#### : 3>&3-
: 3>&3-
echo hello
## stdout: hello
## status: 0
## timeout: 10

#### Redirect to empty string
f=''
echo s > "$f"
echo "result=$?"
set -o errexit
echo s > "$f"
echo DONE
## stdout: result=1
## status: 1
## timeout: 10

#### Redirect to file descriptor that's not open
# Notes:
# - 7/2021: descriptor 7 seems to work on all CI systems.  The process state
#   isn't clean, but we could probably close it in OSH?
# - dash doesn't allow file descriptors greater than 9.  (This is a good
#   thing, because the bash chapter in AOSA book mentions that juggling user
#   vs.  system file descriptors is a huge pain.)
# - But somehow running in parallel under spec-runner.sh changes whether
#   descriptor 3 is open.  e.g. 'echo hi 1>&3'.  Possibly because of
#   /usr/bin/time.  The _tmp/spec/*.task.txt file gets corrupted!
# - Oh this is because I use time --output-file.  That opens descriptor 3.  And
#   then time forks the shell script.  The file descriptor table is inherited.
#   - You actually have to set the file descriptor to something.  What do
#   configure and debootstrap too?

opened=$(ls /proc/$$/fd)
if echo "$opened" | egrep '^7$'; then
  echo "FD 7 shouldn't be open"
  echo "OPENED:"
  echo "$opened"
fi

echo hi 1>&7
## STDOUT:
7
FD 7 shouldn't be open
OPENED:
0
1
10
2
255
3
7
## END
## status: 0
## XFAIL ps-bash gap-oils-redirect redirect (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Open descriptor with exec
# What is the point of this?  ./configure scripts and debootstrap use it.
exec 3>&1
echo hi 1>&3
## stdout: hi
## status: 0
## timeout: 10

#### Open multiple descriptors with exec
# What is the point of this?  ./configure scripts and debootstrap use it.
exec 3>&1
exec 4>&1
echo three 1>&3
echo four 1>&4
## STDOUT:
three
four
## END
## status: 0
## timeout: 10

#### can't mention big file descriptor
echo hi 9>&1
# trivia: 23 is the max descriptor for mksh
#echo hi 24>&1
echo hi 99>&1
echo hi 100>&1
## STDOUT:
hi
hi
hi
## END
## status: 0
## timeout: 10

#### : >/dev/null 2> / (OSH regression: fail to pop fd frame)
# oil 0.8.pre4 fails to restore fds after redirection failure. In the
# following case, the fd frame remains after the redirection failure
# "2> /" so that the effect of redirection ">/dev/null" remains after
# the completion of the command.
: >/dev/null 2> /
echo hello
# dash/mksh terminates the execution of script on the redirection.
## stdout: hello
## status: 0
## timeout: 10

#### echo foo >&100 (OSH regression: does not fail with invalid fd 100)
# oil 0.8.pre4 does not fail with non-existent fd 100.
fd=100
echo foo53 >&$fd
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-redirect redirect (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### noclobber can still write to non-regular files like /dev/null
set -C  # noclobber
set -e  # errexit (raise any redirection errors)

# Each redirect to /dev/null should succeed
echo a  >  /dev/null  # trunc, write stdout
echo a &>  /dev/null  # trunc, write stdout and stderr
echo a  >> /dev/null  # append, write stdout
echo a &>> /dev/null  # append, write stdout and stderr
echo a  >| /dev/null  # ignore noclobber, trunc, write stdout
## stdout:
## status: 0
## timeout: 10

#### Parsing of x=1> and related cases

echo x=1>/dev/stdout
echo x=1 >/dev/stdout
echo x= 1>/dev/stdout

echo +1>/dev/stdout
echo +1 >/dev/stdout
echo + 1>/dev/stdout

echo a1>/dev/stdout
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-redirect redirect (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### xtrace not affected by redirects
set -x
printf 'aaaa' > /dev/null 2> test_osh
set +x
cat test_osh
## stdout:
## status: 0
## timeout: 10

#### : 3>&3 (OSH regression)

# mksh started being flaky on the continuous build and during release.  We
# don't care!  Related to issue #330.
case $SH in mksh) exit ;; esac

: 3>&3
echo hello
## stdout: hello
## status: 0
## timeout: 10
## real-launcher

#### >&word redirects stdout and stderr when word is not a number or -

# dash, mksh don't implement this bash behaviour.
case $SH in dash|mksh) exit 1 ;; esac

tmp="$(basename $SH)-$$.txt"  # unique name for shell and test case

stdout_stderr.py >&$tmp

# order is indeterminate
grep STDOUT $tmp
grep STDERR $tmp
## stdout:
## status: 1
## timeout: 10
## real-launcher

#### exec {fd}>&- (OSH regression: fails to close fd)
# mksh, dash do not implement {fd} redirections.
case $SH in mksh|dash) exit 1 ;; esac
# oil 0.8.pre4 fails to close fd by {fd}&-.
exec {fd}>file1
echo foo55 >&$fd
exec {fd}>&-
echo bar >&$fd
cat file1
## stdout: foo55
## status: 0
## XFAIL ps-bash gap-oils-redirect redirect (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Parsing of x={myvar} and related cases
case $SH in dash) exit ;; esac

echo {myvar}>/dev/stdout
# Bash chooses fds starting with 10 here, osh with 100, and there can already
# be some open fds, so compare further fds against this one
starting_fd=$myvar

echo x={myvar}>/dev/stdout
echo $((myvar-starting_fd))
echo x={myvar} >/dev/stdout
echo $((myvar-starting_fd))
echo x= {myvar}>/dev/stdout
echo $((myvar-starting_fd))

echo +{myvar}>/dev/stdout
echo $((myvar-starting_fd))
echo +{myvar} >/dev/stdout
echo $((myvar-starting_fd))
echo + {myvar}>/dev/stdout
echo $((myvar-starting_fd))
## STDOUT:
0
0
0
0
0
0
## END
## status: 0
## XFAIL ps-bash gap-oils-redirect redirect (oils): the expectation needs stdout to be a regular file (each >/dev/stdout reopens it with O_TRUNC, dropping earlier output); ps-bash stdout is a stream, so every echo is kept, as in bash writing to a pipe; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
