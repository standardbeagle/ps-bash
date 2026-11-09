## Imported from the Oils spec tests (paren-ambiguity.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### (( closed with ) ) after multiple lines is command - #2337

(( echo 1
echo 2
(( x ))
: $(( x ))
echo 3
) )
## STDOUT:
1
2
3
## END
## status: 0
## timeout: 10

#### $(( closed with ) ) after multiple lines is command - #2337

echo $(( echo 1
echo 2
(( x ))
: $(( x ))
echo 3
) )
## stdout: 1 2 3
## status: 0
## XFAIL ps-bash gap-oils-paren-ambiguity paren-ambiguity (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### (((grep example - 4+ instances in regtest/aports - #2337

# https://oilshell.zulipchat.com/#narrow/channel/502349-osh/topic/.28.28.28.20not.20parsed.20like.20bash/with/518874141

# spaces help
good() {
  cputype=`( ( (grep cpu /proc/cpuinfo | cut -d: -f2) ; ($PRTDIAG -v |grep -i sparc) ; grep -i cpu /var/run/dmesg.boot ) | head -n 1) 2> /dev/null`
}

bad() {
  cputype=`(((grep cpu /proc/cpuinfo | cut -d: -f2) ; ($PRTDIAG -v |grep -i sparc) ; grep -i cpu /var/run/dmesg.boot ) | head -n 1) 2> /dev/null`
  #echo cputype=$cputype
}

good
bad
## stdout:
## status: 0
## timeout: 10

#### ((gzip example - zdiff package - #2337

# https://github.com/git-for-windows/git-sdk-64/blob/main/usr/bin/zdiff#L136

gzip_status=$(
  exec 4>&1
  (gzip -cdfq -- "$file1" 4>&-; echo $? >&4) 3>&- |
      ((gzip -cdfq -- "$file2" 4>&-
        echo $? >&4) 3>&- 5<&- </dev/null |
       eval "$cmp" /dev/fd/5 - >&3) 5<&0
)
echo bye
## stdout: bye
## status: 0
## XFAIL ps-bash gap-oils-paren-ambiguity paren-ambiguity (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ((pkg-config example - postfix package - #2337

icu_cppflags=`((pkg-config --cflags icu-uc icu-i18n) ||
                  (pkgconf --cflags icu-uc icu-i18n) ||
                  (icu-config --cppflags)) 2>/dev/null`
echo bye
## stdout: bye
## status: 0
## timeout: 10

#### ((test example - liblo package - #2337

if ! ((test x"$i" = x-g) || (test x"$i" = x-O2)); then
    CF="$CF $i"
fi
echo bye
## stdout: bye
## status: 0
## timeout: 10

#### $((which example - command sub versus arith sub - gnunet-gtk package

        gtk_update_icon_cache_bin="$((which gtk-update-icon-cache ||
echo /opt/gnome/bin/gtk-update-icon-cache)2>/dev/null)"

echo bye
## stdout: bye
## status: 0
## XFAIL ps-bash gap-oils-paren-ambiguity paren-ambiguity (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### (( closed with )) after multiple lines is parse error - #2337

$SH -c '
(( echo 1
echo 2
(( x ))
: $(( x ))
echo 3
))
'
if test $? -ne 0; then
  echo ok
fi
## stdout: ok
## status: 0
## timeout: 10
## real-launcher

#### $(( closed with )) after multiple lines is parse error - #2337

$SH -c '
echo $(( echo 1
echo 2
(( x ))
: $(( x ))
echo 3
))
'
if test $? -ne 0; then
  echo ok
fi
## stdout: ok
## status: 0
## timeout: 10
## real-launcher
