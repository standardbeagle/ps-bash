## Absorbed from Differential.Tests/BashSourceTests.cs (scripts only; expectations recorded from bash 5.2).

#### RunAsync
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
printf 'echo "in-lib n=${#BASH_SOURCE[@]}"\n[ "${BASH_SOURCE[1]}" = "$0" ] && echo caller-ok\n' > "$DIR/psb-lib-$$.sh"
source "$DIR/psb-lib-$$.sh"; rc=$?; rm -f "$DIR/psb-lib-$$.sh"
echo "rc=$rc after n=${#BASH_SOURCE[@]}"
## STDOUT:
in-lib n=2
caller-ok
rc=0 after n=1
## END
## status: 0
## real-launcher

#### RunAsync row 2
echo "n=${#BASH_SOURCE[@]}"; [ "${BASH_SOURCE[0]}" = "$0" ] && echo same || echo different
## STDOUT:
n=1
same
## END
## status: 0
## real-launcher

#### RunAsync row 3
[ "${BASH_SOURCE[0]:-$0}" = "$0" ] && echo ok
## stdout: ok
## status: 0

#### RunAsync row 4
bash -c 'echo "n=${#BASH_SOURCE[@]} [${BASH_SOURCE[0]}] fb=[${BASH_SOURCE[0]:-$0}]"'
## stdout: n=0 [] fb=[bash]
## status: 0
