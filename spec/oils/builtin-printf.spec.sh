## Imported from the Oils spec tests (builtin-printf.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### printf with no args
printf
## stdout:
## status: 2
## timeout: 10

#### printf -v %s
var=foo
printf -v $var %s 'hello there'
argv.py "$foo"
## stdout:
## status: 127
## timeout: 10

#### printf -v %q
val='"quoted" with spaces and \'

# quote 'val' and store it in foo
printf -v foo %q "$val"
# then round trip back to eval
eval "bar=$foo"

# debugging:
#echo foo="$foo"
#echo bar="$bar"
#echo val="$val"

test "$bar" = "$val" && echo OK
## stdout: OK
## status: 0
## timeout: 10

#### printf -v a[1]
a=(a b c)
printf -v 'a[1]' %s 'foo'
echo status=$?
argv.py "${a[@]}"
## stdout: status=0
## status: 127
## timeout: 10

#### printf -v syntax error
printf -v 'a[' %s 'foo'
echo status=$?
## stdout: status=2
## status: 0
## timeout: 10

#### dynamic declare instead of %s
var=foo
declare $var='hello there'
argv.py "$foo"
## stdout:
## status: 127
## timeout: 10

#### dynamic declare instead of %q
var=foo
val='"quoted" with spaces and \'
# I think this is bash 4.4 only.
declare $var="${val@Q}"
echo "$foo"
## stdout: '"quoted" with spaces and \'
## status: 0
## timeout: 10

#### printf with too few arguments
printf -- '-%s-%s-%s-\n' 'a b' 'x y'
## stdout: -a b-x y--
## status: 0
## timeout: 10

#### printf with too many arguments
printf -- '-%s-%s-\n' a b c d e
## STDOUT:
-a-b-
-c-d-
-e--
## END
## status: 0
## timeout: 10

#### printf width strings
printf '[%5s]\n' abc
printf '[%-5s]\n' abc
## STDOUT:
[  abc]
[abc  ]
## END
## status: 0
## timeout: 10

#### printf integer
printf '%d\n' 42
printf '%i\n' 42  # synonym
printf '%d\n' \'a # if first character is a quote, use character code
printf '%d\n' \"a # double quotes work too
printf '[%5d]\n' 42
printf '[%-5d]\n' 42
printf '[%05d]\n' 42
#printf '[%-05d]\n' 42  # the leading 0 is meaningless
#[42   ]
## STDOUT:
42
42
97
97
[   42]
[42   ]
[00042]
## END
## status: 0
## timeout: 10

#### printf %6.4d -- "precision" does padding for integers
printf '[%6.4d]\n' 42
printf '[%.4d]\n' 42
printf '[%6.d]\n' 42
echo --
printf '[%6.4d]\n' -42
printf '[%.4d]\n' -42
printf '[%6.d]\n' -42
## STDOUT:
[  0042]
[0042]
[    42]
--
[ -0042]
[-0042]
[   -42]
## END
## status: 0
## timeout: 10

#### printf %6.4x X o
printf '[%6.4x]\n' 42
printf '[%.4x]\n' 42
printf '[%6.x]\n' 42
echo --
printf '[%6.4X]\n' 42
printf '[%.4X]\n' 42
printf '[%6.X]\n' 42
echo --
printf '[%6.4o]\n' 42
printf '[%.4o]\n' 42
printf '[%6.o]\n' 42
## STDOUT:
[  002a]
[002a]
[    2a]
--
[  002A]
[002A]
[    2A]
--
[  0052]
[0052]
[    52]
## END
## status: 0
## timeout: 10

#### %06d zero padding vs. %6.6d
printf '[%06d]\n' 42
printf '[%06d]\n' -42  # 6 TOTAL
echo --
printf '[%6.6d]\n' 42
printf '[%6.6d]\n' -42  # 6 + 1 for the - sign!!!
## STDOUT:
[000042]
[-00042]
--
[000042]
[-000042]
## END
## status: 0
## timeout: 10

#### %06x %06X %06o
printf '[%06x]\n' 42
printf '[%06X]\n' 42
printf '[%06o]\n' 42
## STDOUT:
[00002a]
[00002A]
[000052]
## END
## status: 0
## timeout: 10

#### %06s is no-op
printf '(%6s)\n' 42
printf '(%6s)\n' -42
printf '(%06s)\n' 42
printf '(%06s)\n' -42
echo status=$?
# mksh is stricter
## STDOUT:
(    42)
(   -42)
(    42)
(   -42)
status=0
## END
## status: 0
## timeout: 10

#### printf %6.4s does both truncation and padding
printf '[%6s]\n' foo
printf '[%6.4s]\n' foo
printf '[%-6.4s]\n' foo
printf '[%6s]\n' spam-eggs
printf '[%6.4s]\n' spam-eggs
printf '[%-6.4s]\n' spam-eggs
## STDOUT:
[   foo]
[   foo]
[foo   ]
[spam-eggs]
[  spam]
[spam  ]
## END
## status: 0
## timeout: 10

#### printf %6.0s and %0.0s
printf '[%6.0s]\n' foo
printf '[%0.0s]\n' foo
## STDOUT:
[      ]
[]
## END
## status: 0
## timeout: 10

#### printf %6.s and %0.s
printf '[%6.s]\n' foo
printf '[%0.s]\n' foo
## STDOUT:
[      ]
[]
## END
## status: 0
## timeout: 10

#### printf %*.*s (width/precision from args)
printf '[%*s]\n' 9 hello
printf '[%.*s]\n' 3 hello
printf '[%*.3s]\n' 9 hello
printf '[%9.*s]\n' 3 hello
printf '[%*.*s]\n' 9 3 hello
## STDOUT:
[    hello]
[hel]
[      hel]
[      hel]
[      hel]
## END
## status: 0
## timeout: 10

#### unsigned / octal / hex
printf '[%u]\n' 42
printf '[%o]\n' 42
printf '[%x]\n' 42
printf '[%X]\n' 42
echo

printf '[%X]\n' \'a  # if first character is a quote, use character code
printf '[%X]\n' \'ab # extra chars ignored
## STDOUT:
[42]
[52]
[2a]
[2A]

[61]
[61]
## END
## status: 0
## timeout: 10

#### unsigned / octal / hex big

for big in $(( 1 << 32 )) $(( (1 << 63) - 1 )); do
  printf '[%u]\n' $big
  printf '[%o]\n' $big
  printf '[%x]\n' $big
  printf '[%X]\n' $big
  echo
done
## STDOUT:
[4294967296]
[40000000000]
[100000000]
[100000000]

[9223372036854775807]
[777777777777777777777]
[7fffffffffffffff]
[7FFFFFFFFFFFFFFF]

## END
## status: 0
## timeout: 10

#### empty string (osh is more strict)
printf '%d\n' ''
## stdout: 0
## status: 0
## timeout: 10

#### No char after ' => zero code point

# most shells use 0 here
printf '%d\n' \'
printf '%d\n' \"
## STDOUT:
0
0
## END
## status: 0
## timeout: 10

#### Invalid UTF-8

echo bytes1
not_utf8=$(python2 -c 'print("\xce\xce")')

printf '%x\n' \'$not_utf8
printf '%u\n' \'$not_utf8
printf '%o\n' \'$not_utf8
echo

echo bytes2
not_utf8=$(python2 -c 'print("\xbc\xbc")')
printf '%x\n' \'$not_utf8
printf '%u\n' \'$not_utf8
printf '%o\n' \'$not_utf8
echo

# Copied from data_lang/utf8_test.cc

echo overlong2
overlong2=$(python2 -c 'print("\xC1\x81")')
printf '%x\n' \'$overlong2
printf '%u\n' \'$overlong2
printf '%o\n' \'$overlong2
echo

echo overlong3
overlong3=$(python2 -c 'print("\xE0\x81\x81")')
printf '%x\n' \'$overlong3
printf '%u\n' \'$overlong3
printf '%o\n' \'$overlong3
echo
## STDOUT:
bytes1
0
0
0

bytes2
0
0
0

overlong2
0
0
0

overlong3
0
0
0

## END
## status: 0
## timeout: 10

#### negative numbers with unsigned / octal / hex
printf '[%u]\n' -42
echo status=$?

printf '[%o]\n' -42
echo status=$?

printf '[%x]\n' -42
echo status=$?

printf '[%X]\n' -42
echo status=$?


# osh DISALLOWS this because the output depends on the machine architecture.
## STDOUT:
[18446744073709551574]
status=0
[1777777777777777777726]
status=0
[ffffffffffffffd6]
status=0
[FFFFFFFFFFFFFFD6]
status=0
## END
## status: 0
## timeout: 10

#### printf floating point (not required, but they all implement it)
printf '[%f]\n' 3.14159
printf '[%.2f]\n' 3.14159
printf '[%8.2f]\n' 3.14159
printf '[%-8.2f]\n' 3.14159
printf '[%-f]\n' 3.14159
printf '[%-f]\n' 3.14
## STDOUT:
[3.141590]
[3.14]
[    3.14]
[3.14    ]
[3.141590]
[3.140000]
## END
## status: 0
## timeout: 10

#### printf floating point with - and 0
printf '[%8.4f]\n' 3.14
printf '[%08.4f]\n' 3.14
printf '[%8.04f]\n' 3.14  # meaning less 0
printf '[%08.04f]\n' 3.14
echo ---
# these all boil down to the same thing.  The -, 8, and 4 are respected, but
# none of the 0 are.
printf '[%-8.4f]\n' 3.14
printf '[%-08.4f]\n' 3.14
printf '[%-8.04f]\n' 3.14
printf '[%-08.04f]\n' 3.14
## STDOUT:
[  3.1400]
[003.1400]
[  3.1400]
[003.1400]
---
[3.1400  ]
[3.1400  ]
[3.1400  ]
[3.1400  ]
## END
## status: 0
## timeout: 10

#### printf eE fF gG
printf '[%e]\n' 3.14
printf '[%E]\n' 3.14
printf '[%f]\n' 3.14
# bash is the only one that implements %F?  Is it a synonym?
#printf '[%F]\n' 3.14
printf '[%g]\n' 3.14
printf '[%G]\n' 3.14
## STDOUT:
[3.140000e+00]
[3.140000E+00]
[3.140000]
[3.14]
[3.14]
## END
## status: 0
## timeout: 10

#### printf backslash escapes
argv.py "$(printf 'a\tb')"
argv.py "$(printf '\xE2\x98\xA0')"
argv.py "$(printf '\044e')"
argv.py "$(printf '\0377')"  # out of range
## stdout:
## status: 127
## timeout: 10

#### printf octal backslash escapes
argv.py "$(printf '\0377')"
argv.py "$(printf '\377')"
## stdout:
## status: 127
## timeout: 10

#### printf unicode backslash escapes
argv.py "$(printf '\u2620')"
argv.py "$(printf '\U0000065f')"
## stdout:
## status: 127
## timeout: 10

#### printf invalid backslash escape (is ignored)
printf '[\Z]\n'
## stdout: [\Z]
## status: 0
## timeout: 10

#### printf % escapes
printf '[%%]\n'
## stdout: [%]
## status: 0
## timeout: 10

#### printf %c ASCII

printf '%c\n' a
printf '%c\n' ABC
printf '%cZ\n' ABC
## STDOUT:
a
A
AZ
## END
## status: 0
## timeout: 10

#### printf invalid format
printf '%z' 42
echo status=$?
printf '%-z' 42
echo status=$?
# osh emits parse errors
## STDOUT:
status=1
status=1
## END
## status: 0
## timeout: 10

#### printf %q
x='a b'
printf '[%q]\n' "$x"
## stdout: [a\ b]
## status: 0
## timeout: 10

#### printf %6q (width)
# NOTE: coreutils /usr/bin/printf does NOT implement this %6q !!!
x='a b'
printf '[%6q]\n' "$x"
printf '[%1q]\n' "$x"
## STDOUT:
[  a\ b]
[a\ b]
## END
## status: 0
## timeout: 10

#### printf negative numbers
printf '[%d] ' -42
echo status=$?
printf '[%i] ' -42
echo status=$?

# extra LEADING space too
printf '[%d] ' ' -42'
echo status=$?
printf '[%i] ' ' -42'
echo status=$?

# extra TRAILING space too
printf '[%d] ' ' -42 '
echo status=$?
printf '[%i] ' ' -42 '
echo status=$?

# extra TRAILING chars
printf '[%d] ' ' -42z'
echo status=$?
printf '[%i] ' ' -42z'
echo status=$?

exit 0  # ok

# zsh is LESS STRICT

# osh is like zsh but has a hard failure (TODO: could be an option?)

# ash is MORE STRICT
## STDOUT:
[-42] status=0
[-42] status=0
[-42] status=0
[-42] status=0
[-42] status=1
[-42] status=1
[-42] status=1
[-42] status=1
## END
## status: 0
## timeout: 10

#### printf + and space flags
# I didn't know these existed -- I only knew about - and 0 !
printf '[%+d]\n' 42
printf '[%+d]\n' -42
printf '[% d]\n' 42
printf '[% d]\n' -42
## STDOUT:
[+42]
[-42]
[ 42]
[-42]
## END
## status: 0
## timeout: 10

#### printf # flag
# I didn't know these existed -- I only knew about - and 0 !
# Note: '#' flag for integers outputs a prefix ONLY WHEN the value is non-zero
printf '[%#o][%#o]\n' 0 42
printf '[%#x][%#x]\n' 0 42
printf '[%#X][%#X]\n' 0 42
echo ---
# Note: '#' flag for %f, %g always outputs the decimal point.
printf '[%.0f][%#.0f]\n' 3 3
# Note: In addition, '#' flag for %g does not omit zeroes in fraction
printf '[%g][%#g]\n' 3 3
## STDOUT:
[0][052]
[0][0x2a]
[0][0X2A]
---
[3][3.]
[3][3.00000]
## END
## status: 0
## timeout: 10

#### Runtime error for invalid integer
x=3abc
printf '%d\n' $x
echo status=$?
printf '%d\n' xyz
echo status=$?
# zsh should exit 1 in both cases
# fails but also prints 0 instead of 3abc
# osh doesn't print anything invalid
## STDOUT:
3
status=1
0
status=1
## END
## status: 0
## timeout: 10

#### %(strftime format)T
# The result depends on timezone
export TZ=Asia/Tokyo
printf '%(%Y-%m-%d)T\n' 1557978599
export TZ=US/Eastern
printf '%(%Y-%m-%d)T\n' 1557978599
echo status=$?
## STDOUT:
2019-05-16
2019-05-16
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-printf builtin-printf (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### %(strftime format)T doesn't respect TZ if not exported

# note: this test leaks!  It assumes that /etc/localtime is NOT Portugal.

TZ=Portugal  # NOT exported
localtime=$(printf '%(%Y-%m-%d %H:%M:%S)T\n' 1557978599)

# TZ is respected
export TZ=Portugal
tz=$(printf '%(%Y-%m-%d %H:%M:%S)T\n' 1557978599)

#echo $localtime
#echo $tz

if ! test "$localtime" = "$tz"; then
  echo 'not equal'
fi
## stdout: not equal
## status: 0
## XFAIL ps-bash gap-oils-builtin-printf builtin-printf (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### %(strftime format)T TZ in environ but not in shell's memory

# note: this test leaks!  It assumes that /etc/localtime is NOT Portugal.

# TZ is respected
export TZ=Portugal
tz=$(printf '%(%Y-%m-%d %H:%M:%S)T\n' 1557978599)

unset TZ  # unset in the shell, but still in the environment

localtime=$(printf '%(%Y-%m-%d %H:%M:%S)T\n' 1557978599)

if ! test "$localtime" = "$tz"; then
  echo 'not equal'
fi
## stdout: not equal
## status: 0
## timeout: 10

#### %10.5(strftime format)T
# The result depends on timezone
export TZ=Asia/Tokyo
printf '[%10.5(%Y-%m-%d)T]\n' 1557978599
export TZ=US/Eastern
printf '[%10.5(%Y-%m-%d)T]\n' 1557978599
echo status=$?
## STDOUT:
[     2019-]
[     2019-]
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-printf builtin-printf (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Regression for 'printf x y'
printf x y
printf '%s\n' z
## stdout: xz
## status: 0
## timeout: 10

#### printf positive integer overflow

# %i seems like a synonym for %d

for fmt in '%u\n' '%d\n'; do
  # bash considers this in range for %u
  # same with mksh
  # zsh cuts everything off after 19 digits
  # ash truncates everything
  printf "$fmt" '18446744073709551615'
  echo status=$?
  printf "$fmt" '18446744073709551616'
  echo status=$?
  echo
done
## STDOUT:
18446744073709551615
status=0
18446744073709551615
status=0

9223372036854775807
status=0
9223372036854775807
status=0

## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-printf builtin-printf (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### printf negative integer overflow

# %i seems like a synonym for %d

for fmt in '%u\n' '%d\n'; do

  printf "$fmt" '-18446744073709551615'
  echo status=$?
  printf "$fmt" '-18446744073709551616'
  echo status=$?
  echo
done
## STDOUT:
1
status=0
18446744073709551615
status=0

-9223372036854775808
status=0
-9223372036854775808
status=0

## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-printf builtin-printf (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### printf %b does backslash escaping

printf '[%s]\n' '\044'  # escapes not evaluated
printf '[%b]\n' '\044'  # YES, escapes evaluated
echo

printf '[%s]\n' '\x7e'  # escapes not evaluated
printf '[%b]\n' '\x7e'  # YES, escapes evaluated
echo

# not a valid escape
printf '[%s]\n' '\A'
printf '[%b]\n' '\A'
## STDOUT:
[\044]
[$]

[\x7e]
[~]

[\A]
[\A]
## END
## status: 0
## timeout: 10

#### printf %b unicode escapes

printf '[%s]\n' '\u03bc'  # escapes not evaluated
printf '[%b]\n' '\u03bc'  # YES, escapes evaluated
## STDOUT:
[\u03bc]
[μ]
## END
## status: 0
## timeout: 10

#### printf %b respects \c early return
printf '[%b]\n' 'ab\ncd\cxy'
echo $?
## STDOUT:
[ab
cd0
## END
## status: 0
## timeout: 10

#### printf %b supports octal escapes, both \141 and \0141

printf 'three %b\n' '\141'  # di
printf 'four  %b\n' '\0141'
echo

# trailing 9
printf '%b\n' '\1419'
printf '%b\n' '\01419'

# Notes:
#
# - echo -e: 
#   - NO  3 digit octal  - echo -e '\141' does not work
#   - YES 4 digit octal
# - printf %b
#   - YES 3 digit octal
#   - YES 4 digit octal
# - printf string (outer)
#   - YES 3 digit octal
#   - NO  4 digit octal
# - $'' and $PS1
#   - YES 3 digit octal
#   - NO  4 digit octal
## STDOUT:
three a
four  a

a9
a9
## END
## status: 0
## timeout: 10

#### printf %b with truncated octal escapes

# 8 is not a valid octal digit

printf '%b\n' '\558'
printf '%b\n' '\0558'
echo

show_bytes() {
  od -A n -t x1
}
printf '%b' '\7' | show_bytes
printf '%b' '\07' | show_bytes
printf '%b' '\007' | show_bytes
printf '%b' '\0007' | show_bytes
## STDOUT:
-8
-8

 07
 07
 07
 07
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-printf builtin-printf (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### printf %d %X support hex 0x5 and octal 055

echo hex
printf '%d\n' 0x55
printf '%X\n' 0x55

echo hex CAPS
printf '%d\n' 0X55
printf '%X\n' 0X55

echo octal 3
printf '%d\n' 055
printf '%X\n' 055

echo octal 4
printf '%d\n' 0055
printf '%X\n' 0055

echo octal 5
printf '%d\n' 00055
printf '%X\n' 00055
## STDOUT:
hex
85
55
hex CAPS
85
55
octal 3
45
2D
octal 4
45
2D
octal 5
45
2D
## END
## status: 0
## timeout: 10

#### printf -v dynamic scope
case $SH in mksh|zsh|dash|ash) echo not implemented; exit ;; esac
# OK so printf is like assigning to a var.
# printf -v foo %q "$bar" is like
# foo=${bar@Q}
dollar='dollar'
f() {
  local mylocal=foo
  printf -v dollar %q '$'  # assign foo to a quoted dollar
  printf -v mylocal %q 'mylocal'
  echo dollar=$dollar
  echo mylocal=$mylocal
}
echo dollar=$dollar
echo --
f
echo --
echo dollar=$dollar
echo mylocal=$mylocal
## STDOUT:
dollar=dollar
--
dollar=\$
mylocal=mylocal
--
dollar=\$
mylocal=
## END
## status: 0
## timeout: 10
## real-launcher

#### Too large
case $SH in mksh) echo 'weird bug'; exit ;; esac

echo too large
too_large=$(python2 -c 'print("\xF4\x91\x84\x91")')
printf '%x\n' \'$too_large
printf '%u\n' \'$too_large
printf '%o\n' \'$too_large
echo




# osh rejects code points that are too large for a DIFFERENT reason
## STDOUT:
too large
0
0
0

## END
## status: 0
## timeout: 10
## real-launcher

#### printf %c unicode - prints the first BYTE of a string - it does not respect UTF-8

# TODO: in YSH, this should be deprecated
case $SH in dash|ash) exit ;; esac

show_bytes() {
  od -A n -t x1
}
twomu=$'\u03bc\u03bc'
printf '[%s]\n' "$twomu"

# Hm this cuts off a UTF-8 character?
printf '%c' "$twomu" | show_bytes
## STDOUT:
[μμ]
 ce
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-printf builtin-printf (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### printf %d with + prefix (positive sign)

echo decimal
printf '%d\n' +42

echo octal
printf '%d\n' +077

echo hex lowercase
printf '%d\n' +0xab

echo hex uppercase
printf '%d\n' +0XAB
## STDOUT:
decimal
42
octal
63
hex lowercase
171
hex uppercase
171
## END
## status: 0
## timeout: 10

#### leading spaces are accepted in value given to %d %X, but not trailing spaces

case $SH in zsh) exit ;; esac

# leading space is allowed
printf '%d\n' ' -123'
echo status=$?
printf '%d\n' ' -123 '
echo status=$?

echo ---

printf '%d\n' ' +077'
echo status=$?

printf '%d\n' ' +0xff'
echo status=$?

printf '%X\n' ' +0xff'
echo status=$?

printf '%x\n' ' +0xff'
echo status=$?
## STDOUT:
-123
status=0
-123
status=1
---
63
status=0
255
status=0
FF
status=0
ff
status=0
## END
## status: 0
## timeout: 10
## real-launcher

#### Arbitrary base 64#a is rejected (unlike in shell arithmetic)

printf '%d\n' '64#a'
echo status=$?

# bash, dash, and mksh print 64 and return status 1
# zsh and ash print 0 and return status 1
# OSH rejects it completely (prints nothing) and returns status 1
## STDOUT:
64
status=1
## END
## status: 0
## timeout: 10
