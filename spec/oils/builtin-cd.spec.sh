## Imported from the Oils spec tests (builtin-cd.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### cd and $PWD
cd /
echo $PWD
## stdout: /
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### cd BAD/..

# Odd divergence in shells: dash and mksh normalize the path and don't check
# this error.
# TODO: I would like OSH to behave like bash and zsh, but separating chdir_arg
# and pwd_arg breaks case 17.

cd nonexistent_ZZ/..
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### cd with 2 or more args - with strict_arg_parse

shopt -s strict_arg_parse

mkdir -p foo
cd foo
echo status=$?
cd ..
echo status=$?


cd foo bar
st=$?
if test $st -ne 0; then
  echo 'failed with multiple args'
fi
## STDOUT:
status=0
status=0
failed with multiple args
## END
## status: 0
## timeout: 10

#### cd with 2 or more args is allowed (strict_arg_parse disabled)

mkdir -p foo
cd foo bar
## stdout:
## status: 1
## timeout: 10

#### cd - without OLDPWD

cd - > /dev/null  # silence dash output
echo status=$?
#pwd
## stdout: status=0
## status: 0
## timeout: 10

#### pwd
cd /
pwd
## stdout: /
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### setting $PWD doesn't affect the value of 'pwd' builtin
dir=/tmp/oil-spec-test/pwd
mkdir -p $dir
cd $dir

PWD=foo
echo before $PWD
pwd
echo after $PWD
## STDOUT:
before foo
/tmp/oil-spec-test/pwd
after foo
## END
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### unset PWD; then pwd
dir=/tmp/oil-spec-test/pwd
mkdir -p $dir
cd $dir

unset PWD
echo PWD=$PWD
pwd
echo PWD=$PWD
## STDOUT:
PWD=
/tmp/oil-spec-test/pwd
PWD=
## END
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### remove pwd dir
dir=/tmp/oil-spec-test/pwd
mkdir -p $dir
cd $dir
pwd
rmdir $dir
echo status=$?
pwd
echo status=$?
## STDOUT:
/tmp/oil-spec-test/pwd
status=0
/tmp/oil-spec-test/pwd
status=0
## END
## status: 0
## XFAIL ps-bash platform-windows-cwd-lock Windows cannot remove a process's current directory (rmdir of $PWD fails; Linux allows it) (intentional-differences.md, Platform)
## timeout: 10

#### cd to nonexistent dir
cd /nonexistent/dir
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### cd permits double bare dash
cd -- /
echo $PWD
## stdout: /
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### unset PWD; cd /tmp is allowed (regression)

unset PWD; cd /tmp
pwd
## stdout: /tmp
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### CDPATH is respected

mkdir -p /tmp/spam/foo /tmp/eggs/foo

CDPATH='/tmp/spam:/tmp/eggs'

cd foo
echo status=$?
pwd


# doesn't print the dir
## STDOUT:
/tmp/spam/foo
status=0
/tmp/spam/foo
## END
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### chdir is a synonym for cd - busybox ash

chdir /tmp

if test $? -ne 0; then
  echo fail
  exit
fi

pwd

# It's the same with no args, but mksh fails because of $HOME
#chdir
#echo status=$?
## stdout: fail
## status: 0
## XFAIL ps-bash gap-oils-builtin-cd builtin-cd (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### arguments to pwd
pwd /
## stdout: /tmp/tmp.rnPGBNmmgT
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### pwd errors out on args with strict_arg_parse
shopt -s strict_arg_parse || true
pwd / >/dev/null || echo 'too many args!'
## stdout:
## status: 0
## timeout: 10

#### 'unset PWD; pwd' before any cd (tickles a rare corner case)
dir=/tmp/oil-spec-test/pwd-2
mkdir -p $dir
cd $dir

# ensure clean shell process state
$SH -c 'unset PWD; pwd'
## stdout: /tmp/oil-spec-test/pwd-2
## status: 0
## XFAIL ps-bash gap-oils-builtin-cd builtin-cd (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### lie about PWD; pwd before any cd
dir=/tmp/oil-spec-test/pwd-3
mkdir -p $dir
cd $dir

# ensure clean shell process state
$SH -c 'PWD=foo; pwd'
## stdout: /tmp/oil-spec-test/pwd-3
## status: 0
## XFAIL ps-bash gap-oils-builtin-cd builtin-cd (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Change directory in non-shell parent process (make or Python)

# inspired by Perl package bug

old_dir=$(pwd)

mkdir -p cpan/Encode/Byte

# Simulate make changing the dir
wrapped_chdir() {
  #set -- $SH -c 'echo BEFORE; pwd; echo CD; cd Byte; echo AFTER; pwd'

  set -- $SH -c 'cd Byte; pwd'
  # strace comes out the same - one getcwd() and one chdir()
  #set -- strace -e 'getcwd,chdir' "$@"

  python2 -c '
from __future__ import print_function
import os, sys, subprocess

argv = sys.argv[1:]
print("Python PWD = %r" % os.getenv("PWD"), file=sys.stderr)
print("Python argv = %r" % argv, file=sys.stderr)

os.chdir("cpan/Encode")
subprocess.check_call(argv)
' "$@"
}

#wrapped_chdir
new_dir=$(wrapped_chdir)

#echo $old_dir

# Make the test insensitive to absolute paths
echo "${new_dir##$old_dir}"
## STDOUT:

## END
## status: 0
## timeout: 10
## real-launcher

#### What happens when inherited $PWD and current dir disagree?

DIR=/tmp/osh-spec-cd
mkdir -p $DIR
cd $DIR

old_dir=$(pwd)

mkdir -p cpan/Encode/Byte

# Simulate make changing the dir
wrapped_chdir() {
  #set -- $SH -c 'echo BEFORE; pwd; echo CD; cd Byte; echo AFTER; pwd'

  # disagreement before we gert here
  set -- $SH -c '
echo "PWD = $PWD"; pwd
cd Byte; echo cd=$?
echo "PWD = $PWD"; pwd
'

  # strace comes out the same - one getcwd() and one chdir()
  #set -- strace -e 'getcwd,chdir' "$@"

  python2 -c '
from __future__ import print_function
import os, sys, subprocess

argv = sys.argv[1:]
print("Python argv = %r" % argv, file=sys.stderr)

os.chdir("cpan/Encode")
print("Python PWD = %r" % os.getenv("PWD"), file=sys.stdout)
sys.stdout.flush()

subprocess.check_call(argv)
' "$@"
}

#unset PWD
wrapped_chdir
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### Survey of getcwd() syscall

# This is not that important -- see core/sh_init.py
# Instead of verifying that stat('.') == stat(PWD), which is two sycalls,
# OSH just calls getcwd() unconditionally.

# so C++ leak sanitizer  doesn't print to stderr
export ASAN_OPTIONS='detect_leaks=0'

strace -e getcwd -- $SH -c 'echo hi; pwd; echo $PWD' 1> /dev/null 2> err.txt

wc -l err.txt
#cat err.txt
## stdout: 1 err.txt
## status: 0
## timeout: 10
## real-launcher
