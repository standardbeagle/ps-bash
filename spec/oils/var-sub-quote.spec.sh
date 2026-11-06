## Imported from the Oils spec tests (var-sub-quote.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### "${empty:-}"
empty=
argv.py "${empty:-}"
## stdout:
## status: 127
## timeout: 10

#### ${empty:-}
empty=
argv.py ${empty:-}
## stdout:
## status: 127
## timeout: 10

#### array with empty values
declare -a A=('' x "" '')
argv.py "${A[@]}"
## stdout:
## status: 127
## timeout: 10

#### substitution of IFS character, quoted and unquoted
IFS=:
s=:
argv.py $s
argv.py "$s"
## stdout:
## status: 127
## timeout: 10

#### :-
empty=''
argv.py ${empty:-a} ${Unset:-b}
## stdout:
## status: 127
## timeout: 10

#### -
empty=''
argv.py ${empty-a} ${Unset-b}
# empty one is still elided!
## stdout:
## status: 127
## timeout: 10

#### Inner single quotes
argv.py ${Unset:-'b'}
## stdout:
## status: 127
## timeout: 10

#### Inner single quotes, outer double quotes
# This is the WEIRD ONE.  Single quotes appear outside.  But all shells agree!
argv.py "${Unset:-'b'}"
## stdout:
## status: 127
## timeout: 10

#### Inner double quotes
argv.py ${Unset:-"b"}
## stdout:
## status: 127
## timeout: 10

#### Inner double quotes, outer double quotes
argv.py "${Unset-"b"}"
## stdout:
## status: 127
## timeout: 10

#### Multiple words: no outer quotes, inner double quotes
var='a b c'
argv.py ${Unset:-"$var"}
## stdout:
## status: 127
## timeout: 10

#### Multiple words: outer double quotes, no inner quotes
var='a b c'
argv.py "${Unset:-$var}"
## stdout:
## status: 127
## timeout: 10

#### Multiple words: outer double quotes, inner double quotes
var='a b c'
argv.py "${Unset:-"$var"}"
## stdout:
## status: 127
## timeout: 10

#### Multiple words: outer double quotes, inner single quotes
# WEIRD ONE.
#
# I think I should just disallow any word with single quotes inside double
# quotes.
var='a b c'
argv.py "${Unset:-'$var'}"
## stdout:
## status: 127
## timeout: 10

#### No outer quotes, Multiple internal quotes
# It's like a single command word.  Parts are joined directly.
var='a b c'
argv.py ${Unset:-A$var " $var"D E F}
## stdout:
## status: 127
## timeout: 10

#### Strip a string with single quotes, unquoted
foo="'a b c d'"
argv.py ${foo%d\'}
## stdout:
## status: 127
## timeout: 10

#### Strip a string with single quotes, double quoted
foo="'a b c d'"
argv.py "${foo%d\'}"
## stdout:
## status: 127
## timeout: 10

#### The string to strip is space sensitive
foo='a b c d'
argv.py "${foo%c d}" "${foo%c  d}"
## stdout:
## status: 127
## timeout: 10

#### The string to strip can be single quoted, outer is unquoted
foo='a b c d'
argv.py ${foo%'c d'} ${foo%'c  d'}
## stdout:
## status: 127
## timeout: 10

#### Syntax error for single quote in double quote
foo="'a b c d'"
argv.py "${foo%d'}"
## stdout:
## status: 2
## XFAIL ps-bash gap-oils-var-sub-quote var-sub-quote (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### $'' allowed within VarSub arguments
# Odd behavior of bash/mksh: $'' is recognized but NOT ''!
x=abc
echo ${x%$'b'*}
echo "${x%$'b'*}"  # git-prompt.sh relies on this
## STDOUT:
a
a
## END
## status: 0
## timeout: 10

#### # operator with single quoted arg (dash/ash and bash/mksh disagree, reported by Crestwave)
var=a
echo -${var#'a'}-
echo -"${var#'a'}"-
var="'a'"
echo -${var#'a'}-
echo -"${var#'a'}"-
## STDOUT:
--
--
-'a'-
-'a'-
## END
## status: 0
## timeout: 10

#### / operator with single quoted arg (causes syntax error in regex in OSH, reported by Crestwave)
var="++--''++--''"
echo no plus or minus "${var//[+-]}"
echo no plus or minus "${var//['+-']}"
## STDOUT:
no plus or minus ''''
no plus or minus ''''
## END
## status: 0
## timeout: 10

#### single quotes work inside character classes
x='a[[[---]]]b'
echo "${x//['[]']}"
## stdout: a---b
## status: 0
## timeout: 10

#### comparison: :- operator with single quoted arg
echo ${unset:-'a'}
echo "${unset:-'a'}"
## STDOUT:
a
'a'
## END
## status: 0
## timeout: 10

#### Right Brace as argument (similar to #702)

echo "${var-}}"
echo "${var-\}}"
echo "${var-'}'}"
echo "${var-"}"}"
## STDOUT:
}
}
'}'
}
## END
## status: 0
## timeout: 10

#### Var substitution with newlines (#2492)
echo "${var-a \
b}"
echo "${var-a
b}"

echo "${var:-c \
d}"
echo "${var:-c
d}"

var=set
echo "${var:+e \
f}"
echo "${var:+e
f}"
## STDOUT:
a b
a
b
c d
c
d
e f
e
f
## END
## status: 0
## timeout: 10

#### Var substitution with \n in value
echo "${var-a\nb}"
echo "${var:-c\nd}"
var=val
echo "${var:+e\nf}"
## STDOUT:
a\nb
c\nd
e\nf
## END
## status: 0
## timeout: 10

#### Multiple words: no quotes
argv.py ${Unset:-a b c}
## stdout:
## status: 127
## timeout: 10

#### Multiple words: no outer quotes, inner single quotes
argv.py ${Unset:-'a b c'}
## stdout:
## status: 127
## timeout: 10

#### Mixed inner quotes
argv.py ${Unset:-"a b" c}
## stdout:
## status: 127
## timeout: 10

#### Mixed inner quotes with outer quotes
argv.py "${Unset:-"a b" c}"
## stdout:
## status: 127
## timeout: 10

#### part_value tree with multiple words
argv.py ${a:-${a:-"1 2" "3 4"}5 "6 7"}
## stdout:
## status: 127
## timeout: 10

#### part_value tree on RHS
v=${a:-${a:-"1 2" "3 4"}5 "6 7"}
argv.py "${v}"
## stdout:
## status: 127
## timeout: 10

#### Var with multiple words: no quotes
var='a b c'
argv.py ${Unset:-$var}
## stdout:
## status: 127
## timeout: 10

#### "${undef-'c d'}" and "${foo%'c d'}" are parsed differently

# quotes are LITERAL here
argv.py "${undef-'c d'}" "${undef-'c  d'}"
argv.py ${undef-'c d'} ${undef-'c  d'}

echo ---

# quotes are RESPECTED here
foo='a b c d'
argv.py "${foo%'c d'}" "${foo%'c  d'}"

case $SH in dash) exit ;; esac

argv.py "${foo//'c d'/zzz}" "${foo//'c  d'/zzz}"
argv.py "${foo//'c d'/'zzz'}" "${foo//'c  d'/'zzz'}"
## stdout: ---
## status: 127
## timeout: 10
## real-launcher
