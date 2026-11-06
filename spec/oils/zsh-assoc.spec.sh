## Imported from the Oils spec tests (zsh-assoc.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### TODO: SETUP should be shared
typeset -A a
a=(aa b foo bar a+1 c)
## stdout:
## status: 0
## timeout: 10

#### retrieve key
typeset -A a
a=(aa b foo bar a+1 c)
echo ${a[aa]}
## stdout: b
## status: 0
## XFAIL ps-bash gap-oils-zsh-assoc zsh-assoc (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### set key
typeset -A a
a=(aa b foo bar a+1 c)
a[X]=XX
argv.py "${a[@]}"
# What order is this?
## stdout:
## status: 127
## timeout: 10

#### iterate over keys
typeset -A assoc
assoc=(k1 v1 k2 v2 k3 v3)
for k in "${(@k)assoc}"; do
  echo "$k: $assoc[$k]"
done
## stdout:
## status: 1
## timeout: 10

#### iterate over both keys and values
typeset -A assoc
assoc=(k1 v1 k2 v2 k3 v3)
for k v ("${(@kv)assoc}"); do
  echo "$k: $v"
done
## stdout:
## status: 2
## timeout: 10

#### get length
typeset -A assoc
assoc=(k1 v1 k2 v2 k3 v3)
echo ${#assoc} ${#assoc[k1]}
## stdout: 0 2
## status: 0
## timeout: 10

#### index by integer does not work
typeset -A assoc
assoc=(k1 v1 k2 v2 k3 v3)
argv.py "${assoc[1]}"
## stdout:
## status: 127
## timeout: 10
