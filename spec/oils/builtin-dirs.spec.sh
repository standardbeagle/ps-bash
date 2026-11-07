## Imported from the Oils spec tests (builtin-dirs.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### pushd/popd
set -o errexit
cd /
pushd /tmp
echo -n pwd=; pwd
popd
echo -n pwd=; pwd
## STDOUT:
/tmp /
pwd=/tmp
/
pwd=/
## END
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### pushd usage
pushd -z
echo status=$?
pushd /tmp >/dev/null
echo status=$?
pushd -- /tmp >/dev/null
echo status=$?
## STDOUT:
status=2
status=0
status=0
## END
## status: 0
## timeout: 10

#### popd usage error
pushd / >/dev/null
popd zzz
echo status=$?

popd -- >/dev/null
echo status=$?

popd -z
echo status=$?
## STDOUT:
status=2
status=0
status=2
## END
## status: 0
## timeout: 10

#### popd returns error on empty directory stack
message=$(popd 2>&1)
echo $?
echo "$message" | grep -o "directory stack"
## STDOUT:
1
directory stack
## END
## status: 0
## timeout: 10

#### cd replaces the lowest entry on the directory stack!

# stable temp dir
dir=/tmp/oils-spec/builtin-dirs

mkdir -p $dir
cd $dir

pushd /tmp >/dev/null
echo pushd=$?

dirs

cd /
echo cd=$?

dirs

popd >/dev/null
echo popd=$?

popd >/dev/null
echo popd=$?
## STDOUT:
pushd=0
/tmp /tmp/oils-spec/builtin-dirs
cd=0
/ /tmp/oils-spec/builtin-dirs
popd=0
popd=1
## END
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### dirs builtin
cd /
dirs
## stdout: /
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### dirs -c to clear the stack
set -o errexit
cd /
pushd /tmp >/dev/null  # zsh pushd doesn't print anything, but bash does
echo --
dirs
dirs -c
echo --
dirs
## STDOUT:
--
/tmp /
--
/tmp
## END
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### dirs -v to print numbered stack, one entry per line
set -o errexit
cd /
pushd /tmp >/dev/null
echo --
dirs -v
pushd /dev >/dev/null
echo --
dirs -v
#
#  zsh uses tabs
## STDOUT:
--
 0  /tmp
 1  /
--
 0  /dev
 1  /tmp
 2  /
## END
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### dirs -p to print one entry per line
set -o errexit
cd /
pushd /tmp >/dev/null
echo --
dirs -p
pushd /dev >/dev/null
echo --
dirs -p
## STDOUT:
--
/tmp
/
--
/dev
/tmp
/
## END
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### dirs -l to print in long format, no tilde prefix
# Can't use the OSH test harness for this because
# /home/<username> may be included in a path.
cd /
HOME=/tmp
mkdir -p $HOME/oil_test
pushd $HOME/oil_test >/dev/null
dirs
dirs -l
## STDOUT:
~/oil_test /
/tmp/oil_test /
## END
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### dirs to print using tilde-prefix format
cd /
HOME=/tmp
mkdir -p $HOME/oil_test
pushd $HOME/oil_test >/dev/null
dirs
## stdout: ~/oil_test /
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### dirs test converting true home directory to tilde
cd /
HOME=/tmp
mkdir -p $HOME/oil_test/$HOME
pushd $HOME/oil_test/$HOME >/dev/null
dirs
## stdout: ~/oil_test/tmp /
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### dirs don't convert to tilde when $HOME is substring
cd /
mkdir -p /tmp/oil_test
mkdir -p /tmp/oil_tests
HOME=/tmp/oil_test
pushd /tmp/oil_tests
dirs
## STDOUT:
/tmp/oil_tests /
/tmp/oil_tests /
## END
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### dirs tilde test when $HOME is exactly $PWD
cd /
mkdir -p /tmp/oil_test
HOME=/tmp/oil_test
pushd $HOME
dirs
# zsh doesn't duplicate the stack I guess.
## STDOUT:
~ /
~ /
## END
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### dirs test of path alias `..`
cd /tmp
pushd .. >/dev/null
dirs
## stdout: / /tmp
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### dirs test of path alias `.`
cd /tmp
pushd . >/dev/null
dirs
## stdout: /tmp /tmp
## status: 0
## XFAIL ps-bash platform-windows-paths Windows paths print as C:\... (drive letter, backslash); bash prints /tmp (intentional-differences.md, Platform: paths)
## timeout: 10

#### pushd does not take more than one argument
pushd . . >/dev/null || echo too many args!
## stdout: too many args!
## status: 0
## timeout: 10

#### dirs does not take arguments
dirs a || echo failed
dirs -l a || echo failed
## STDOUT:
failed
failed
## END
## status: 0
## timeout: 10
