## Imported from the Oils spec tests (hay-isolation.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Turn off external binaries with shvar PATH='' {}
shopt --set parse_brace parse_proc

echo hi > file

# Note: this CACHES the lookup, so shvar has to clear cache when modifying it
cp -v file /tmp >&2
echo status=$?

# TODO: implement this, and call it whenever shvar mutates PATH?
# what about when PATH is mutated?   No leave it out for now.

# hash -r  # clear the cache, no longer necessary

shvar PATH='' {
  cp -v file /tmp
  echo status=$?

  # this also doesn't work
  command cp -v file /tmp
  echo status=$?
}

# Now it's available again
cp -v file /tmp >&2
echo status=$?
## STDOUT:
status=0
'file' -> '/tmp/file'
status=0
'file' -> '/tmp/file'
status=0
## END
## status: 2
## timeout: 10

#### variables mutated within hay eval don't persist
shopt --set parse_brace

hay define Package

setvar x = 42

hay eval :result {
  Package foo

  setvar x = 1
}

echo "x = $x"
## stdout:
## status: 2
## timeout: 10

#### hay at top level allows arbitrary commands
shopt --set parse_brace

hay define Package

Package $(seq 2) {
  seq 3 4
}

json write (_hay()) | jq '.children[0].args' > actual.txt

diff -u - actual.txt <<EOF
[
  "1",
  "2"
]
EOF

hay eval :result {
  echo inside
  Package $(seq 2) {
    seq 3 4
  }
}
## STDOUT:
3
4
## END
## status: 2
## timeout: 10
