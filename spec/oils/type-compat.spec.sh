## Imported from the Oils spec tests (type-compat.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### declare -i -l -u errors can be silenced - ignore_flags_not_impl

declare -i foo=2+3
echo status=$?
echo foo=$foo
echo

shopt -s ignore_flags_not_impl
declare -i bar=2+3
echo status=$?
echo bar=$bar


# bash doesn't need this
## STDOUT:
status=0
foo=5

status=0
bar=5
## END
## status: 0
## timeout: 10

#### declare -i with +=
declare s
s='1 '
s+=' 2 '  # string append

declare -i i
i='1 '
i+=' 2 '  # arith add

declare -i j
j=x  # treated like zero
j+=' 2 '  # arith add

echo "[$s]"
echo [$i]
echo [$j]
## STDOUT:
[1  2 ]
[3]
[2]
## END
## status: 0
## timeout: 10

#### declare -i with arithmetic inside strings (Nix, issue 864)

# example
# https://github.com/NixOS/nixpkgs/blob/master/pkgs/stdenv/generic/setup.sh#L379

declare -i s
s='1 + 2'
echo s=$s

declare -a array=(1 2 3)
declare -i item
item='array[1+1]'
echo item=$item
## STDOUT:
s=3
item=3
## END
## status: 0
## XFAIL ps-bash gap-oils-type-compat type-compat (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### append in arith context
declare s
(( s='1 '))
(( s+=' 2 '))  # arith add
declare -i i
(( i='1 ' ))
(( i+=' 2 ' ))
declare -i j
(( j='x ' ))  # treated like zero
(( j+=' 2 ' ))
echo "$s|$i|$j"
## stdout: ||
## status: 0
## XFAIL ps-bash gap-oils-type-compat type-compat (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### declare array vs. string: mixing -a +a and () ''
# dynamic parsing of first argument.
declare +a 'xyz1=1'
declare +a 'xyz2=(2 3)'
declare -a 'xyz3=4'
declare -a 'xyz4=(5 6)'
argv.py "${xyz1}" "${xyz2}" "${xyz3[@]}" "${xyz4[@]}"
## stdout:
## status: 127
## timeout: 10

#### declare array vs. associative array
# Hm I don't understand why the array only has one element.  I guess because
# index 0 is used twice?
declare -a 'array=([a]=b [c]=d)'
declare -A 'assoc=([a]=b [c]=d)'
argv.py "${#array[@]}" "${!array[@]}" "${array[@]}"
argv.py "${#assoc[@]}" "${!assoc[@]}" "${assoc[@]}"
## stdout:
## status: 127
## timeout: 10

#### declare -l -u

declare -l lower=FOO
declare -u upper=foo

echo $lower
echo $upper

# other:
# -t trace
# -I inherit attributes
## STDOUT:
foo
FOO
## END
## status: 0
## XFAIL ps-bash gap-oils-type-compat type-compat (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
