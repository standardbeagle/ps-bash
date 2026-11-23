using System.Text;
using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// GNU-vs-ps-bash comparisons for BINARY safety (docs/specs/runtime-functions.md "Raw bytes"): bytes that
/// are not valid UTF-8 survive every boundary. Stdout stays ASCII (counts, base64, digests) so the
/// canonicalizer cannot hide a difference; file CONTENT is compared byte for byte through the FsStateOracle
/// snapshot (base64 of every file in the resulting tree).
/// </summary>
public class RawBytesDifferentialTests
{
    /// <summary>A printf format that writes exactly <paramref name="bytes"/> (every byte as a \NNN octal escape).</summary>
    private static string Oct(IEnumerable<byte> bytes) =>
        string.Concat(bytes.Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));

    private static readonly string AllBytes = Oct(Enumerable.Range(0, 256).Select(i => (byte)i));

    private static readonly string Random1k = Oct(Rand(3, 1000));

    private static byte[] Rand(int seed, int n)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static string MkBin(string name, string octal) => $"printf '{octal}' > {name}";

    // ───────────── the acceptance probes ─────────────

    [SkippableFact] public Task Printf_LoneInvalidByte_WcC_IsOne() =>
        AssertOracle.EqualAsync(@"printf '\351' | wc -c");

    [SkippableFact] public Task Printf_HexInvalidByte_WcC_IsOne() =>
        AssertOracle.EqualAsync(@"printf '\xe9' | wc -c");

    [SkippableFact] public Task Printf_InvalidByte_Redirect_WritesOneByteFile() =>
        EqualAsync("", @"printf '\xe9' > f");

    [SkippableFact] public Task Printf_InvalidBytes_Base64() =>
        AssertOracle.EqualAsync(@"printf '\xff\xfe' | base64");

    [SkippableFact] public Task Printf_InvalidByte_Md5sum() =>
        AssertOracle.EqualAsync(@"printf '\xe9' | md5sum");

    [SkippableFact] public Task Printf_InvalidByte_Sha256sum() =>
        AssertOracle.EqualAsync(@"printf '\xe9' | sha256sum");

    [SkippableFact] public Task Printf_ValidUtf8_Euro_IsThreeBytes() =>
        AssertOracle.EqualAsync(@"printf '\xe2\x82\xac' | wc -c");

    [SkippableFact] public Task Printf_ValidUtf8_Euro_IsOneCharacter() =>
        AssertOracle.EqualAsync(@"printf '\xe2\x82\xac' | wc -m");

    [SkippableFact] public Task AnsiCQuoting_InvalidByte_IsOneByte() =>
        AssertOracle.EqualAsync(@"printf '%s' $'\xe9' | wc -c");

    [SkippableFact] public Task EchoE_InvalidByte_IsOneByteAndNewline() =>
        AssertOracle.EqualAsync(@"echo -e '\xe9' | wc -c");

    // REGRESSION (Linux): skipped there as a platform limit; the scalar store now keeps the exact value (ProcessScalars).
    [SkippableFact] public Task CommandSubstitution_KeepsTheInvalidByte() =>
        AssertOracle.EqualAsync(@"x=$(printf '\351'); printf '%s' ""$x"" | wc -c");

    // ───────────── a scalar keeps its invalid bytes, however it got them ─────────────

    /// <summary>The bytes of stdin as hex, after a label: ASCII output the canonicalizer cannot blur.</summary>
    private const string Hex = "x() { printf '%s:' \"$1\"; od -An -tx1 | tr -s ' \\n' ' '; echo; }\n";

    // REGRESSION (Linux): every scalar went through the process environment, which turned the marker into U+FFFD
    // (`ef bf bd` for `ff`). Primitive: PsBash.Core.ProcessScalars; the shell's Env: drive reads through it.
    [SkippableFact] public Task Scalars_KeepTheInvalidByte() => AssertOracle.EqualAsync(Hex + """
        s=$'\xff'
        printf '%s' "$s" | x var
        t=$s; printf '%s' "$t" | x copy
        u="a${s}b"; printf '%s' "$u" | x interp
        c=$(printf '\xff'); printf '%s' "$c" | x cmdsub
        c2="$(printf 'a\xfeb')"; printf '%s' "$c2" | x cmdsubq
        printf '\xfd\n' > f; read -r r2 < f; printf '%s' "$r2" | x readfile
        printf '\xfd\n' | { read -r r3; printf '%s' "$r3" | x readpipe; }
        read -r r4 <<< "$s"; printf '%s' "$r4" | x readhs
        a=($'\xff' x); a+=("$s"); printf '%s' "${a[0]}${a[2]}" | x array
        declare -A m; m[k]=$s; printf '%s' "${m[k]}" | x assoc
        cat <<< "$s" | x herestring
        cat <<EOF | x heredoc
        $s
        EOF
        f() { local l=$1; printf '%s' "$l" | x local; }; f "$s"
        printf -v pv '%s' "$s"; printf '%s' "$pv" | x printfv
        echo -n "$s" | x echo
        v=${s}z; printf '%s' "${v%z}" "${#s}" | x strip-len
        printf '%s\n' "$s" > g; od -An -tx1 g | tr -s ' \n' ' '; echo ":redirect"
        printf '%q\n' "$s" "a${s}b"
        { t=$s; printf '%s' "$t" | x job; } & wait
        ( s=$'\xfe' ); printf '%s' "$s" | x subshell
        """ + "\n");

    // REGRESSION (Linux): PowerShell's launcher copies the process environment and marshals argv with plain UTF-8, so a
    // program got U+FFFD. While a scalar holds such a value the shell starts programs itself (PsBash.Core.NativeRunner).
    // The programs are named by path: `sh` / `bash` are ps-bash itself (Export_ToANestedShell_KeepsTheInvalidByte).
    // Windows has no byte environment or argv: a child gets UTF-16, where an invalid byte has no form.
    [SkippableFact] public Task Export_ToAProgram_KeepsTheInvalidByte()
    {
        Skip.If(OperatingSystem.IsWindows(), "a Windows child gets its environment and argv as UTF-16: an invalid byte has no UTF-16 form");
        return AssertOracle.EqualAsync(Hex + """
            s=$'\xff'
            export E=$s; /usr/bin/printenv E | x export
            E2=$'\xfc' /usr/bin/printenv E2 | x prefix
            /usr/bin/printf '%s' "$s" | x argv
            /usr/bin/printf '%s' "$s" 2>&1 | x merged
            { /usr/bin/printf '%s' "$s" | x job; } & wait
            s=$'\xfe' /usr/bin/true; printf '%s' "$s" | x prefix-restore
            """ + "\n");
    }

    // REGRESSION (Linux): while an exported scalar was kept, a program was a stand-in function, and the check "is this
    // name a program?" behind a pipe into it saw a function: the pipe's text went in as lines (`printf ab | od -c` read
    // `a b \n`, `wc -l` counted 3). Primitive: PsBash.Cmdlets.ProgramLookup (guard ProgramLookupGuardTests).
    [SkippableFact] public Task Pipe_IntoAProgram_WhileAnExportedScalarIsKept()
    {
        Skip.If(OperatingSystem.IsWindows(), "the programs are named by their Unix path");
        return AssertOracle.EqualAsync("""
            t=$'\xfe'
            export s=$'\xff'
            printf 'a\nb\n' | /usr/bin/wc -l
            printf ab | /usr/bin/od -An -c
            { printf 'a\nb\n' | /usr/bin/wc -l; } & wait
            { printf ab | /usr/bin/od -An -c; } & wait
            """ + "\n");
    }

    // REGRESSION (Linux): the same check kept a compound command's stdin from a program inside it while an exported
    // scalar was kept: the program read the shell's own stdin instead.
    [SkippableFact] public Task StdinScope_ReachesAProgram_WhileAnExportedScalarIsKept()
    {
        Skip.If(OperatingSystem.IsWindows(), "the programs are named by their Unix path");
        return AssertOracle.EqualAsync("""
            export s=$'\xff'
            printf 'x\n' | { /usr/bin/cat; }
            { read -r l; echo "$l"; /usr/bin/cat; } <<< $'z\nw'
            printf 'p\nq\n' > f
            while read -r l; do echo "$l"; /usr/bin/cat; done < f
            """ + "\n");
    }

    // The switch to the shell's own launcher turns off when no exported scalar is kept any more: programs still get
    // their stdin and argv after each way the value goes.
    [SkippableFact] public Task Programs_AfterTheKeptScalarGoes_GetTheirInput()
    {
        Skip.If(OperatingSystem.IsWindows(), "the programs are named by their Unix path");
        return AssertOracle.EqualAsync("""
            export s=$'\xff'; unset s
            printf ab | /usr/bin/od -An -c
            export s=$'\xff'; s=ok
            printf 'a\nb\n' | /usr/bin/wc -l; /usr/bin/printenv s
            f() { local -x l=$'\xfd'; /usr/bin/printenv l | od -An -tx1; }; f
            printf cd | /usr/bin/od -An -c
            ( export z=$'\xfc' ); printf ef | /usr/bin/od -An -c
            export n=$'\xfb'; export -n n; printf gh | /usr/bin/od -An -c
            """ + "\n");
    }

    // REGRESSION (Linux): `sh` / `bash` start ps-bash itself, and .NET decodes a process's argv and environment with
    // UTF-8 before any code runs; the launcher now takes such a value from /proc/self (PsBash.Core.Platform.LaunchBytes).
    // macOS has no /proc: a known gap there.
    [SkippableFact] public Task Export_ToANestedShell_KeepsTheInvalidByte()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the exact argv / environment of a process is read from /proc/self (Linux only; Windows has UTF-16)");
        return AssertOracle.EqualAsync(Hex + """
            s=$'\xff'
            export E=$s; sh -c 'printf %s "$E"' | x export
            E2=$'\xfc' bash -c 'printf %s "$E2"' | x prefix
            sh -c 'printf %s "$1"' _ "$s" | x argv
            bash -c 'printf %s "$0"' $'\xfb' | x name
            """ + "\n");
    }

    [SkippableFact] public Task PrintfAppendRedirect_InvalidBytes() =>
        EqualAsync("", @"printf '\xe9' >> f; printf '\xe9\x80' >> f");

    // ───────────── files round-trip byte-identically ─────────────

    [SkippableFact] public Task Cat_EveryByte_ToRedirect() =>
        EqualAsync(MkBin("bin", AllBytes), "cat bin > copy");

    [SkippableFact] public Task Cat_RandomBinary_ToRedirect() =>
        EqualAsync(MkBin("bin", Random1k), "cat bin > copy");

    [SkippableFact] public Task Cat_TwoBinaryFiles_Concatenate() =>
        EqualAsync(MkBin("a", AllBytes) + "\n" + MkBin("b", Random1k), "cat a b > c");

    [SkippableFact] public Task HeadBytes_OfBinary() =>
        EqualAsync(MkBin("bin", Random1k), "head -c 100 bin > h");

    [SkippableFact] public Task TailBytes_OfBinary() =>
        EqualAsync(MkBin("bin", Random1k), "tail -c 10 bin > t");

    [SkippableFact] public Task HeadAndTailBytes_OnAPipe() =>
        EqualAsync(MkBin("bin", Random1k), "cat bin | head -c 100 > h; cat bin | tail -c 10 > t");

    [SkippableFact] public Task Tee_OfBinary() =>
        EqualAsync(MkBin("bin", Random1k), "cat bin | tee t > /dev/null");

    [SkippableFact] public Task Gzip_StdoutThenDecompress_RoundTrips() =>
        EqualAsync(MkBin("bin", Random1k), "gzip -c bin | gzip -dc > out");

    [SkippableFact] public Task Gzip_File_ThenDecompress_RoundTrips() =>
        EqualAsync(MkBin("bin", Random1k), "gzip -k bin; gzip -dc bin.gz > out; rm bin.gz");

    [SkippableFact] public Task Base64_EncodeDecode_RoundTrips() =>
        EqualAsync(MkBin("bin", Random1k), "base64 bin | base64 -d > out");

    [SkippableFact] public Task Base64_Decode_KeepsTheFinalNewlineByte() =>
        AssertOracle.EqualAsync(@"printf 'a\n' | base64 | base64 -d | wc -c; printf 'a' | base64 | base64 -d | wc -c; printf 'a\n\n' | base64 | base64 -d | wc -c");

    [SkippableFact] public Task Base64_Wrapped_UsesLfBetweenLines() =>
        AssertOracle.EqualAsync(MkBin("bin", Random1k) + "; base64 bin | wc -c; base64 -w 20 bin | wc -c; rm bin");

    [SkippableFact] public Task Base64_OfBinaryFile() =>
        AssertOracle.EqualAsync(MkBin("bin", AllBytes) + "; base64 bin; rm bin");

    [SkippableFact] public Task Checksums_OfBinaryFile() =>
        AssertOracle.EqualAsync(MkBin("bin", Random1k) + "; md5sum bin; sha1sum bin; sha256sum bin; rm bin");

    [SkippableFact] public Task WcC_OfBinaryFile_AndOfItsStream() =>
        AssertOracle.EqualAsync(MkBin("bin", Random1k) + "; wc -c < bin; cat bin | wc -c; rm bin");

    [SkippableFact] public Task StdinRedirect_FeedsExactBytes() =>
        AssertOracle.EqualAsync(MkBin("bin", Random1k) + "; md5sum < bin; base64 < bin | md5sum; rm bin");

    [SkippableFact] public Task Split_BinaryByBytes() =>
        EqualAsync(MkBin("bin", Random1k), "split -b 300 bin p");

    // ───────────── counters, tr, cut ─────────────

    [SkippableFact] public Task Wc_Mwlc_OnInvalidByteWithText() =>
        AssertOracle.EqualAsync(@"printf 'a\xffb c' | wc -w -m -c -L | awk '{print $1, $2, $3, $4}'");

    [SkippableFact] public Task Wc_Mwlc_OnLoneInvalidByteAndNewline() =>
        AssertOracle.EqualAsync(@"printf '\xe9\n' | wc -l -w -m -c -L | awk '{print $1, $2, $3, $4, $5}'");

    [SkippableFact] public Task Wc_M_OnOnlyInvalidBytes() =>
        AssertOracle.EqualAsync(@"printf '\xff\xfe' | wc -m");

    [SkippableFact] public Task Wc_Mc_OnAccentsEmojiCjk() =>
        AssertOracle.EqualAsync(@"printf 'caf\xc3\xa9 \xf0\x9f\x98\x80 \xe6\x97\xa5\n' | wc -m -c | awk '{print $1, $2}'");

    [SkippableFact] public Task Tr_OctalSet_MatchesTheByte() =>
        AssertOracle.EqualAsync(@"printf 'a\351b' | tr '\351' x");

    [SkippableFact] public Task Tr_Delete_InvalidByte() =>
        AssertOracle.EqualAsync(@"printf 'a\351b' | tr -d '\351' | wc -c");

    [SkippableFact] public Task Cut_Bytes_SelectsTheInvalidByte() =>
        AssertOracle.EqualAsync(@"printf 'a\351b\n' | cut -b2 | wc -c");

    [SkippableFact] public Task Cut_Bytes_ExcludesTheInvalidByte() =>
        AssertOracle.EqualAsync(@"printf 'a\351b\n' | cut -b1,3");

    // ───────────── the fused lane and process substitution ─────────────

    [SkippableFact] public Task FusedLane_InvalidBytes_PassThroughCatHeadWc() =>
        AssertOracle.EqualAsync(@"printf 'a\351\nb\377\n' | cat | head -n 1 | wc -c");

    [SkippableFact] public Task FusedLane_Tac_Rev_Keep_TheBytes() =>
        EqualAsync("", @"printf 'x\351\ny\n' | tac | cat > out");

    [SkippableFact] public Task ProcessSubstitution_CarriesInvalidBytes() =>
        AssertOracle.EqualAsync(@"cat <(printf '\351') | wc -c");

    [SkippableFact] public Task Sort_OfInvalidBytes_KeepsEveryByte() =>
        EqualAsync("", @"printf 'b\351\na\377\n' | sort | wc -c > n");
}
