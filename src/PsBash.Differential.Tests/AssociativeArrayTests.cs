using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>Associative-array literals, key/value expansion, <c>unset arr[i]</c> and variable subscripts.</summary>
public class AssociativeArrayTests
{
    [Fact] public Task Literal_ReadsAndCounts() => AssertOracle.EqualAsync("declare -A m=([k1]=v1 [k2]=v2); echo \"${m[k1]} ${m[k2]} ${#m[@]}\"");
    [Fact] public Task Keys_Loop() => AssertOracle.EqualAsync("declare -A m=([k1]=v1 [k2]=v2); for k in \"${!m[@]}\"; do echo \"k=$k\"; done | sort");
    [Fact] public Task VariableSubscript() => AssertOracle.EqualAsync("declare -A n; n[x]=1; n[y]=2; for k in \"${!n[@]}\"; do echo \"$k=${n[$k]}\"; done | sort");
    [Fact] public Task Values_Expand() => AssertOracle.EqualAsync("declare -A m=([a]=1 [b]=2 [c]=3); for v in \"${m[@]}\"; do echo $v; done | sort; echo \"${m[@]}\" | tr ' ' '\\n' | sort | tr '\\n' ' '; echo");
    [Fact] public Task Unset_Key() => AssertOracle.EqualAsync("declare -A n; n[x]=1; n[y]=2; unset 'n[x]'; echo \"${#n[@]} [${n[x]}] ${n[y]}\"");
    [Fact] public Task Unset_IndexedElement() => AssertOracle.EqualAsync("b=(p q r); unset 'b[1]'; echo \"${#b[@]} ${b[@]}\"");
    [Fact] public Task IndexedArray_VariableIndex() => AssertOracle.EqualAsync("c=(a b c); for i in 0 1 2; do echo -n \"${c[$i]} \"; done; echo");
    [Fact] public Task Literal_WithExpansionValue() => AssertOracle.EqualAsync("v=zz; declare -A m=([a]=$v [b]=\"x y\"); echo \"${m[a]}|${m[b]}\"");

    // Enumeration follows bash's hash-table order (1024 buckets, FNV-1), not insertion order and not sorted; keys are case-sensitive.
    [Fact] public Task Order_KeysMatchBash() => AssertOracle.EqualAsync("declare -A m; for k in x y b a A z Z 0 1 10 _x; do m[$k]=1; done; echo \"${!m[@]}\"");
    [Fact] public Task Order_NonAsciiKeys() => AssertOracle.EqualAsync("declare -A m; for k in é ü ñ 日本 ключ; do m[$k]=1; done; echo \"${!m[@]}\"");
    [Fact] public Task Order_AfterUnsetAndReAdd() => AssertOracle.EqualAsync("declare -A m; for k in a b c d e f; do m[$k]=1; done; unset 'm[c]'; m[c]=2; m[g]=3; echo \"${!m[@]}\"; echo \"${m[@]}\"");
    [Fact] public Task Order_DeclareP() => AssertOracle.EqualAsync("declare -A m=([x]=1 [y]=2 [b]=3 [a]=4 [A]=5); declare -p m");
    [Fact] public Task Keys_AreCaseSensitive() => AssertOracle.EqualAsync("declare -A m; m[a]=1; m[A]=2; echo \"${#m[@]} ${m[a]} ${m[A]}\"");
    [Fact] public Task Order_ManyKeys() => AssertOracle.EqualAsync("declare -A m; for i in $(seq 1 60); do m[k$i]=$i; done; echo \"${!m[@]}\"");
    [Fact] public Task WordListAssign_IsKeyValuePairs() => AssertOracle.EqualAsync("declare -A a=([k]=v); a=(foo 'spam eggs'); declare -p a; declare -A b=(1 2 3); declare -p b");
    [Fact] public Task LiteralAppend_Merges() => AssertOracle.EqualAsync("declare -A a=([apple]=red [orange]=orange); a+=([lemon]=yellow [apple]=green); declare -p a");
    [Fact] public Task AppendedElement_Subscript_Tilde_IsLiteral() => AssertOracle.EqualAsync("declare -A m; m[~]=v; m[~+]=w; echo \"${!m[@]}\"");
}