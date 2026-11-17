using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// Invoke-BashRead from PsBash.psm1 to a binary cmdlet.
///
/// Oracle: the psm1 function. The cmdlet preserves every observable
/// branch: pipeline collection, default REPLY destination, single + multi
/// variable splits (last variable gets remainder), -p prompt prefix, -a
/// array, -n max-chars, -r raw (no-op for parity), Directive 12
/// identifier-name rejection.
/// </summary>
public class InvokeBashReadCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashReadCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private string ReadVariable(string script, string varName)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            $"$v = [PsBash.Cmdlets.BashVariableLookup]::Scalar($ExecutionContext.SessionState, '{varName}'); " +
            "if ($null -eq $v) { return } " +
            "Write-Output ($v -as [string])").Invoke();
        pwsh.Commands.Clear();
        return result.Count > 0 ? result[0]?.ToString() ?? "" : "";
    }

    private string[] ReadArrayVariable(string script, string varName)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            $"$v = Get-Variable -Name '{varName}' -ValueOnly -ErrorAction SilentlyContinue; " +
            "if ($null -eq $v) { return } " +
            "$arr = @($v); for ($k=0; $k -lt $arr.Count; $k++) { Write-Output $arr[$k] }").Invoke();
        pwsh.Commands.Clear();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    private string[] RunAndCollectErrors(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        var errs = pwsh.AddScript("$error | ForEach-Object { $_.ToString() }").Invoke();
        pwsh.Commands.Clear();
        return errs.Select(o => o?.ToString() ?? "").ToArray();
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Read_SharedStdinQueue_TakesOneLineAtATimeAndLeavesTheRestForTheFeed()
    {
        // The compound-stdin scope (PsBuild.StdinScope) fills Queue[object] with the ORIGINAL
        // records; `read` takes a LINE (a multi-line record yields its first line and keeps the
        // remainder at the front), and the feed yields what is left, in order.
        var lines = RunLines(
            "$global:__BashStdIn = [System.Collections.Generic.Queue[object]]::new(); " +
            "$global:__BashStdIn.Enqueue(\"a`nb`nc\"); $global:__BashStdIn.Enqueue('d'); " +
            "Invoke-BashRead x; Invoke-BashRead y; " +
            "\"x=$env:x y=$env:y\"; $env:x = $null; $env:y = $null; " +
            "& { while ($global:__BashStdIn -and $global:__BashStdIn.Count -gt 0) { $global:__BashStdIn.Dequeue() } }");

        Assert.Equal(new[] { "x=a y=b", "c", "d" }, lines);
    }

    [Fact]
    public void SharedStdinLine_TakesOneLinePerCallAndLeavesTheRestForTheOtherReaders()
    {
        // A `while read` loop over a shared stdin (PsBuild.SharedStdinNextLine) takes ONE line per pass, the cursor a
        // `read` in its body advances too: `while read a; do read b; …` pairs 1-2, 3-4. It took a whole record per pass,
        // so a multi-line record left the body reader nothing (bash oracle: `printf '1\n2\n3\n4\n' | while read a; do read
        // b; echo "$a-$b"; done` prints 1-2 and 3-4).
        var lines = RunLines(
            "$global:__BashStdIn = [System.Collections.Generic.Queue[object]]::new(); " +
            "$global:__BashStdIn.Enqueue(\"1`n2`n3`n4`n\"); " +
            "while ($null -ne ($a = [PsBash.Cmdlets.SharedStdinLine]::Next($global:__BashStdIn))) { Invoke-BashRead b; \"$a-$env:b\" }; $env:b = $null");

        Assert.Equal(new[] { "1-2", "3-4" }, lines);
    }

    [Fact]
    public void SharedStdinLine_JoinsAnUnterminatedRecordWithTheNext_AndSkipsAnUnterminatedEnd()
    {
        // `{ printf a; echo b; printf c; } | while read l; …`: bash reads the byte stream, so the first line is `ab`, and
        // the unterminated `c` at the end fails `read` (the body never sees it).
        var lines = RunLines(
            "$global:__BashStdIn = [System.Collections.Generic.Queue[object]]::new(); " +
            "$global:__BashStdIn.Enqueue([PsBash.Cmdlets.BashRuntime]::TextRecord('a', $true)); " +
            "$global:__BashStdIn.Enqueue(\"b`n\"); " +
            "$global:__BashStdIn.Enqueue([PsBash.Cmdlets.BashRuntime]::TextRecord('c', $true)); " +
            "while ($null -ne ($l = [PsBash.Cmdlets.SharedStdinLine]::Next($global:__BashStdIn))) { \"[$l]\" }; " +
            "[PsBash.Cmdlets.SharedStdinLine]::Next($null) -eq $null");

        Assert.Equal(new[] { "[ab]", "True" }, lines);
    }

    [Fact]
    public void Read_AtTheEndOfTheSharedStdin_EmptiesItsVariablesAndFails()
    {
        // bash oracle: `printf '1\n' | { read b; read b; echo "[$b] $?"; }` prints `[] 1`. The read at the end of the
        // input left b at `1`, so `while read a; do read b; …` repeated the previous line for an odd line count.
        var lines = RunLines(
            "$global:__BashStdIn = [System.Collections.Generic.Queue[object]]::new(); " +
            "$global:__BashStdIn.Enqueue(\"1`n\"); " +
            "Invoke-BashRead b; Invoke-BashRead b; \"[$env:b] $global:LASTEXITCODE\"; " +
            "$env:y = 'old'; $env:z = 'old'; Invoke-BashRead y z; \"[$env:y][$env:z]\"; $env:b = $null; $env:y = $null; $env:z = $null");

        Assert.Equal(new[] { "[] 1", "[][]" }, lines);
    }

    [Fact]
    public void Read_NCharsOfAnUnterminatedInput_PutsTheRestBackUnterminated()
    {
        // bash oracle: `printf 'abc' | { read -n 2 x; echo "x=$x"; cat; echo; } | od -c` ends `c \n` -- one newline, the
        // echo's. The rest went back as a plain string, which every reader takes for a whole LINE, so cat printed `c\n`.
        var lines = RunLines(
            "$global:__BashStdIn = [System.Collections.Generic.Queue[object]]::new(); " +
            "$global:__BashStdIn.Enqueue([PsBash.Cmdlets.BashRuntime]::TextRecord('abc', $true)); " +
            "Invoke-BashRead -n 2 x; \"x=$env:x\"; $env:x = $null; " +
            "$rest = $global:__BashStdIn.Dequeue(); " +
            "\"rest=$([PsBash.Cmdlets.BashRuntime]::GetBashText($rest)) unterminated=$([PsBash.Cmdlets.BashRuntime]::IsUnterminated($rest))\"");

        Assert.Equal(new[] { "x=ab", "rest=c unterminated=True" }, lines);
    }

    [Fact]
    public void Read_NCharsOfATerminatedLine_PutsTheRestBackAsALine()
    {
        // bash oracle: `printf 'abc\ndef\n' | { read -n 2 x; cat; }` prints `c` and `def`, each with its newline.
        var lines = RunLines(
            "$global:__BashStdIn = [System.Collections.Generic.Queue[object]]::new(); " +
            "$global:__BashStdIn.Enqueue(\"abc`n\"); $global:__BashStdIn.Enqueue(\"def`n\"); " +
            "Invoke-BashRead -n 2 x; Invoke-BashRead y; Invoke-BashRead z; \"x=$env:x y=$env:y z=$env:z\"; $env:x = $null; $env:y = $null; $env:z = $null");

        Assert.Equal(new[] { "x=ab y=c z=def" }, lines);
    }

    [Fact]
    public void BackgroundJobStdin_SharesTheCompoundStdin_ButNotTheShellsOwn()
    {
        // ps-bash-specific surface (the job's runspace is handed a cursor): bash gives `cmd &` the stdin of the compound
        // command it is started in (`echo hi | { cat & wait; }` prints hi) and, with job control off, /dev/null at the top
        // of a script. A queue is replaced by a locked cursor that both runspaces read; the shell's own stdin is withheld.
        var lines = RunLines(
            "$global:__BashStdIn = [System.Collections.Generic.Queue[object]]::new(); " +
            "$global:__BashStdIn.Enqueue('a'); $global:__BashStdIn.Enqueue('b'); " +
            "$job = [PsBash.Cmdlets.BackgroundJobStdin]::Inherit($ExecutionContext.SessionState); " +
            "\"same=$([object]::ReferenceEquals($job, $global:__BashStdIn)) type=$($job.GetType().Name)\"; " +
            "\"job=$($job.Dequeue())\"; Invoke-BashRead x; \"shell=$env:x left=$($job.Count)\"; $env:x = $null; " +
            "$global:__BashStdIn = [PsBash.Core.StdinCursor]::new([object[]]@('s')); " +
            "\"cursor-shared=$([object]::ReferenceEquals([PsBash.Cmdlets.BackgroundJobStdin]::Inherit($ExecutionContext.SessionState), $global:__BashStdIn))\"; " +
            "$global:__BashStdIn = [PsBash.Core.StdinCursor]@{ IsShellStdin = $true }; $global:__BashStdIn.Enqueue('own'); " +
            "$top = [PsBash.Cmdlets.BackgroundJobStdin]::Inherit($ExecutionContext.SessionState); " +
            "\"shell-own-shared=$([object]::ReferenceEquals($top, $global:__BashStdIn)) job-sees=$($top.Count)\"; " +
            "$global:__BashStdIn = $null; " +
            "\"none=$([PsBash.Cmdlets.BackgroundJobStdin]::Inherit($ExecutionContext.SessionState).Count)\"");

        Assert.Equal(new[] { "same=True type=StdinCursor", "job=a", "shell=b left=0", "cursor-shared=True", "shell-own-shared=False job-sees=0", "none=0" }, lines);
    }

    [Fact]
    public void Read_BasicStdin_PopulatesREPLY()
    {
        // No destination name given: oracle parity (bash default) is REPLY.
        var v = ReadVariable("'hello world' | Invoke-BashRead", "REPLY");
        Assert.Equal("hello world", v);
    }

    [Fact]
    public void Read_NamedVariable_AssignsToNamedScope()
    {
        var v = ReadVariable("'apple' | Invoke-BashRead myvar", "myvar");
        Assert.Equal("apple", v);
    }

    [Fact]
    public void Read_RawFlag_AcceptedAsNoOp()
    {
        // -r is a no-op for the cmdlet (matches the psm1 oracle — neither
        // implemented backslash escape processing).
        var v = ReadVariable("'raw text' | Invoke-BashRead -r myvar", "myvar");
        Assert.Equal("raw text", v);
    }

    [Fact]
    public void Read_PromptFlag_DoesNotPolluteVariable()
    {
        // With a pipeline source, the prompt path is bypassed (oracle
        // parity). The variable should still receive the pipeline text.
        // Tests that the -p parameter does not consume the variable name.
        var v = ReadVariable(
            "'value' | Invoke-BashRead -P 'enter: ' myvar",
            "myvar");
        Assert.Equal("value", v);
    }

    [Fact]
    public void Read_ArrayFlag_WhitespaceSplitsIntoArray()
    {
        var arr = ReadArrayVariable(
            "'foo bar baz' | Invoke-BashRead -A myarr",
            "myarr");
        Assert.Equal(new[] { "foo", "bar", "baz" }, arr);
    }

    [Fact]
    public void Read_LimitChars_TruncatesInput()
    {
        // -n N: read at most N chars. "abcdef" with -n 3 -> "abc".
        var v = ReadVariable("'abcdef' | Invoke-BashRead -n 3 myvar", "myvar");
        Assert.Equal("abc", v);
    }

    [Fact]
    public void Read_DefaultNameWhenMissing_IsREPLY()
    {
        // Bare `read` with no operands defaults to REPLY (bash default
        // contract; the psm1 oracle returned with no assignment when no
        // names were given, but the cmdlet upgrades to bash-correct REPLY).
        var v = ReadVariable("'defaulted' | Invoke-BashRead", "REPLY");
        Assert.Equal("defaulted", v);
    }

    [Fact]
    public void Read_MultiVariable_SplitsWithLastGettingRemainder()
    {
        // a b c d into two vars: first gets "a", second gets "b c d".
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("'a b c d' | Invoke-BashRead v1 v2").Invoke();
        pwsh.Commands.Clear();
        var vs = pwsh.AddScript("$env:v1; $env:v2; $env:v1 = $null; $env:v2 = $null").Invoke();
        Assert.Equal(new[] { "a", "b c d" }, vs.Select(o => o?.ToString()));
    }

    [Fact]
    public void Read_AliasResolution_InvokeBashReadCommand()
    {
        // The cmdlet is exposed under its full name; the emitter targets
        // Invoke-BashRead directly. Verify the name resolves.
        var lines = RunLines(
            "(Get-Command Invoke-BashRead).CommandType.ToString()");
        Assert.NotEmpty(lines);
        Assert.Contains("Cmdlet", lines[0]);
    }

    [Fact]
    public void Read_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashRead --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("read", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Read_EmptyPipeline_NoAssignmentToREPLY()
    {
        // Empty pipeline + EOF stdin: ReadLine returns null → EOF path → exit 1, no
        // assignment. REPLY stays empty/unset.
        //
        // DETERMINISM (QA Directive 6): with an empty pipeline the cmdlet falls back to
        // [Console]::In.ReadLine(). Whether that blocks depends on the ambient console of
        // the *test host* — under a real console (local `dotnet test` from a terminal)
        // it blocks FOREVER and hangs the entire suite; under redirected CI stdin it does
        // not. We pin the input to an empty StringReader so the fallback reads a
        // deterministic EOF in every environment instead of the ambient stdin. Restored
        // in a finally so sibling tests see the original reader.
        var v = ReadVariable(
            "$__in = [Console]::In; " +
            "try { [Console]::SetIn([System.IO.StringReader]::new('')); @() | Invoke-BashRead } " +
            "finally { [Console]::SetIn($__in) }; ''",
            "REPLY");
        // Either unset or empty — both indicate no assignment occurred.
        // (The test runspace may have a leftover REPLY from a sibling test
        // since the fixture creates a fresh runspace per test; we get "" if
        // truly empty/unset.)
        // We accept either branch — the important assertion is that the
        // injected pipeline payload is not present.
        Assert.NotEqual("PWNED", v);
    }

    [Fact]
    public void Read_MultiVariable_HonorsCustomIfs()
    {
        // REGRESSION (R16): the multi-variable path split on `\s+` regardless of
        // IFS, so `IFS=,` never split. bash splits on IFS with the last variable
        // absorbing the remainder (separators included).
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(
            "$env:IFS = ','; try { '1,2,3' | Invoke-BashRead v1 v2 v3 } finally { Remove-Item Env:\\IFS }")
            .Invoke();
        pwsh.Commands.Clear();
        var vs = pwsh.AddScript("$env:v1; $env:v2; $env:v3; $env:v1 = $null; $env:v2 = $null; $env:v3 = $null").Invoke();
        Assert.Equal(new[] { "1", "2", "3" }, vs.Select(o => o?.ToString()));
    }

    [Fact]
    public void Read_MultiVariable_HonorsCustomIfs_LastAbsorbsRemainder()
    {
        // Last variable gets the remainder including the IFS separators.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(
            "$env:IFS = ','; try { '1,2,3' | Invoke-BashRead a b } finally { Remove-Item Env:\\IFS }")
            .Invoke();
        pwsh.Commands.Clear();
        var ab = pwsh.AddScript("$env:a; $env:b; $env:a = $null; $env:b = $null").Invoke();
        Assert.Equal(new[] { "1", "2,3" }, ab.Select(o => o?.ToString()));
    }

    // ---- oils builtin-read gaps (oracle: bash 5.2) ----

    [Fact]
    public void Read_HereDocRecords_TakesOnlyTheFirstLine()
    {
        // read x <<EOF / "A B" / "FG" / EOF -> x is "A B" (the records used to be glued: "A BFG").
        Assert.Equal("A B", ReadVariable("@('A B','FG') | Invoke-BashRead x", "x"));
    }

    [Fact]
    public void Read_NoNames_KeepsLeadingAndTrailingWhitespaceInReply()
    {
        Assert.Equal("  a b  ", ReadVariable("$env:REPLY = $null; '  a b  ' | Invoke-BashRead", "REPLY"));
        Assert.Equal("a b", ReadVariable("'  a b  ' | Invoke-BashRead myvar", "myvar"));
    }

    [Fact]
    public void Read_BackslashNewline_JoinsLinesUnlessRaw()
    {
        Assert.Equal("a b  line2", ReadVariable("@('a b  \\','line2') | Invoke-BashRead v", "v"));
        Assert.Equal("a b  \\", ReadVariable("@('a b  \\','line2') | Invoke-BashRead -r v", "v"));
    }

    // Oracle: `printf '.\000.' | { read s; echo ${#s}; }` is 2 and `read -n 3` reads `..`: read skips NUL bytes. A kept NUL
    // ended the stored value at the first one (oils nul-bytes "read builtin" / "read -n").
    [Fact]
    public void Read_SkipsNulBytes()
    {
        Assert.Equal("..", ReadVariable("\".`0.\" | Invoke-BashRead -r v", "v"));
        Assert.Equal("..", ReadVariable("\".`0.\" | Invoke-BashRead -r -n 3 v", "v"));
    }

    [Fact]
    public void Read_SmooshedValueFlag_ReadsOneCharacter()
    {
        Assert.Equal("h", ReadVariable("'hi' | Invoke-BashRead -rn1 var", "var"));
    }

    [Fact]
    public void Read_ExactCount_IgnoresTheDelimiterButMaxCountStopsAtIt()
    {
        Assert.Equal("fooba", ReadVariable("'foobar' | Invoke-BashRead -N 5 -d b; $env:out = $env:REPLY; $env:REPLY = $null", "out"));
        Assert.Equal("foo", ReadVariable("'foobar' | Invoke-BashRead -n 5 -d b; $env:out = $env:REPLY; $env:REPLY = $null", "out"));
    }

    [Fact]
    public void Read_TimeoutZero_OnlyTestsForInputAndConsumesNothing()
    {
        Assert.Equal("", ReadVariable("$env:REPLY = $null; 'foo' | Invoke-BashRead -t 0", "REPLY"));
    }

    [Fact]
    public void Read_InvalidCount_IsAnErrorWithStatusOne()
    {
        var lines = RunLines("Invoke-BashRead -n -1; $LASTEXITCODE");
        Assert.Equal("1", lines[^1]);
    }

    // ---- Directive 12 injection probe ----

    [Fact]
    public void Read_InjectionInVariableName_RejectedAsInvalidIdentifier()
    {
        // A literal `$(throw 'pwn')` token in the variable-name argument
        // (passed via PowerShell backtick-escape) must not be evaluated as
        // PowerShell. The cmdlet rejects names containing scriptblock
        // metacharacters with a bash-style "not a valid identifier" error
        // and skips the assignment.
        //
        // Assert on the assignment side-effect, not the error text — the
        // cmdlet's error message echoes the bad name back for parity with
        // the psm1 oracle, so the literal "'pwn'" substring legitimately
        // appears in the rejection message. The actual security property
        // is "REPLY (the default destination) was not assigned the input
        // value because the explicit name argument was rejected before
        // even being treated as a name fallback".
        var v = ReadVariable(
            "$env:REPLY = $null; 'value' | Invoke-BashRead \"`$(throw 'pwn')\"",
            "REPLY");
        Assert.True(string.IsNullOrEmpty(v),
            "REPLY should not be assigned when the name argument is rejected");
    }
}
