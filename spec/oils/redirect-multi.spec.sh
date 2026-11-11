## Imported from the Oils spec tests (redirect-multi.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### File redirects with glob args (bash and zsh only)

touch one-bar

echo hi > one-*

cat one-bar

echo escaped > one-\*

cat one-\*
## STDOUT:
hi
escaped
## END
## status: 0
## XFAIL ps-bash gap-oils-redirect-multi redirect-multi (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### File redirect without matching any file

echo hi > zz-*-xx
echo status=$?

echo zz*
## STDOUT:
status=0
zz-*-xx
## END
## status: 0
## XFAIL ps-bash gap-oils-redirect-multi redirect-multi (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### File redirect without matching any file, with failglob

shopt -s failglob

echo hi > zz-*-xx
echo status=$?

echo zz*
echo status=$?
## STDOUT:
status=1
status=1
## END
## status: 0
## timeout: 10

#### Redirect to $empty (in function body)
empty=''
fun() { echo hi; } > $empty
fun
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### Redirect to ''
echo hi > ''
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### File redirect to $var with glob char

touch two-bar

star='*'

# This gets glob-expanded, as it does outside redirects
echo hi > two-$star
echo status=$?

head two-bar two-\*
## STDOUT:
status=0
==> two-bar <==
hi
## END
## status: 1
## XFAIL ps-bash gap-oils-redirect-multi redirect-multi (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### File redirect that globs to more than one file (bash and zsh only)

touch foo-bar
touch foo-spam

echo hi > foo-*
echo status=$?

head foo-bar foo-spam
## STDOUT:
status=1
==> foo-bar <==

==> foo-spam <==
## END
## status: 0
## timeout: 10

#### File redirect with extended glob

shopt -s extglob

touch foo-bar

echo hi > @(*-bar|other)
echo status=$?

cat foo-bar
## STDOUT:
status=0
hi
## END
## status: 0
## timeout: 10

#### Non-file redirects don't respect glob args (we differe from bash)

touch 10

exec 10>&1  # open stdout as descriptor 10

# Does this go to stdout?  ONLY bash respects it, not zsh
echo should-not-be-on-stdout >& 1*

echo stdout
echo stderr >&2
## STDOUT:
should-not-be-on-stdout
stdout
## END
## status: 0
## XFAIL ps-bash gap-oils-redirect-multi redirect-multi (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Redirect with brace expansion isn't allowed

echo hi > a-{one,two}
echo status=$?

head a-*
echo status=$?
## STDOUT:
status=1
status=1
## END
## status: 0
## timeout: 10

#### File redirects have word splitting too!

file='foo bar'

echo hi > $file
echo status=$?

cat "$file"
echo status=$?
## STDOUT:
status=1
status=1
## END
## status: 0
## timeout: 10
