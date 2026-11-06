## Imported from the Oils spec tests (ble-features.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### [bash_unset] local-unset / dynamic-unset for localvar
unlocal() { unset -v "$1"; }

f1() {
  local v=local
  unset v
  echo "[$1,local,(unset)] v: ${v-(unset)}"
}
v=global
f1 global

f1() {
  local v=local
  unlocal v
  echo "[$1,local,(unlocal)] v: ${v-(unset)}"
}
v=global
f1 'global'



# always-value-unset
#   local-unset   = value-unset
#   dynamic-unset = value-unset
[global,local,(unset)] v: (unset)
[global,local,(unlocal)] v: (unset)
## STDOUT:
[global,local,(unset)] v: (unset)
[global,local,(unlocal)] v: global
## END
## status: 2
## XFAIL ps-bash gap-oils-ble-features ble-features (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### [bash_unset] local-unset / dynamic-unset for localvar (mutated from tempenv)
unlocal() { unset -v "$1"; }

f1() {
  local v=local
  unset v
  echo "[$1,local,(unset)] v: ${v-(unset)}"
}
v=global
v=tempenv f1 'global,tempenv'

f1() {
  local v=local
  unlocal v
  echo "[$1,local,(unlocal)] v: ${v-(unset)}"
}
v=global
v=tempenv f1 'global,tempenv'


# Note on bug in bash 4.3 to bash 5.0
# [global,tempenv,local,(unset)] v: global
# [global,tempenv,local,(unlocal)] v: global


# always-value-unset
#   local-unset   = value-unset
#   dynamic-unset = value-unset
[global,tempenv,local,(unset)] v: (unset)
[global,tempenv,local,(unlocal)] v: (unset)
## STDOUT:
[global,tempenv,local,(unset)] v: (unset)
[global,tempenv,local,(unlocal)] v: global
## END
## status: 2
## XFAIL ps-bash gap-oils-ble-features ble-features (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### [bash_unset] local-unset / dynamic-unset for tempenv
unlocal() { unset -v "$1"; }

f1() {
  unset v
  echo "[$1,(unset)] v: ${v-(unset)}"
}
v=global
v=tempenv f1 'global,tempenv'

f1() {
  unlocal v
  echo "[$1,(unlocal)] v: ${v-(unset)}"
}
v=global
v=tempenv f1 'global,tempenv'
## STDOUT:
[global,tempenv,(unset)] v: global
[global,tempenv,(unlocal)] v: global
## END
## status: 0
## XFAIL ps-bash gap-oils-ble-features ble-features (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### [bash_unset] function call with tempenv vs tempenv-eval
unlocal() { unset -v "$1"; }

f5() {
  echo "[$1] v: ${v-(unset)}"
  local v
  echo "[$1,local] v: ${v-(unset)}"
  ( unset v
    echo "[$1,local+unset] v: ${v-(unset)}" )
  ( unlocal v
    echo "[$1,local+unlocal] v: ${v-(unset)}" )
}
v=global
f5 'global'
v=tempenv f5 'global,tempenv'
v=tempenv eval 'f5 "global,tempenv,(eval)"'


# Note on bug in bash 4.3 to bash 5.0
# [global] v: global
# [global,local] v: (unset)
# [global,local+unset] v: (unset)
# [global,local+unlocal] v: global
# [global,tempenv] v: tempenv
# [global,tempenv,local] v: tempenv
# [global,tempenv,local+unset] v: global
# [global,tempenv,local+unlocal] v: global
# [global,tempenv,(eval)] v: tempenv
# [global,tempenv,(eval),local] v: tempenv
# [global,tempenv,(eval),local+unset] v: (unset)
# [global,tempenv,(eval),local+unlocal] v: tempenv

# always-value-unset x init.unset
[global] v: global
[global,local] v: (unset)
[global,local+unset] v: (unset)
[global,local+unlocal] v: (unset)
[global,tempenv] v: tempenv
[global,tempenv,local] v: tempenv
[global,tempenv,local+unset] v: (unset)
[global,tempenv,local+unlocal] v: (unset)
[global,tempenv,(eval)] v: tempenv
[global,tempenv,(eval),local] v: (unset)
[global,tempenv,(eval),local+unset] v: (unset)
[global,tempenv,(eval),local+unlocal] v: (unset)

# always-value-unset x init.empty
[global] v: global
[global,local] v: 
[global,local+unset] v: (unset)
[global,local+unlocal] v: (unset)
[global,tempenv] v: tempenv
[global,tempenv,local] v: 
[global,tempenv,local+unset] v: (unset)
[global,tempenv,local+unlocal] v: (unset)
[global,tempenv,(eval)] v: tempenv
[global,tempenv,(eval),local] v: 
[global,tempenv,(eval),local+unset] v: (unset)
[global,tempenv,(eval),local+unlocal] v: (unset)

# always-value-unset x init.inherit
[global] v: global
[global,local] v: global
[global,local+unset] v: (unset)
[global,local+unlocal] v: (unset)
[global,tempenv] v: tempenv
[global,tempenv,local] v: tempenv
[global,tempenv,local+unset] v: (unset)
[global,tempenv,local+unlocal] v: (unset)
[global,tempenv,(eval)] v: tempenv
[global,tempenv,(eval),local] v: tempenv
[global,tempenv,(eval),local+unset] v: (unset)
[global,tempenv,(eval),local+unlocal] v: (unset)
## STDOUT:
[global] v: global
[global,local] v: (unset)
[global,local+unset] v: (unset)
[global,local+unlocal] v: global
[global,tempenv] v: tempenv
[global,tempenv,local] v: tempenv
[global,tempenv,local+unset] v: (unset)
[global,tempenv,local+unlocal] v: global
[global,tempenv,(eval)] v: tempenv
[global,tempenv,(eval),local] v: tempenv
[global,tempenv,(eval),local+unset] v: (unset)
[global,tempenv,(eval),local+unlocal] v: tempenv
## END
## status: 2
## XFAIL ps-bash gap-oils-ble-features ble-features (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### [bash_unset] localvar-inherit from tempenv
f1() {
  local v
  echo "[$1,(local)] v: ${v-(unset)}"
}
f2() {
  f1 "$1,(func)"
}
f3() {
  local v=local
  f1 "$1,local,(func)"
}
v=global

f1 'global'
v=tempenv f1 'global,tempenv'
(export v=global; f1 'xglobal')

f2 'global'
v=tempenv f2 'global,tempenv'
(export v=global; f2 'xglobal')

f3 'global'



# init.unset x tempenv-in-localctx
[global,(local)] v: (unset)
[global,tempenv,(local)] v: tempenv
[xglobal,(local)] v: (unset)
[global,(func),(local)] v: (unset)
[global,tempenv,(func),(local)] v: (unset)
[xglobal,(func),(local)] v: (unset)
[global,local,(func),(local)] v: (unset)

# init.empty
[global,(local)] v: 
[global,tempenv,(local)] v: 
[xglobal,(local)] v: 
[global,(func),(local)] v: 
[global,tempenv,(func),(local)] v: 
[xglobal,(func),(local)] v: 
[global,local,(func),(local)] v: 

# init.inherit
[global,(local)] v: global
[global,tempenv,(local)] v: tempenv
[xglobal,(local)] v: global
[global,(func),(local)] v: global
[global,tempenv,(func),(local)] v: tempenv
[xglobal,(func),(local)] v: global
[global,local,(func),(local)] v: local
## STDOUT:
[global,(local)] v: (unset)
[global,tempenv,(local)] v: tempenv
[xglobal,(local)] v: (unset)
[global,(func),(local)] v: (unset)
[global,tempenv,(func),(local)] v: tempenv
[xglobal,(func),(local)] v: (unset)
[global,local,(func),(local)] v: (unset)
## END
## status: 2
## XFAIL ps-bash gap-oils-ble-features ble-features (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### [compat_array] ${arr} is ${arr[0]}
case ${SH##*/} in dash|ash) exit 1 ;; esac # dash/ash does not have arrays
case ${SH##*/} in osh) shopt -s compat_array ;; esac
case ${SH##*/} in zsh) setopt KSH_ARRAYS ;; esac
arr=(foo bar baz)
argv.py "$arr" "${arr}"
## stdout:
## status: 127
## timeout: 10

#### [compat_array] scalar write to arrays
case ${SH##*/} in
(dash|ash) exit 1;; # dash/ash does not have arrays
(osh) shopt -s compat_array;;
(zsh) setopt KSH_ARRAYS;;
esac

a=(1 0 0)
: $(( a++ ))
argv.py "${a[@]}"
## stdout:
## status: 127
## timeout: 10

#### [compat_array] scalar write to associative arrays
case ${SH##*/} in
(dash|ash|yash|mksh) exit 1;; # dash/ash/yash/mksh does not have associative arrays
(osh) shopt -s compat_array;;
(zsh) setopt KSH_ARRAYS;;
esac

declare -A d=()
d['0']=1
d['foo']=hello
d['bar']=world
((d++))
argv.py ${d['0']} ${d['foo']} ${d['bar']}
## stdout:
## status: 127
## timeout: 10

#### [compat_array] ${alpha@a}
declare -A alpha=(['1']=2)
echo type=${alpha@a}
shopt -s compat_array
echo type=${alpha@a}
## STDOUT:
type=A
type=A
## END
## status: 0
## timeout: 10
