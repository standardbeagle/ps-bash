## Imported from the Oils spec tests (serialize.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### xtrace
zz=$'one\ntwo'
set -x
echo "$zz"
## STDOUT:
one
two
## END
## status: 0
## timeout: 10

#### printf %q newline
case $SH in ash) return ;; esac  # yash and ash don't implement this

newline=$'one\ntwo'
printf '%q\n' "$newline"

quoted="$(printf '%q\n' "$newline")"
restored=$(eval "echo $quoted")
test "$newline" = "$restored" && echo roundtrip-ok
## STDOUT:
$'one\ntwo'
roundtrip-ok
## END
## status: 0
## timeout: 10
## real-launcher

#### printf %q spaces
case $SH in ash) return ;; esac  # yash and ash don't implement this

# bash does a weird thing and uses \

spaces='one two'
printf '%q\n' "$spaces"
## stdout: one\ two
## status: 0
## timeout: 10
## real-launcher

#### printf %q quotes
case $SH in ash) return ;; esac  # yash and ash don't implement %q

quotes=\'\"
printf '%q\n' "$quotes"

quoted="$(printf '%q\n' "$quotes")"
restored=$(eval "echo $quoted")
test "$quotes" = "$restored" && echo roundtrip-ok
## STDOUT:
\'\"
roundtrip-ok
## END
## status: 0
## timeout: 10
## real-launcher

#### printf %q unprintable
case $SH in ash) return ;; esac  # yash and ash don't implement this

unprintable=$'\xff'
printf '%q\n' "$unprintable"

# bash and zsh agree
## stdout: $'\377'
## status: 0
## XFAIL ps-bash gap-oils-serialize serialize (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### printf %q unicode
case $SH in ash) return ;; esac  # yash and ash don't implement this

unicode=$'\u03bc'
unicode=$'\xce\xbc'  # does the same thing

printf '%q\n' "$unicode"

# OSH issue: we have quotes.  Isn't that OK?
## stdout: μ
## status: 0
## timeout: 10
## real-launcher

#### printf %q invalid unicode
case $SH in ash) return ;; esac

# Hm bash/mksh/zsh understand these.  They are doing decoding and error
# recovery!  inspecting the bash source seems to confirm this.
unicode=$'\xce'
printf '%q\n' "$unicode"

unicode=$'\xce\xce\xbc'
printf '%q\n' "$unicode"

unicode=$'\xce\xbc\xce'
printf '%q\n' "$unicode"

case $SH in mksh) return ;; esac  # it prints unprintable chars here!

unicode=$'\xcea'
printf '%q\n' "$unicode"
unicode=$'a\xce'
printf '%q\n' "$unicode"
## STDOUT:
$'\316'
$'\316μ'
$'μ\316'
$'\316a'
$'a\316'
## END
## status: 0
## XFAIL ps-bash gap-oils-serialize serialize (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### set
case $SH in zsh) return ;; esac  # zsh doesn't make much sense

zz=$'one\ntwo'

set | grep zz
## stdout: zz=$'one\ntwo'
## status: 0
## timeout: 10
## real-launcher

#### declare
case $SH in ash|zsh) return ;; esac  # zsh doesn't make much sense

zz=$'one\ntwo'

typeset | grep zz
typeset -p zz
## STDOUT:
zz=$'one\ntwo'
declare -- zz=$'one\ntwo'
## END
## status: 0
## XFAIL ps-bash gap-oils-serialize serialize (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
