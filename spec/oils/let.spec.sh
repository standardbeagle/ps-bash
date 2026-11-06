## Imported from the Oils spec tests (let.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### let
# NOTE: no spaces are allowed.  How is this tokenized?
let x=1
let y=x+2
let z=y*3  # zsh treats this as a glob; bash doesn't
let z2='y*3'  # both are OK with this
echo $x $y $z $z2
## stdout: 1 3 9 9
## status: 0
## timeout: 10

#### let with ()
let x=( 1 )
let y=( x + 2 )
let z=( y * 3 )
echo $x $y $z
## stdout: 1 3 9
## status: 0
## timeout: 10
