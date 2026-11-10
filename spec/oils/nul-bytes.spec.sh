## Imported from the Oils spec tests (nul-bytes.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### printf - \0 escape shows NUL byte
show_hex() { od -A n -t c -t x1; }

printf '\0\n' | show_hex
## STDOUT:
  \0  \n
  00  0a
## END
## status: 0
## timeout: 10

#### Compare \x00 byte versus \x01 byte - command sub

# https://stackoverflow.com/questions/32722007/is-skipping-ignoring-nul-bytes-on-process-substitution-standardized
# bash contains a warning!

show_bytes() {
  echo -n "$1" | od -A n -t x1
}

s=$(printf '.\001.')
echo len=${#s}
show_bytes "$s"

s=$(printf '.\000.')
echo len=${#s}
show_bytes "$s"

s=$(printf '\000')
echo len=${#s} 
show_bytes "$s"
## STDOUT:
len=3
 2e 01 2e
len=2
 2e 2e
len=0
## END
## status: 0
## XFAIL ps-bash gap-oils-nul-bytes nul-bytes (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Compare \x00 byte versus \x01 byte - read builtin

# Hm same odd behavior

show_string() {
  read s
  echo len=${#s}
  echo -n "$s" | od -A n -t x1
}

printf '.\001.' | show_string

printf '.\000.' | show_string

printf '\000' | show_string
## STDOUT:
len=3
 2e 01 2e
len=2
 2e 2e
len=0
## END
## status: 0
## XFAIL ps-bash gap-oils-nul-bytes nul-bytes (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Strip ops # ## % %% with NUL bytes

show_bytes() {
  echo -n "$1" | od -A n -t x1
}

s=$(printf '\000.\000')
echo len=${#s}
show_bytes "$s"

echo ---

t=${s#?}
echo len=${#t}
show_bytes "$t"

t=${s##?}
echo len=${#t}
show_bytes "$t"

t=${s%?}
echo len=${#t}
show_bytes "$t"

t=${s%%?}
echo len=${#t}
show_bytes "$t"
## STDOUT:
len=1
 2e
---
len=0
len=0
len=0
len=0
## END
## status: 0
## XFAIL ps-bash gap-oils-nul-bytes nul-bytes (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Issue 2269 Reduction

show_bytes() {
  echo -n "$1" | od -A n -t x1
}

s=$(printf '\000x')
echo len=${#s}
show_bytes "$s"

# strip one char from the front
s=${s#?}
echo len=${#s}
show_bytes "$s"

echo ---

s=$(printf '\001x')
echo len=${#s}
show_bytes "$s"

# strip one char from the front
s=${s#?}
echo len=${#s}
show_bytes "$s"
## STDOUT:
len=1
 78
len=0
---
len=2
 01 78
len=1
 78
## END
## status: 0
## XFAIL ps-bash gap-oils-nul-bytes nul-bytes (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### NUL bytes with echo -e
case $SH in dash) exit ;; esac

show_hex() { od -A n -t c -t x1; }

echo -e '\0-' | show_hex
#echo -e '\x00-'
#echo -e '\000-'
## STDOUT:
  \0   -  \n
  00  2d  0a
## END
## status: 0
## timeout: 10
## real-launcher

#### printf - literal NUL in format string
case $SH in dash|ash) return ;; esac

# Show both printable and hex
show_hex() { od -A n -t c -t x1; }

printf $'x\U0z' | show_hex
echo ---

printf $'x\U00z' | show_hex
echo ---

printf $'\U0z' | show_hex
## STDOUT:
   x
  78
---
   x
  78
---
## END
## status: 0
## XFAIL ps-bash gap-oils-nul-bytes nul-bytes (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### printf - NUL byte in value (OSH and zsh agree)
case $SH in dash) exit ;; esac
show_hex() { od -A n -t c -t x1; }

nul=$'\0'
echo "$nul" | show_hex
printf '%s\n' "$nul" | show_hex
## STDOUT:
  \n
  0a
  \n
  0a
## END
## status: 0
## timeout: 10
## real-launcher

#### NUL bytes with echo $'\0' (OSH and zsh agree)
case $SH in dash) exit ;; esac
show_hex() { od -A n -t c -t x1; }

# OSH agrees with ZSH -- so you have the ability to print NUL bytes without
# legacy echo -e

echo $'\0' | show_hex
## STDOUT:
  \n
  0a
## END
## status: 0
## timeout: 10
## real-launcher

#### NUL bytes and IFS splitting
case $SH in dash) exit ;; esac

argv.py $(echo -e '\0')
argv.py "$(echo -e '\0')"
argv.py $(echo -e 'a\0b')
argv.py "$(echo -e 'a\0b')"
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### NUL bytes with test -n

case $SH in dash) exit ;; esac

# zsh is buggy here, weird
test -n $''
echo status=$?

test -n $'\0'
echo status=$?
## STDOUT:
status=1
status=1
## END
## status: 0
## timeout: 10
## real-launcher

#### NUL bytes with test -f

case $SH in dash) exit ;; esac


test -f $'\0'
echo status=$?

touch foo
test -f $'foo\0'
echo status=$?

test -f $'foo\0bar'
echo status=$?

test -f $'foobar'
echo status=$?
## STDOUT:
status=1
status=0
status=0
status=1
## END
## status: 0
## timeout: 10
## real-launcher

#### NUL bytes with ${#s} (OSH and zsh agree)

case $SH in dash) exit ;; esac

empty=$''
nul=$'\0'

echo empty=${#empty}
echo nul=${#nul}
## STDOUT:
empty=0
nul=0
## END
## status: 0
## timeout: 10
## real-launcher

#### Compare \x00 byte versus \x01 byte - read -n
case $SH in dash) exit ;; esac

show_string() {
  read -n 3 s
  echo len=${#s}
  echo -n "$s" | od -A n -t x1
}


printf '.\001.' | show_string

printf '.\000.' | show_string

printf '\000' | show_string


len=3
 2e 01 2e
len=1
 2e
len=0
## STDOUT:
len=3
 2e 01 2e
len=2
 2e 2e
len=0
## END
## status: 0
## XFAIL ps-bash gap-oils-nul-bytes nul-bytes (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Compare \x00 byte versus \x01 byte - mapfile builtin
case $SH in dash|mksh|zsh|ash) exit ;; esac

{ 
  printf '.\000.\n'
  printf '.\000.\n'
} |
{ mapfile LINES
  echo len=${#LINES[@]}
  for line in ${LINES[@]}; do
    echo -n "$line" | od -A n -t x1
  done
}

# bash is INCONSISTENT:
# - it TRUNCATES at \0, with 'mapfile'
# - rather than just IGNORING \0, with 'read'
## STDOUT:
len=2
 2e
 2e
## END
## status: 0
## XFAIL ps-bash gap-oils-nul-bytes nul-bytes (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
