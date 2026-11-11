## Imported from the Oils spec tests (brace-expansion.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### no expansion
echo {foo}
## stdout: {foo}
## status: 0
## timeout: 10

#### incomplete trailing expansion
echo {a,b}_{
## stdout: a_{ b_{
## status: 0
## timeout: 10

#### partial leading expansion
echo }_{a,b}
## stdout: }_a }_b
## status: 0
## timeout: 10

#### Char range expansion
echo -{a..e}-
## stdout: -a- -b- -c- -d- -e-
## status: 0
## timeout: 10

#### Char range expansion with step
echo -{a..e..2}-
## stdout: -a- -c- -e-
## status: 0
## timeout: 10

#### Char ranges with steps of the wrong sign
echo -{a..e..-2}-
echo -{e..a..2}-
## STDOUT:
-a- -c- -e-
-e- -c- -a-
## END
## status: 0
## timeout: 10

#### Descending char range expansion
echo -{e..a..-2}-
## stdout: -e- -c- -a-
## status: 0
## timeout: 10

#### Fixed width number range expansion
echo -{01..03}-
echo -{09..12}-  # doesn't become -012-, fixed width
echo -{12..07}-
## STDOUT:
-01- -02- -03-
-09- -10- -11- -12-
-12- -11- -10- -09- -08- -07-
## END
## status: 0
## timeout: 10

#### Inconsistent fixed width number range expansion
# zsh uses the first one, bash uses the max width?
echo -{01..003}-
## stdout: -001- -002- -003-
## status: 0
## timeout: 10

#### Inconsistent fixed width number range expansion
# zsh uses the first width, bash uses the max width?
echo -{01..3}-
## stdout: -01- -02- -03-
## status: 0
## timeout: 10

#### Adjacent comma and range works
echo -{a,b}{1..3}-
## stdout: -a1- -a2- -a3- -b1- -b2- -b3-
## status: 0
## timeout: 10

#### Range inside comma works
echo -{a,_{1..3}_,b}-
## stdout: -a- -_1_- -_2_- -_3_- -b-
## status: 0
## timeout: 10

#### Mixed comma and range doesn't work
echo -{a,b,1..3}-
## stdout: -a- -b- -1..3-
## status: 0
## timeout: 10

#### comma and invalid range (adjacent and nested)
echo -{a,b}{1...3}-
echo -{a,{1...3}}-
echo {a,b}{}
# osh doesn't expand ANYTHING on invalid syntax.  That's OK because of the test
# case below.
## STDOUT:
-a{1...3}- -b{1...3}-
-a- -{1...3}-
a{} b{}
## END
## status: 0
## timeout: 10

#### OSH provides an alternative to invalid syntax
echo -{a,b}\{1...3\}-
echo -{a,\{1...3\}}-
echo {a,b}\{\}
## STDOUT:
-a{1...3}- -b{1...3}-
-a- -{1...3}-
a{} b{}
## END
## status: 0
## timeout: 10

#### Side effect in expansion
# bash is the only one that does it first.  I guess since this is
# non-POSIX anyway, follow bash?
i=0
echo {a,b,c}-$((i++))
## stdout: a-0 b-1 c-2
## status: 0
## timeout: 10

#### Invalid brace expansions don't expand
echo {1.3}
echo {1...3}
echo {1__3}
## STDOUT:
{1.3}
{1...3}
{1__3}
## END
## status: 0
## timeout: 10

#### partial leading expansion 2
echo {x}_{a,b}
## stdout: {x}_a {x}_b
## status: 0
## timeout: 10

#### } in expansion
# hm they treat this the SAME.  Leftmost { is matched by first }, and then
# there is another } as the postfix.
echo {a,b}}
## stdout: a} b}
## status: 0
## timeout: 10

#### single expansion
echo {foo,bar}
## stdout: foo bar
## status: 0
## timeout: 10

#### double expansion
echo {a,b}_{c,d}
## stdout: a_c a_d b_c b_d
## status: 0
## timeout: 10

#### triple expansion
echo {0,1}{0,1}{0,1}
## stdout: 000 001 010 011 100 101 110 111
## status: 0
## timeout: 10

#### double expansion with single and double quotes
echo {'a',b}_{c,"d"}
## stdout: a_c a_d b_c b_d
## status: 0
## timeout: 10

#### expansion with mixed quotes
echo -{\X"b",'cd'}-
## stdout: -Xb- -cd-
## status: 0
## timeout: 10

#### expansion with simple var
a=A
echo -{$a,b}-
## stdout: -A- -b-
## status: 0
## timeout: 10

#### double expansion with simple var -- bash bug
# bash is inconsistent with the above
a=A
echo {$a,b}_{c,d}
## stdout: b_c b_d
## status: 0
## timeout: 10

#### double expansion with braced variable
# This fixes it
a=A
echo {${a},b}_{c,d}
## stdout: A_c A_d b_c b_d
## status: 0
## timeout: 10

#### double expansion with literal and simple var
a=A
echo {_$a,b}_{c,d}
## stdout: _ _ b_c b_d
## status: 0
## timeout: 10

#### expansion with command sub
a=A
echo -{$(echo a),b}-
## stdout: -a- -b-
## status: 0
## timeout: 10

#### expansion with arith sub
a=A
echo -{$((1 + 2)),b}-
## stdout: -3- -b-
## status: 0
## timeout: 10

#### double expansion with escaped literals
a=A
echo -{\$,\[,\]}-
## stdout: -$- -[- -]-
## status: 0
## timeout: 10

#### { in expansion
# bash and mksh treat this differently.  bash treats the
# first { is a prefix.  I think it's harder to read, and \{{a,b} should be
# required.
echo {{a,b}
## stdout: {a {b
## status: 0
## XFAIL ps-bash gap-oils-brace-expansion brace-expansion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### quoted { in expansion
echo \{{a,b}
## stdout: {a {b
## status: 0
## timeout: 10

#### Empty expansion
echo a{X,,Y}b
## stdout: aXb ab aYb
## status: 0
## timeout: 10

#### Empty alternative
# zsh and mksh don't do word elision, probably because they do brace expansion
# AFTER variable substitution.
argv.py {X,,Y,}
## stdout:
## status: 127
## timeout: 10

#### Empty alternative with empty string suffix
# zsh and mksh don't do word elision, probably because they do brace expansion
# AFTER variable substitution.
argv.py {X,,Y,}''
## stdout:
## status: 127
## timeout: 10

#### nested brace expansion
echo -{A,={a,b}=,B}-
## stdout: -A- -=a=- -=b=- -B-
## status: 0
## timeout: 10

#### triple nested brace expansion
echo -{A,={a,.{x,y}.,b}=,B}-
## stdout: -A- -=a=- -=.x.=- -=.y.=- -=b=- -B-
## status: 0
## timeout: 10

#### nested and double brace expansion
echo -{A,={a,b}{c,d}=,B}-
## stdout: -A- -=ac=- -=ad=- -=bc=- -=bd=- -B-
## status: 0
## timeout: 10

#### expansion on RHS of assignment
# I think bash's behavior is more consistent.  No splitting either.
v={X,Y}
echo $v
## stdout: {X,Y}
## status: 0
## timeout: 10

#### no expansion with RHS assignment
{v,x}=X
## stdout:
## status: 127
## timeout: 10

#### Tilde expansion
HOME=/home/foo
echo ~
HOME=/home/bar
echo ~
## STDOUT:
/home/foo
/home/bar
## END
## status: 0
## timeout: 10

#### Tilde expansion with brace expansion

# The brace expansion happens FIRST.  After that, the second token has tilde
# FIRST, so it gets expanded.  The first token has an unexpanded tilde, because
# it's not in the leading position.

HOME=/home/bob

# Command

echo {foo~,~}/bar

# Loop

for x in {foo~,~}/bar; do
  echo -- $x
done

# Array

a=({foo~,~}/bar)

for y in "${a[@]}"; do
  echo "== $y"
done
## STDOUT:
foo~/bar /home/bob/bar
-- foo~/bar
-- /home/bob/bar
== foo~/bar
== /home/bob/bar
## END
## status: 0
## timeout: 10

#### Two kinds of tilde expansion

HOME=/home/bob

# Command
echo ~{/src,root}

# Loop

for x in ~{/src,root}; do
  echo -- $x
done

# Array

a=(~{/src,root})

for y in "${a[@]}"; do
  echo "== $y"
done
## STDOUT:
/home/bob/src /root
-- /home/bob/src
-- /root
== /home/bob/src
== /root
## END
## status: 0
## XFAIL ps-bash gap-oils-brace-expansion brace-expansion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Tilde expansion come before var expansion
HOME=/home/bob
foo=~
echo $foo
foo='~'
echo $foo
# In the second instance, we expand into a literal ~, and since var expansion
# comes after tilde expansion, it is NOT tried again.
## STDOUT:
/home/bob
~
## END
## status: 0
## timeout: 10

#### Number range expansion
echo -{1..8..3}-
echo -{1..10..3}-
## STDOUT:
-1- -4- -7-
-1- -4- -7- -10-
## END
## status: 0
## timeout: 10

#### Ascending number range expansion with negative step is invalid
echo -{1..8..-3}-
## stdout: -1- -4- -7-
## status: 0
## timeout: 10

#### regression: -1 step disallowed
echo -{1..4..-1}-
## stdout: -1- -2- -3- -4-
## status: 0
## timeout: 10

#### regression: 0 step disallowed
echo -{1..4..0}-
## stdout: -1- -2- -3- -4-
## status: 0
## timeout: 10

#### Descending number range expansion with positive step is invalid
echo -{8..1..3}-
## stdout: -8- -5- -2-
## status: 0
## timeout: 10

#### Descending number range expansion with negative step
echo -{8..1..-3}-
# zsh behavior seems clearly wrong!
## stdout: -8- -5- -2-
## status: 0
## timeout: 10

#### Singleton ranges
echo {1..1}-
echo {-9..-9}-
echo {-9..-9..3}-
echo {-9..-9..-3}-
echo {a..a}-
## STDOUT:
1-
-9-
-9-
-9-
a-
## END
## status: 0
## timeout: 10

#### Singleton char ranges with steps
echo {a..a..2}-
echo {a..a..-2}-
# zsh is considered buggy because it implements {a..a} but not {a..a..1} !
## STDOUT:
a-
a-
## END
## status: 0
## timeout: 10

#### Mixed case char expansion is invalid
case $SH in *zsh) echo BUG; exit ;; esac
echo -{z..A}-
echo -{z..A..2}-
# This is exposed a weird bash bug!!!
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-brace-expansion brace-expansion (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Invalid brace expansions mixing characters and numbers
# zsh does something crazy like : ; < = > that I'm not writing
case $SH in *zsh) echo BUG; exit ;; esac
echo {1..a}
echo {z..3}
## STDOUT:
{1..a}
{z..3}
## END
## status: 0
## timeout: 10
## real-launcher
