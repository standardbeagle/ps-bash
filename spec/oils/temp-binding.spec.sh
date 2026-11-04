## Imported from the Oils spec tests (temp-binding.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Temp bindings with local

f() {
  local x=x
  tmp='' local tx=tx

  # Hm both y and ty persist in bash/zsh
  eval 'local y=y'
  tmp='' eval 'local ty=ty'

  # Why does this have an effect in OSH?  Oh because 'unset' is a special
  # builtin
  if true; then
    x='X' unset x
    tx='TX' unset tx
    y='Y' unset y
    ty='TY' unset ty
  fi

  #unset y
  #unset ty

  echo x=$x
  echo tx=$tx
  echo y=$y
  echo ty=$ty
}

f
## STDOUT:
x=x
tx=tx
y=y
ty=ty
## END
## status: 0
## XFAIL ps-bash gap-oils-temp-binding temp-binding (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### FOO=bar $unset - temp binding, then empty argv from unquoted unset var (#2411)
foo=alive! $unset
echo $foo
## stdout: alive!
## status: 0
## timeout: 10

#### Temp bindings with unset

# key point:
# unset looks up the stack
# local doesn't though

x=42
unset x
echo x=$x

echo ---

x=42
tmp= unset x
echo x=$x

x=42
tmp= eval 'unset x'
echo x=$x

echo ---

shadow() {
  x=42
  x=tmp unset x
  echo x=$x
  
  x=42
  x=tmp eval 'unset x'
  echo x=$x
}

shadow

echo ---

case $SH in
  bash) set -o posix ;;
esac
shadow

# Now shadow

# unset is a special builtin
# type unset
## STDOUT:
x=
---
x=
x=
---
x=42
x=42
---
x=42
x=42
## END
## status: 0
## XFAIL ps-bash gap-oils-temp-binding temp-binding (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
