## Imported from the Oils spec tests (for-expr.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### C-style for loop
n=10
for ((a=1; a <= n ; a++))  # Double parentheses, and naked 'n'
do
  if test $a = 3; then
    continue
  fi
  if test $a = 6; then
    break
  fi
  echo $a
done
## STDOUT:
1
2
4
5
## END
## status: 0
## timeout: 10

#### For loop with and without semicolon
for ((a=1; a <= 3; a++)); do
  echo $a
done
for ((a=1; a <= 3; a++)) do
  echo $a
done
## STDOUT:
1
2
3
1
2
3
## END
## status: 0
## timeout: 10

#### Accepts { } syntax too
for ((a=1; a <= 3; a++)) {
  echo $a
}
## STDOUT:
1
2
3
## END
## status: 0
## XFAIL ps-bash gap-oils-for-expr for-expr (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Empty init
i=1
for ((  ;i < 4;  i++ )); do
  echo $i
done
## STDOUT:
1
2
3
## END
## status: 0
## timeout: 10

#### Empty init and cond
i=1
for ((  ; ;  i++ )); do
  if test $i = 4; then
    break
  fi
  echo $i
done
## STDOUT:
1
2
3
## END
## status: 0
## timeout: 10

#### Infinite loop with ((;;))
a=1
for ((  ;  ;  )); do
  if test $a = 4; then
    break
  fi
  echo $((a++))
done
## STDOUT:
1
2
3
## END
## status: 0
## timeout: 10

#### Arith lexer mode

# bash is lenient; zsh disagrees

for ((i = '3';  i < '5';  ++i)); do echo $i; done
for ((i = "3";  i < "5";  ++i)); do echo $i; done
for ((i = $'3'; i < $'5'; ++i)); do echo $i; done
for ((i = $"3"; i < $"5"; ++i)); do echo $i; done
## STDOUT:
3
4
3
4
## END
## status: 0
## XFAIL ps-bash gap-oils-for-expr for-expr (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Integers near 31, 32, 62 bits

# Hm this was never a bug, but it's worth testing.
# The bug was EvalToInt() in the condition.

for base in 31 32 62; do

  start=$(( (1 << $base) - 2))
  end=$(( (1 << $base) + 2))

  for ((i = start; i < end; ++i)); do
    echo $i
  done
  echo ---
done
## STDOUT:
2147483646
2147483647
2147483648
2147483649
---
4294967294
4294967295
4294967296
4294967297
---
4611686018427387902
4611686018427387903
4611686018427387904
4611686018427387905
---
## END
## status: 0
## timeout: 10

#### Condition that's greater than 32 bits

iters=0

for ((i = 1 << 32; i; ++i)); do
  echo $i
  iters=$(( iters + 1 ))
  if test $iters -eq 5; then
    break
  fi
done
## STDOUT:
4294967296
4294967297
4294967298
4294967299
4294967300
## END
## status: 0
## timeout: 10
