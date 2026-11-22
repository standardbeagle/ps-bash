# Container smoke test for ps-bash (docker/windows, docker/linux; .github/workflows/container-smoke.yml).
# Prints SMOKE-OK and exits 0 when every check holds; otherwise names the first failed check and exits 1.
fail() { echo "SMOKE-FAIL: $*"; exit 1; }

[ "$(printf 'b\na\nb\n' | sort | uniq -c | sed 's/^ *//' | tr '\n' ' ')" = "1 a 2 b " ] || fail "pipeline"
[ $(( 6 * 7 )) -eq 42 ] || fail "arithmetic"
arr=(x y z); [ "${#arr[@]}:${arr[1]}" = "3:y" ] || fail "array"
declare -A m=([k]=v); [ "${m[k]}" = "v" ] || fail "associative array"
f() { local v="$1"; echo "<$v>"; }; [ "$(f 'a b')" = "<a b>" ] || fail "function"
cd "$(mktemp -d)" || fail "mktemp"
cat > h.txt <<'EOF'
one $HOME
EOF
[ "$(cat h.txt)" = 'one $HOME' ] || fail "heredoc"
printf '%s\n' c a b > l.txt; [ "$(sort l.txt | head -1)" = "a" ] || fail "file read"
( exit 3 ); [ $? -eq 3 ] || fail "subshell status"
[[ "abc" == a* ]] || fail "[[ pattern"

# An OS program, and a detached one writing a redirected file (nohup).
if [ "${OS:-}" = "Windows_NT" ]; then
    [ "$(cmd.exe /d /c echo hi | tr -d '\r')" = "hi" ] || fail "external program"
    nohup cmd.exe /d /c "echo E1 1>&2" > n.txt 2>&1 || fail "nohup status"
else
    [ "$(/bin/echo hi)" = "hi" ] || fail "external program"
    nohup /bin/sh -c 'echo E1 >&2' > n.txt 2>&1 || fail "nohup status"
fi
[ "$(tr -d '\r ' < n.txt)" = "E1" ] || fail "nohup redirect: [$(cat n.txt)]"

echo "ps-bash $BASH_VERSION, $(ls | wc -l | tr -d ' ') files in $(pwd)"
echo SMOKE-OK
