## Imported from the Oils spec tests (nocasematch-match.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### [[ equality matching
shopt -s nocasematch
[[ a == A ]]; echo $?
[[ A == a ]]; echo $?
[[ A == [a] ]]; echo $?
[[ a == [A] ]]; echo $?
## STDOUT:
0
0
0
0
## END
## status: 0
## timeout: 10

#### [[ regex matching
shopt -s nocasematch
[[ a =~ A ]]; echo $?
[[ A =~ a ]]; echo $?
[[ a =~ [A] ]]; echo $?
[[ A =~ [a] ]]; echo $?
## STDOUT:
0
0
0
0
## END
## status: 0
## timeout: 10

#### [ matching
shopt -s nocasematch
[ a = A ]; echo $?
[ A = a ]; echo $?
## STDOUT:
1
1
## END
## status: 0
## timeout: 10

#### case matching
shopt -s nocasematch
case a in A) echo 0 ;; *) echo 1 ;; esac
case A in a) echo 0 ;; *) echo 1 ;; esac
case a in [A]) echo 0 ;; *) echo 1 ;; esac
case A in [a]) echo 0 ;; *) echo 1 ;; esac
## STDOUT:
0
0
0
0
## END
## status: 0
## timeout: 10

#### file matching
shopt -s nocasematch
touch a B
echo [A] [b]
## stdout: [A] [b]
## status: 0
## timeout: 10

#### parameter expansion matching
shopt -s nocasematch
foo=a
bar=A
echo "${foo#A}" "${foo#[A]}"
echo "${bar#a}" "${bar#[a]}"
## STDOUT:
a a
A A
## END
## status: 0
## timeout: 10
