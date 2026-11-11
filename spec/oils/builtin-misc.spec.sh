## Imported from the Oils spec tests (builtin-misc.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### history builtin usage
history
echo status=$?
history +5  # hm bash considers this valid
echo status=$?
history -5  # invalid flag
echo status=$?
history f 
echo status=$?
history too many args
echo status=$?
## STDOUT:
status=0
status=0
status=2
status=1
status=1
## END
## status: 0
## timeout: 10

#### time pipeline
time echo hi | wc -c
## stdout: 3
## status: 0
## timeout: 10

#### shift
set -- 1 2 3 4
shift
echo "$@"
shift 2
echo "$@"
## STDOUT:
2 3 4
4
## END
## status: 0
## timeout: 10

#### Shifting too far
set -- 1
shift 2
## stdout:
## status: 1
## timeout: 10

#### Invalid shift argument
shift ZZZ
## stdout:
## status: 1
## timeout: 10

#### Print shell strings with weird chars: set and printf %q and ${x@Q}

# bash declare -p will print binary data, which makes this invalid UTF-8!
foo=$(/bin/echo -e 'a\nb\xffc'\'d)

# let's test the easier \x01, which doesn't give bash problems
foo=$(/bin/echo -e 'a\nb\x01c'\'d)

# dash:
#   only supports 'set'; prints it on multiple lines with binary data
#   switches to "'" for single quotes, not \'
# zsh:
#   print binary data all the time, except for printf %q
#   does print $'' strings
# mksh:
#   prints binary data for @Q
#   prints $'' strings

# All are very inconsistent.

case $SH in dash|mksh|zsh) return ;; esac


set | grep -A1 foo

# Will print multi-line and binary data literally!
#declare -p foo

printf 'pf  %q\n' "$foo"

echo '@Q ' ${foo@Q}
## STDOUT:
foo=$'a\nb\001c\'d'
pf  $'a\nb\001c\'d'
@Q  $'a\nb\001c\'d'
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-misc builtin-misc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Print shell strings with normal chars: set and printf %q and ${x@Q}

# There are variations on whether quotes are printed

case $SH in dash|zsh) return ;; esac

foo=spam

set | grep -A1 foo

# Will print multi-line and binary data literally!
typeset -p foo

printf 'pf  %q\n' "$foo"

echo '@Q ' ${foo@Q}
## STDOUT:
foo=spam
declare -- foo="spam"
pf  spam
@Q  'spam'
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-misc builtin-misc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
