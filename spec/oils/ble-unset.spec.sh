## Imported from the Oils spec tests (ble-unset.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### [bash_unset] nested context by tempenv-eval
f1() {
  local v=local1
  echo "[$1,local1] v: ${v-(unset)}"
  v=tempenv2 eval '
    echo "[$1,local1,tempenv2,(eval)] v: ${v-(unset)}"
    local v=local2
    echo "[$1,local1,tempenv2,(eval),local2] v: ${v-(unset)}"
  '
  echo "[$1,local1] v: ${v-(unset)} (after)"
}
v=global
v=tempenv1 f1 global,tempenv1
## STDOUT:
[global,tempenv1,local1] v: local1
[global,tempenv1,local1,tempenv2,(eval)] v: tempenv2
[global,tempenv1,local1,tempenv2,(eval),local2] v: local2
[global,tempenv1,local1] v: local1 (after)
## END
## status: 0
## timeout: 10

#### [bash_unset] local-unset / dynamic-unset for localvar on nested-context
unlocal() { unset -v "$1"; }

f2() {
  local v=local1
  v=tempenv2 eval '
    local v=local2
    (unset v  ; echo "[$1,local1,tempenv2,(eval),local2,(unset)] v: ${v-(unset)}")
    (unlocal v; echo "[$1,local1,tempenv2,(eval),local2,(unlocal)] v: ${v-(unset)}")
  '
}
v=global
v=tempenv1 f2 global,tempenv1


# Note that bash-4.3 to bash 5.0 behave differently
# [global,tempenv1,local1,tempenv2,(eval),local2,(unset)] v: local1
# [global,tempenv1,local1,tempenv2,(eval),local2,(unlocal)] v: local1

# always-value-unset
[global,tempenv1,local1,tempenv2,(eval),local2,(unset)] v: (unset)
[global,tempenv1,local1,tempenv2,(eval),local2,(unlocal)] v: (unset)
## STDOUT:
[global,tempenv1,local1,tempenv2,(eval),local2,(unset)] v: (unset)
[global,tempenv1,local1,tempenv2,(eval),local2,(unlocal)] v: local1
## END
## status: 2
## timeout: 10

#### [bash_unset] dynamic-unset for nested localvars
unlocal() { unset -v "$1"; }

f3() {
  local v=local1
  v=tempenv2 eval '
    local v=local2
    v=tempenv3 eval "
      local v=local3
      echo \"[\$1/local1,tempenv2/local2,tempenv3/local3] v: \${v-(unset)}\"
      unlocal v
      echo \"[\$1/local1,tempenv2/local2,tempenv3/local3] v: \${v-(unset)} (unlocal 1)\"
      unlocal v
      echo \"[\$1/local1,tempenv2/local2,tempenv3/local3] v: \${v-(unset)} (unlocal 2)\"
      unlocal v
      echo \"[\$1/local1,tempenv2/local2,tempenv3/local3] v: \${v-(unset)} (unlocal 3)\"
      unlocal v
      echo \"[\$1/local1,tempenv2/local2,tempenv3/local3] v: \${v-(unset)} (unlocal 4)\"
    "
  '
}
v=global
v=tempenv1 f3 global,tempenv1


# value-unset
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: local3
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: (unset) (unlocal 1)
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: (unset) (unlocal 2)
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: (unset) (unlocal 3)
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: (unset) (unlocal 4)


# cell-unset (remove all localvar/tempenv) x tempenv-value-unset
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: local3
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: tempenv1 (unlocal 1)
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: (unset) (unlocal 2)
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: (unset) (unlocal 3)
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: (unset) (unlocal 4)
## STDOUT:
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: local3
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: local2 (unlocal 1)
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: local1 (unlocal 2)
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: global (unlocal 3)
[global,tempenv1/local1,tempenv2/local2,tempenv3/local3] v: (unset) (unlocal 4)
## END
## status: 2
## timeout: 10

#### [bash_unset] dynamic-unset for nested tempenvs
unlocal() { unset -v "$1"; }

f4_unlocal() {
  v=tempenv2 eval '
    v=tempenv3 eval "
      echo \"[\$1,tempenv2,tempenv3] v: \${v-(unset)}\"
      unlocal v
      echo \"[\$1,tempenv2,tempenv3] v: \${v-(unset)} (unlocal 1)\"
      unlocal v
      echo \"[\$1,tempenv2,tempenv3] v: \${v-(unset)} (unlocal 2)\"
      unlocal v
      echo \"[\$1,tempenv2,tempenv3] v: \${v-(unset)} (unlocal 3)\"
      unlocal v
      echo \"[\$1,tempenv2,tempenv3] v: \${v-(unset)} (unlocal 4)\"
    "
  '
}
v=global
v=tempenv1 f4_unlocal global,tempenv1



# cell-unset
[global,tempenv1,tempenv2,tempenv3] v: tempenv3
[global,tempenv1,tempenv2,tempenv3] v: tempenv1 (unlocal 1)
[global,tempenv1,tempenv2,tempenv3] v: global (unlocal 2)
[global,tempenv1,tempenv2,tempenv3] v: (unset) (unlocal 3)
[global,tempenv1,tempenv2,tempenv3] v: (unset) (unlocal 4)

# remove all tempenv3
[global,tempenv1,tempenv2,tempenv3] v: tempenv3
[global,tempenv1,tempenv2,tempenv3] v: (unset) (unlocal 1)
[global,tempenv1,tempenv2,tempenv3] v: (unset) (unlocal 2)
[global,tempenv1,tempenv2,tempenv3] v: (unset) (unlocal 3)
[global,tempenv1,tempenv2,tempenv3] v: (unset) (unlocal 4)
## STDOUT:
[global,tempenv1,tempenv2,tempenv3] v: tempenv3
[global,tempenv1,tempenv2,tempenv3] v: tempenv2 (unlocal 1)
[global,tempenv1,tempenv2,tempenv3] v: tempenv1 (unlocal 2)
[global,tempenv1,tempenv2,tempenv3] v: global (unlocal 3)
[global,tempenv1,tempenv2,tempenv3] v: (unset) (unlocal 4)
## END
## status: 2
## timeout: 10

#### [bash_unset] local-unset for nested tempenvs
f4_unset() {
  v=tempenv2 eval '
    v=tempenv3 eval "
      echo \"[\$1,tempenv2,tempenv3] v: \${v-(unset)}\"
      unset v
      echo \"[\$1,tempenv2,tempenv3] v: \${v-(unset)} (unset 1)\"
      unset v
      echo \"[\$1,tempenv2,tempenv3] v: \${v-(unset)} (unset 2)\"
      unset v
      echo \"[\$1,tempenv2,tempenv3] v: \${v-(unset)} (unset 3)\"
      unset v
      echo \"[\$1,tempenv2,tempenv3] v: \${v-(unset)} (unset 4)\"
    "
  '
}
v=global
v=tempenv1 f4_unset global,tempenv1



# cell-unset
[global,tempenv1,tempenv2,tempenv3] v: tempenv3
[global,tempenv1,tempenv2,tempenv3] v: tempenv1 (unset 1)
[global,tempenv1,tempenv2,tempenv3] v: global (unset 2)
[global,tempenv1,tempenv2,tempenv3] v: (unset) (unset 3)
[global,tempenv1,tempenv2,tempenv3] v: (unset) (unset 4)
## STDOUT:
[global,tempenv1,tempenv2,tempenv3] v: tempenv3
[global,tempenv1,tempenv2,tempenv3] v: tempenv2 (unset 1)
[global,tempenv1,tempenv2,tempenv3] v: tempenv1 (unset 2)
[global,tempenv1,tempenv2,tempenv3] v: global (unset 3)
[global,tempenv1,tempenv2,tempenv3] v: (unset) (unset 4)
## END
## status: 2
## timeout: 10
