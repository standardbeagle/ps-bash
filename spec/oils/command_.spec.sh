## Imported from the Oils spec tests (command_.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Command block
PATH=/bin

{ which ls; }
## stdout: /bin/ls
## status: 0
## XFAIL ps-bash gap-oils-command_ command_ (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Name too long
./0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789
## stdout:
## status: 126
## XFAIL ps-bash gap-oils-command_ command_ (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### External programs don't have _OVM in environment
# bug fix for leakage
env | grep _OVM
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### Non-executable on $PATH

# shells differ in whether they actually execve('one/cmd') and get EPERM

mkdir -p one two
PATH="one:two:$PATH"

rm -f one/mycmd two/mycmd
echo 'echo one' > one/mycmd
echo 'echo two' > two/mycmd

# only make the second one executable
chmod +x two/mycmd
mycmd
echo status=$?
## STDOUT:
two
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-command_ command_ (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### hash without args prints the cache
whoami >/dev/null
hash
echo status=$?

# bash uses a weird table.  Although we could use TSV2.
## STDOUT:
hits	command
   1	/usr/bin/whoami
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-command_ command_ (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### hash with args
hash whoami
echo status=$?
hash | grep -o /whoami  # prints it twice
hash _nonexistent_
echo status=$?

# mksh doesn't fail
## STDOUT:
status=0
/whoami
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-command_ command_ (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### hash -r doesn't allow additional args
hash -r whoami >/dev/null  # avoid weird output with mksh
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### Executing command with same name as directory in PATH (#2429)

# Make the following directory structure. File type and permission bits are
# given on the left.
# [drwxr-xr-x]  _tmp
# +-- [drwxr-xr-x]  bin
# |   \-- [-rwxr-xr-x]  hello
# +-- [drwxr-xr-x]  notbin
# |   \-- [-rw-r--r--]  hello
# \-- [drwxr-xr-x]  dir
#     \-- [drwxr-xr-x]  hello
mkdir -p _tmp/bin
mkdir -p _tmp/bin2
mkdir -p _tmp/notbin
mkdir -p _tmp/dir/hello
printf '#!/usr/bin/env sh\necho hi\n' >_tmp/notbin/hello
printf '#!/usr/bin/env sh\necho hi\n' >_tmp/bin/hello
chmod +x _tmp/bin/hello

DIR=$PWD/_tmp/dir
BIN=$PWD/_tmp/bin
NOTBIN=$PWD/_tmp/notbin

# The command resolution will search the path for matching *files* (not
# directories) WITH the execute bit set.

# Should find executable hello right away and run it
PATH="$BIN:$PATH" hello
echo status=$?

hash -r  # Needed to clear the PATH cache

# Will see hello dir, skip it and then find&run the hello exe
PATH="$DIR:$BIN:$PATH" hello
echo status=$?

hash -r  # Needed to clear the PATH cache

# Will see hello (non-executable) file, skip it and then find&run the hello exe
PATH="$NOTBIN:$BIN:$PATH" hello
echo status=$?
## STDOUT:
hi
status=0
hi
status=0
hi
status=0
## END
## status: 0
## timeout: 30
