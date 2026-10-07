using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// Under <c>set -e</c> the emitter adds errexit checks after statements, runtime suppression around
/// exempt contexts (conditions, non-final <c>&amp;&amp;</c>/<c>||</c> operands, <c>!</c>, command
/// substitutions) and a tail marker on the final operand of a list. Every construct that emission
/// touches must still parse as PowerShell (ParseabilityContract branch A): broken emission breaks
/// the whole script before any of it runs. The oracle is PowerShell's parser (see ParseabilityContract).
/// </summary>
public class ErrexitParseabilityTests
{
    [Theory]
    [InlineData("set -e; cat /nofile; echo after")]
    [InlineData("set -euo pipefail; cat /nofile")]
    [InlineData("set -e; f() { cat /nofile; echo in; }; f; echo after")]
    [InlineData("set -e; if f; then echo y; elif g; then :; fi")]
    [InlineData("set -e; if ! f; then echo neg; fi")]
    [InlineData("set -e; while cat /nofile; do :; done; until g; do break; done")]
    [InlineData("set -e; f() { i=0; while [ $i -lt 2 ]; do i=$((i+1)); done; }; f")]
    [InlineData("set -e; a && b || c")]
    [InlineData("set -e; a && { b; c; }")]
    [InlineData("set -e; { a; b; } || exit 1")]
    [InlineData("set -e; cd /nodir || exit 1")]
    [InlineData("set -e; for i in 1 2; do [ $i = 2 ] && break; echo $i; done")]
    [InlineData("set -e; ! cat /nofile")]
    [InlineData("set -e; echo hi | cat /nofile | wc -l")]
    [InlineData("set -e; (cat /nofile; echo in); (exit 3) || echo r")]
    [InlineData("set -e; ( (a; b); c )")]
    [InlineData("set -e; x=$(cat /nofile); y=\"$(f)\"; export Z=$(false); local_v=1")]
    [InlineData("set -e; echo \"$(f) and $(g | h)\"")]
    [InlineData("set -e; case $x in a) cat /nofile; echo in;; *) :;; esac")]
    [InlineData("set -e; [ -f /nofile ]; [[ $x == 1 ]] && echo one; ((i++))")]
    [InlineData("set -e; cat /nofile & wait")]
    [InlineData("set -e; cat /nofile > /tmp/psb_out 2>&1; echo after")]
    [InlineData("set -e; eval 'cat /nofile; echo in'")]
    [InlineData("set -e; set +e; cat /nofile")]
    [InlineData("set -e; f() { false; echo in-f; }; g() { f; echo in-g; }; g && echo ok")]
    [InlineData("set -e; while read -r l; do echo \"$l\"; done < /tmp/psb_in")]
    public void Transpile_UnderSetE_EmitsParseablePowerShell(string bash)
        => ParseabilityContract.Assert(bash, "errexit emission");
}
