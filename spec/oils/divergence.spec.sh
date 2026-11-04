## Imported from the Oils spec tests (divergence.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### xz package: dirprefix="${line##*([}"

# https://oilshell.zulipchat.com/#narrow/channel/502349-osh/topic/alpine.20xz.20-.20.22.24.7Bline.23.23*.28.5B.7D.22.20interpreted.20as.20extended.20glob/with/519718284

# NOTE: spec/extglob-match shows that bash respects it
#
# echo 'strip ##' ${x##@(foo)}

shopt -s extglob


dirprefix="${line##*([}"
echo "-$dirprefix-"

# Now try with real data
line='*([foo'
dirprefix="${line##*([}"
echo "-$dirprefix-"
## STDOUT:
--
-foo-
## END
## status: 0
## timeout: 10

#### !( as negation and subshell versus extended glob - #2463

have_icu_uc=false
have_icu_i18n=false

if !($have_icu_uc && $have_icu_i18n); then
  echo one
fi
echo two
## STDOUT:
one
two
## END
## status: 0
## timeout: 10

#### Changing PATH will invalidate PATH cache

mkdir -p _tmp/bin
mkdir -p _tmp/bin2
printf '#!/usr/bin/env sh\necho hi\n' >_tmp/bin/hello
printf '#!/usr/bin/env sh\necho hey\n' >_tmp/bin2/hello
chmod +x _tmp/bin/hello
chmod +x _tmp/bin2/hello

BIN=$PWD/_tmp/bin
BIN2=$PWD/_tmp/bin2

# Will find _tmp/bin/hello
PATH="$BIN:$PATH" hello
echo status=$?

# Should invalidate cache and then find _tmp/bin2/hello
PATH="$BIN2:$PATH" hello
echo status=$?

# Same when PATH= and export PATH=
PATH="$BIN:$PATH"
hello
echo status=$?
PATH="$BIN2:$PATH"
hello
echo status=$?

export PATH="$BIN:$PATH"
hello
echo status=$?
export PATH="$BIN2:$PATH"
hello
echo status=$?
## STDOUT:
hi
status=0
hey
status=0
hi
status=0
hey
status=0
hi
status=0
hey
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-divergence divergence (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### builtin cat crashes a subshell (#2530)

((/usr/bin/cat </dev/zero; echo $? >&7) | true) 7>&1

((cat </dev/zero; echo $? >&7) | true) 7>&1
## STDOUT:
141
141
## END
## status: 0
## XFAIL ps-bash gap-oils-divergence divergence (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
