using System.Diagnostics;
using System.Linq;
using Xunit;

namespace PsBash.Shell.Tests;

[Trait("Category", "Integration")]
public class AgentPatternEndToEndTests
{
    private static readonly string IpcEndpoint = PsBashTestProcess.CreateEndpoint();

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunShellAsync(
        params string[] arguments)
        => RunShellAsync(arguments, timeout: null, env: null, workingDirectory: null);

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunShellAsync(
        string[] arguments,
        TimeSpan? timeout)
        => RunShellAsync(arguments, timeout, env: null, workingDirectory: null);

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunShellAsync(
        string[] arguments,
        TimeSpan? timeout,
        IReadOnlyDictionary<string, string?>? env,
        string? workingDirectory = null)
    {
        var psi = env is null
            ? PsBashTestProcess.Create(arguments, workingDirectory, env, ipcEndpoint: IpcEndpoint)
            : PsBashTestProcess.Create(arguments, workingDirectory, env);
        return ProcessRunHelper.RunAsync(psi, stdinContent: null, timeout: timeout);
    }

    [SkippableFact]
    public async Task SedRangeAddress_UnquotedComma_IsOneArgumentNotArray()
    {
        // `725,750p` is one word in bash; unquoted it was a PowerShell array and the
        // cmdlet binder failed ("Cannot convert 'System.Object[]' ... 'Arguments'").
        var tempDir = Path.Combine(Path.GetTempPath(), "ps-bash-comma-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "f"), "l1\nl2\nl3\nl4\n");
            var (exitCode, stdout, stderr) = await RunShellAsync(
                ["-c", "sed -n 2,3p f; echo a,b; x=q; echo $x,y; echo {a,b}"],
                timeout: null,
                env: null,
                workingDirectory: tempDir);

            Assert.Equal("", stderr.Trim());
            Assert.Equal(0, exitCode);
            Assert.Equal(new[] { "l2", "l3", "a,b", "q,y", "a b" },
                stdout.Replace("\r", "").TrimEnd('\n').Split('\n'));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task FailedRedirect_InsideStdinScopedEval_FailsOnlyThatCommand()
    {
        // bash oracle: `eval 'echo hi > /nodir/x; echo "rc=$?"' < /dev/null; echo tail` prints
        // the error, then rc=1 and tail. The Claude Code Bash tool wraps every command exactly so
        // (`eval '…' < /dev/null`): the stdin scope emits a `try { }`, and Invoke-BashRedirect's
        // terminating error aborted the rest of the block — every later statement vanished.
        var missing = "/psb_nodir_" + Guid.NewGuid().ToString("N")[..8] + "/x";
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c",
            $"eval 'echo hi > {missing}; echo \"rc=$?\"' < /dev/null; {{ echo a; }} > {missing}; echo \"grp rc=$?\"; echo tail");

        Assert.Contains("No such file or directory", stderr);
        Assert.Equal(new[] { "rc=1", "grp rc=1", "tail" },
            stdout.Replace("\r", "").TrimEnd('\n').Split('\n'));
        Assert.Equal(0, exitCode);
    }

    [SkippableFact]
    public async Task FailedRedirect_InsideStdinScopedEval_CommandNeverRuns()
    {
        // bash oracle: a redirect that cannot be opened means the command is never executed —
        // `{ touch m; } > /nodir/x` and `touch m > /nodir/x` both leave m absent. Running it anyway
        // would let `rm -rf build > /nodir/log` delete while "failing".
        var missing = "/psb_nodir_" + Guid.NewGuid().ToString("N")[..8] + "/x";
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash", "redir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var d = dir.Replace('\\', '/');
            var (_, stdout, _) = await RunShellAsync(
                "-c",
                $"eval '{{ touch {d}/grp; }} > {missing}; touch {d}/plain > {missing}; echo \"rc=$?\"' < /dev/null; echo tail");

            Assert.Equal(new[] { "rc=1", "tail" }, stdout.Replace("\r", "").TrimEnd('\n').Split('\n'));
            Assert.False(File.Exists(Path.Combine(dir, "grp")), "brace group ran despite its failed redirect");
            Assert.False(File.Exists(Path.Combine(dir, "plain")), "touch ran despite its failed redirect");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // The real terminal stderr bytes (the differential oracle trims, so it cannot see a stray final
    // newline). bash writes exactly what the command wrote: `printf x >&2` is the one byte `x`. Every
    // stderr record used to get a newline from the host's frame convention. Oracle: bash 5.2.
    [SkippableTheory]
    [InlineData("printf x >&2", "x")]
    [InlineData("printf 'a\\nb' >&2", "a\nb")]
    [InlineData("echo -n y >&2", "y")]
    [InlineData("echo line >&2", "line\n")]
    [InlineData("printf x >&2; echo y >&2", "xy\n")]
    public async Task StderrRecord_ExactBytesOnTheTerminal(string script, string expectedStderr)
    {
        var (exitCode, _, stderr) = await RunShellAsync("-c", script);
        Assert.Equal(0, exitCode);
        Assert.Equal(expectedStderr, stderr);
    }

    [SkippableFact]
    public async Task StderrRecord_CmdletDiagnostic_StillEndsInNewline()
    {
        var (_, _, stderr) = await RunShellAsync("-c", "cat /psb-nonexistent-zz; printf after >&2");
        Assert.EndsWith("No such file or directory\nafter", stderr);
    }

    [SkippableFact]
    public async Task FailedRedirect_UnderSetE_StillStopsTheScript()
    {
        // bash oracle: `set -e; echo hi > /nodir/x; echo after` prints only the error, exit 1.
        var missing = "/psb_nodir_" + Guid.NewGuid().ToString("N")[..8] + "/x";
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", $"set -e; echo hi > {missing}; echo after");

        Assert.Contains("No such file or directory", stderr);
        Assert.DoesNotContain("after", stdout);
        Assert.Equal(1, exitCode);
    }

    [SkippableFact]
    public async Task ExpandedUnixDriveRedirectTargets_WithUnixPaths_WriteTheFiles()
    {
        // With PSBASH_UNIX_PATHS=1 a LITERAL `> /c/...` target was rewritten, but one built by
        // expansion (`d=/c/...; echo hi > $d/f`) was not: "No such file or directory".
        Skip.IfNot(OperatingSystem.IsWindows(), "drive-letter paths are Windows-only");
        var tempDir = Path.Combine(Path.GetTempPath(), "ps-bash-unixredir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var full = Path.GetFullPath(tempDir);
        var unix = "/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
        try
        {
            var (exitCode, stdout, stderr) = await RunShellAsync(
                ["-c", $"d={unix}; echo o > $d/o; echo a >> \"$d/o\"; ls psb_nosuch 2>$d/e; "
                     + "echo b &> $d/b; cat < $d/o; echo \"rc=$?\""],
                timeout: null,
                env: new Dictionary<string, string?> { ["PSBASH_UNIX_PATHS"] = "1" });

            Assert.Equal("", stderr.Trim());
            Assert.Equal(new[] { "o", "a", "rc=0" }, stdout.Replace("\r", "").TrimEnd('\n').Split('\n'));
            Assert.Equal(0, exitCode);
            Assert.Equal("o\na\n", File.ReadAllText(Path.Combine(tempDir, "o")));
            Assert.Contains("psb_nosuch", File.ReadAllText(Path.Combine(tempDir, "e")));
            // `&>` is a native PowerShell redirect, which writes CRLF (a separate divergence from
            // bash's "b\n"); this test pins only that the file lands at the mapped drive path.
            Assert.Equal("b", File.ReadAllText(Path.Combine(tempDir, "b")).TrimEnd('\r', '\n'));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task TmpWordWithQuotedPart_NamesTheUnquotedFile()
    {
        // bash oracle: `echo c > /tmp/psb_'s p'` writes the file `psb_s p` (the quotes are
        // syntax, not name characters). The /tmp rewrite used to splice the emitted text, so the
        // name kept LITERAL single quotes and `cat "/tmp/psb_s p"` found nothing.
        var id = Guid.NewGuid().ToString("N")[..8];
        var script = $"echo c > /tmp/psb_{id}_'s p'; cat \"/tmp/psb_{id}_s p\"; "
                   + $"cat /tmp/\"psb_{id}_s p\"; ls /tmp/psb_{id}_*";
        var file = Path.Combine(Path.GetTempPath(), $"psb_{id}_s p");
        try
        {
            var (exitCode, stdout, stderr) = await RunShellAsync(
                ["-c", script], timeout: null, env: null, workingDirectory: null);

            Assert.Equal("", stderr.Trim());
            Assert.Equal(0, exitCode);
            var lines = stdout.Replace("\r", "").TrimEnd('\n').Split('\n');
            Assert.Equal("c", lines[0]);
            Assert.Equal("c", lines[1]);
            Assert.Contains($"psb_{id}_s p", lines[2]);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    [SkippableFact]
    public async Task GlobWordWithComma_MatchesLiteralCommaAndKeepsWordWhenNoMatch()
    {
        // bash oracle: the comma is literal inside the glob; `ls f*,g` lists `fx,g`, and an
        // unmatched pattern (nullglob off) reaches the command as the literal word.
        var tempDir = Path.Combine(Path.GetTempPath(), "ps-bash-globc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "fx,g"), "");
            File.WriteAllText(Path.Combine(tempDir, "a.c,x"), "A\n");
            File.WriteAllText(Path.Combine(tempDir, "b.c,x"), "B\n");
            var (exitCode, stdout, stderr) = await RunShellAsync(
                ["-c", "ls f*,g; cat *.c,x; ls nomatch*,zz"],
                timeout: null,
                env: null,
                workingDirectory: tempDir);

            var lines = stdout.Replace("\r", "").TrimEnd('\n').Split('\n');
            Assert.Equal(new[] { "fx,g", "A", "B" }, lines);
            Assert.Contains("nomatch*,zz", stderr);
            Assert.NotEqual(0, exitCode);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task NestedBash_ScriptAndDashCArgs_ArePositionalVerbatim()
    {
        // bash oracle: `bash s.sh -v -e -c x` -> $1=-v $2=-e $3=-c $4=x;
        // `bash -c 'echo "$0 $1"' zero -d` -> `zero -d`; `bash s.sh --version --help` -> both are $1 $2.
        var tempDir = Path.Combine(Path.GetTempPath(), "ps-bash-bargs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "s.sh"), "echo \"$1|$2|$3|$4\"\n");
            // The nested `bash` cmdlet forwards its argv to a child ps-bash; drive that child
            // (the launcher) directly with the same argv.
            var lines = new List<string>();
            foreach (var argv in new[]
            {
                new[] { "s.sh", "-v", "-e", "-c", "x" },
                new[] { "-c", "echo \"$1 $2\"", "zero", "-d", "-x" },
                new[] { "s.sh", "--version", "--help" },
            })
            {
                var (exitCode, stdout, stderr) = await RunShellAsync(
                    argv, timeout: null, env: null, workingDirectory: tempDir);
                Assert.Equal("", stderr.Trim());
                Assert.Equal(0, exitCode);
                lines.AddRange(stdout.Replace("\r", "").TrimEnd('\n').Split('\n'));
            }
            Assert.Equal(new[] { "-v|-e|-c|x", "-d -x", "--version|--help||" }, lines);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task DollarZero_IsTheNameOrScriptPathAsGiven()
    {
        // bash oracle (5.2): `-c 'echo "$0 $1"' zero -d` -> `zero -d`; `-c 'echo "[$0]"'` -> `[bash]`;
        // `-c ... a/b/c` -> `[a/b/c]` (NOT the basename); `bash s0.sh a` -> `[s0.sh]`, `./s0.sh` and
        // `sub/s0.sh` keep the path as typed, and a function inside the script still sees the script's $0.
        var tempDir = Path.Combine(Path.GetTempPath(), "ps-bash-dz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "sub"));
        try
        {
            const string body = "echo \"[$0] [$1]\"\nf() { echo \"in f: $0\"; }\nf\n";
            File.WriteAllText(Path.Combine(tempDir, "s0.sh"), body);
            File.WriteAllText(Path.Combine(tempDir, "sub", "s0.sh"), body);
            var cases = new (string[] Argv, string[] Expected)[]
            {
                (new[] { "-c", "echo \"$0 $1\"", "zero", "-d" }, new[] { "zero -d" }),
                (new[] { "-c", "echo \"[$0]\"" }, new[] { "[bash]" }),
                (new[] { "-c", "echo \"[$0]\"", "a/b/c" }, new[] { "[a/b/c]" }),
                (new[] { "s0.sh", "a" }, new[] { "[s0.sh] [a]", "in f: s0.sh" }),
                (new[] { "./s0.sh", "a" }, new[] { "[./s0.sh] [a]", "in f: ./s0.sh" }),
                (new[] { "sub/s0.sh", "a" }, new[] { "[sub/s0.sh] [a]", "in f: sub/s0.sh" }),
            };
            foreach (var (argv, expected) in cases)
            {
                var (exitCode, stdout, stderr) = await RunShellAsync(
                    argv, timeout: null, env: null, workingDirectory: tempDir);
                Assert.Equal("", stderr.Trim());
                Assert.Equal(0, exitCode);
                Assert.Equal(expected, stdout.Replace("\r", "").TrimEnd('\n').Split('\n'));
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Pwd_AfterHomeRelativeCd_PrintsDirectory()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "ps-bash-home-" + Guid.NewGuid().ToString("N"));
        var targetDir = Path.Combine(tempHome, "work", "beagle-term");
        Directory.CreateDirectory(targetDir);

        try
        {
            var (exitCode, stdout, stderr) = await RunShellAsync(
                ["-c", "cd ~/work/beagle-term; pwd"],
                timeout: null,
                env: new Dictionary<string, string?>
                {
                    ["HOME"] = tempHome,
                    ["USERPROFILE"] = tempHome,
                });

            Assert.Equal(0, exitCode);
            Assert.Contains("work/beagle-term", stdout.Replace('\\', '/'));
            Assert.DoesNotContain("work/beagle-term", stderr.Replace('\\', '/'));
        }
        finally
        {
            try { Directory.Delete(tempHome, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Pwd_AfterRelativeCd_PrintsDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ps-bash-cd-relative-" + Guid.NewGuid().ToString("N"));
        var targetDir = Path.Combine(tempDir, "child");
        Directory.CreateDirectory(targetDir);

        try
        {
            var (exitCode, stdout, _) = await RunShellAsync(
                ["-c", "cd child; pwd"],
                timeout: null,
                env: null,
                workingDirectory: tempDir);

            Assert.Equal(0, exitCode);
            Assert.Contains("child", stdout.Replace('\\', '/'));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Pwd_AfterQuotedCdWithSpaces_PrintsDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ps-bash-cd-spaces-" + Guid.NewGuid().ToString("N"));
        var targetDir = Path.Combine(tempDir, "child dir");
        Directory.CreateDirectory(targetDir);

        try
        {
            var (exitCode, stdout, _) = await RunShellAsync(
                ["-c", "cd 'child dir'; pwd"],
                timeout: null,
                env: null,
                workingDirectory: tempDir);

            Assert.Equal(0, exitCode);
            Assert.Contains("child dir", stdout.Replace('\\', '/'));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Pwd_AfterParentDirectoryCd_PrintsParent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ps-bash-cd-parent-" + Guid.NewGuid().ToString("N"));
        var childDir = Path.Combine(tempDir, "child");
        Directory.CreateDirectory(childDir);

        try
        {
            var (exitCode, stdout, _) = await RunShellAsync(
                ["-c", "cd ..; pwd"],
                timeout: null,
                env: null,
                workingDirectory: childDir);

            Assert.Equal(0, exitCode);
            Assert.Contains(tempDir.Replace('\\', '/'), stdout.Replace('\\', '/'));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task CdWithoutArgs_GoesHome()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "ps-bash-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);

        try
        {
            var (exitCode, stdout, _) = await RunShellAsync(
                ["-c", "cd; pwd"],
                timeout: null,
                env: new Dictionary<string, string?>
                {
                    ["HOME"] = tempHome,
                    ["USERPROFILE"] = tempHome,
                });

            Assert.Equal(0, exitCode);
            Assert.Contains(tempHome.Replace('\\', '/'), stdout.Replace('\\', '/'));
        }
        finally
        {
            try { Directory.Delete(tempHome, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task CdMissingDirectory_ReturnsNonZeroAndKeepsCwd()
    {
        // cd to a missing dir returns its OWN non-zero exit (verified as the command's last status).
        var (cdExit, _, _) = await RunShellAsync(["-c", "cd /definitely/not/ps-bash-here"]);
        Assert.NotEqual(0, cdExit);

        // A failed cd keeps the cwd unchanged. Per bash the SHELL exit reflects the LAST command —
        // here the trailing pwd succeeds, so the run exits 0 (correct). The old assertion of non-zero
        // relied on a stale-$?-propagation bug where the successful pwd leaked cd's failure code,
        // since fixed; it is intentionally no longer asserted.
        var (_, stdout, _) = await RunShellAsync(
            ["-c", "pwd; cd /definitely/not/ps-bash-here 2>/dev/null; pwd"]);
        var lines = stdout.Replace('\\', '/')
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2);
        Assert.Equal(lines[0], lines[^1]);
    }

    // ── Heredoc ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Heredoc_CatMultipleLines_OutputsAllLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "cat <<EOF\nline one\nline two\nline three\nEOF");

        Assert.Equal(0, exitCode);
        Assert.Contains("line one", stdout);
        Assert.Contains("line two", stdout);
        Assert.Contains("line three", stdout);
    }

    [SkippableFact]
    public async Task Heredoc_QuotedDelimiter_NoVariableExpansion()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "cat <<'EOF'\n$HOME should be literal\nEOF");

        Assert.Equal(0, exitCode);
        Assert.Contains("$HOME should be literal", stdout);
    }

    // ── Here-string ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task HereString_EchoViaGrepFilter_MatchesLine()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "grep foo <<EOF\nfoo bar\nbaz qux\nEOF");

        Assert.Equal(0, exitCode);
        Assert.Contains("foo bar", stdout);
        Assert.DoesNotContain("baz qux", stdout);
    }

    // ── Piped awk ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Pipe_AwkPrintField_ExtractsColumn()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'hello world' | awk '{print $1}'");

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout);
        Assert.DoesNotContain("world", stdout);
    }

    [SkippableFact]
    public async Task Pipe_AwkWithFieldSep_SplitsOnComma()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'a,b,c' | awk -F, '{print $2}'");

        Assert.Equal(0, exitCode);
        Assert.Contains("b", stdout.Trim().Split('\n').Last().Trim());
    }

    // ── Piped head / tail / wc / cut / tr ────────────────────────────────────

    [SkippableFact]
    public async Task Pipe_HeadLimitsOutput_FirstTwoLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'a\\nb\\nc\\nd\\n' | head -n 2");

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }

    [SkippableFact]
    public async Task Pipe_WcCountsLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'one\\ntwo\\nthree\\n' | wc -l");

        Assert.Equal(0, exitCode);
        Assert.Contains("3", stdout.Trim());
    }

    [SkippableFact]
    public async Task Pipe_CutExtractsField()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'a:b:c' | cut -d: -f2");

        Assert.Equal(0, exitCode);
        Assert.Equal("b", stdout.Trim().Split('\n').Last().Trim());
    }

    [SkippableFact]
    public async Task Pipe_TrTranslatesCharacters()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'hello' | tr 'a-z' 'A-Z'");

        Assert.Equal(0, exitCode);
        Assert.Contains("HELLO", stdout);
    }

    // ── Piped sed ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Pipe_SedSubstitution_ReplacesText()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'hello world' | sed 's/world/earth/'");

        Assert.Equal(0, exitCode);
        Assert.Contains("hello earth", stdout);
    }

    // ── Piped grep ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Pipe_GrepFiltersLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'apple\\nbanana\\napricot\\n' | grep ap");

        Assert.Equal(0, exitCode);
        Assert.Contains("apple", stdout);
        Assert.Contains("apricot", stdout);
        Assert.DoesNotContain("banana", stdout);
    }

    // ── Multi-stage pipeline ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task Pipeline_MultiStage_GrepSortHead()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'cherry\\napple\\nbanana\\napricot\\n' | grep ap | sort | head -n 1");

        Assert.Equal(0, exitCode);
        Assert.Contains("apple", stdout.Trim().Split('\n').Last().Trim());
    }

    // ── Variable expansion in double quotes ──────────────────────────────────

    [SkippableFact]
    public async Task VarExpansion_DoubleQuotedEchoEnvVar()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "X=hello; echo \"value is $X\"");

        Assert.Equal(0, exitCode);
        Assert.Contains("value is hello", stdout);
    }

    // ── Brace expansion ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task BraceExpansion_TupleExpandsToMultiple()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {a,b,c}");

        Assert.Equal(0, exitCode);
        Assert.Contains("a", stdout);
        Assert.Contains("b", stdout);
        Assert.Contains("c", stdout);
    }

    // ── For loop ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ForLoop_IteratesOverWords()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "for x in alpha beta gamma; do echo $x; done");

        Assert.Equal(0, exitCode);
        Assert.Contains("alpha", stdout);
        Assert.Contains("beta", stdout);
        Assert.Contains("gamma", stdout);
    }

    // ── C-style for loop (while-like counting) ─────────────────────────────

    [SkippableFact]
    public async Task ForArith_CountsToThree()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "for ((i=1; i<=3; i++)); do echo $i; done");

        Assert.Equal(0, exitCode);
        Assert.Contains("1", stdout);
        Assert.Contains("2", stdout);
        Assert.Contains("3", stdout);
    }

    [SkippableFact]
    public async Task ForArith_PrintfNoNewline_AccumulatesOnOneLine()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "for ((i=0; i<5; i++)); do printf \"%d \" $i; done; echo");

        Assert.Equal(0, exitCode);
        var trimmed = stdout.TrimEnd('\n', '\r');
        Assert.Equal("0 1 2 3 4", trimmed.TrimEnd());
    }

    // ── If/else ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task IfElse_TrueBranch_OutputsYes()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "if [[ 1 -eq 1 ]]; then echo yes; else echo no; fi");

        Assert.Equal(0, exitCode);
        Assert.Contains("yes", stdout);
        Assert.DoesNotContain("no", stdout);
    }

    // ── Case statement ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Case_MatchesPattern_OutputsCorrectBranch()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "X=banana; case $X in apple) echo fruit1;; banana) echo fruit2;; *) echo other;; esac");

        Assert.Equal(0, exitCode);
        Assert.Contains("fruit2", stdout);
        Assert.DoesNotContain("fruit1", stdout);
        Assert.DoesNotContain("other", stdout);
    }

    // ── Xargs ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Pipe_XargsEcho_ConcatenatesInputOnOneLine()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'one\\ntwo\\nthree\\n' | xargs echo");

        Assert.Equal(0, exitCode);
        Assert.Equal("one two three", stdout.Trim());
    }

    [SkippableFact]
    public async Task Pipe_XargsN1Echo_OutputsSeparateLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'a\\nb\\nc\\n' | xargs -n 1 echo");

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal("a", lines[0]);
        Assert.Equal("b", lines[1]);
        Assert.Equal("c", lines[2]);
    }

    // ── Trap ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Trap_ExitHandler_DoesNotCrash()
    {

        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", "trap 'echo cleanup' EXIT; echo hello");

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout);
    }

    [SkippableFact]
    public async Task Trap_EmptyIntSignal_DoesNotCrash()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "trap '' INT; echo ok");

        Assert.Equal(0, exitCode);
        Assert.Contains("ok", stdout);
    }

    // ── Command substitution ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task CommandSubstitution_InEcho_InlinesResult()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo \"count: $(echo 42)\"");

        Assert.Equal(0, exitCode);
        Assert.Contains("count: 42", stdout);
    }

    // ── Brace range expansion (fix: bare 1..5 → @(1..5)) ────────────────────

    [SkippableFact]
    public async Task BraceRange_DefaultStep_ExpandsSequence()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {1..5}");

        Assert.Equal(0, exitCode);
        Assert.Equal("1 2 3 4 5", stdout.Trim());
    }

    [SkippableFact]
    public async Task BraceRange_ReverseDefaultStep_ExpandsSequence()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {5..1}");

        Assert.Equal(0, exitCode);
        Assert.Equal("5 4 3 2 1", stdout.Trim());
    }

    [SkippableFact]
    public async Task BraceRange_WithStep_ExpandsCorrectly()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {1..10..3}");

        Assert.Equal(0, exitCode);
        Assert.Equal("1 4 7 10", stdout.Trim());
    }

    [SkippableFact]
    public async Task BraceRange_NonDivisibleStep_NoInfiniteLoop()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {1..10..7}");

        Assert.Equal(0, exitCode);
        Assert.Equal("1 8", stdout.Trim());
    }

    // ── File redirect (fix: Invoke-BashRedirect pipeline binding) ────────────

    [SkippableFact]
    public async Task Redirect_EchoToFile_WritesAndReads()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo hello > /tmp/psbash-redir-test.txt; cat /tmp/psbash-redir-test.txt; rm /tmp/psbash-redir-test.txt");

        Assert.Equal(0, exitCode);
        Assert.Equal("hello", stdout.Trim());
    }

    [SkippableFact]
    public async Task Redirect_AppendToFile_AppendsCorrectly()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo line1 > /tmp/psbash-append-test.txt; echo line2 >> /tmp/psbash-append-test.txt; cat /tmp/psbash-append-test.txt; rm /tmp/psbash-append-test.txt");

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal("line1", lines[0]);
        Assert.Equal("line2", lines[1]);
    }

    [SkippableFact]
    public async Task Redirect_ToDevNull_NoOutput()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo hidden > /dev/null; echo visible");

        Assert.Equal(0, exitCode);
        Assert.Equal("visible", stdout.Trim());
    }

    [SkippableFact]
    public async Task Redirect_InputRedirect_CatReadsFile()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo hello > /tmp/psbash-input-redir-test.txt; cat < /tmp/psbash-input-redir-test.txt; rm /tmp/psbash-input-redir-test.txt");

        Assert.Equal(0, exitCode);
        Assert.Equal("hello", stdout.Trim());
    }

    [SkippableFact]
    public async Task Array_LengthExpansion_ReturnsCount()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "array=(one two three); echo ${#array[@]}");

        Assert.Equal(0, exitCode);
        Assert.Equal("3", stdout.Trim());
    }

    [SkippableFact]
    public async Task Array_LengthExpansion_InDoubleQuotes()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", @"array=(one two three); echo ""count: ${#array[@]}""");

        Assert.Equal(0, exitCode);
        Assert.Equal("count: 3", stdout.Trim());
    }

    // ── Tee /dev/null (fix: $null as file path) ─────────────────────────────

    [SkippableFact]
    public async Task Tee_DevNull_PassesThroughWithoutCrash()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo tee-test | tee /dev/null");

        Assert.Equal(0, exitCode);
        Assert.Equal("tee-test", stdout.Trim());
    }

    [SkippableFact]
    public async Task Tee_ToFile_WritesAndPassesThrough()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo tee-content | tee /tmp/psbash-tee-test.txt; echo ---; cat /tmp/psbash-tee-test.txt; rm /tmp/psbash-tee-test.txt");

        Assert.Equal(0, exitCode);
        Assert.Contains("tee-content", stdout);
    }

    // ── Function $1 (fix: $args[0] → $($args[0]) in double quotes) ──────────

    [SkippableFact]
    public async Task Function_PositionalParam_NoIndexSuffix()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "greet() { echo \"hello $1\"; }; greet world");

        Assert.Equal(0, exitCode);
        Assert.Equal("hello world", stdout.Trim());
    }

    [SkippableFact]
    public async Task Function_MultiplePositionalParams_AllResolve()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "f() { echo \"$1 and $2\"; }; f alpha beta");

        Assert.Equal(0, exitCode);
        Assert.Equal("alpha and beta", stdout.Trim());
    }

    // ── While read (fix: trailing newline before split) ──────────────────────

    [SkippableFact]
    public async Task WhileRead_NoExtraBlankLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo -e \"a\\nb\\nc\" | while read x; do echo \"[$x]\"; done");

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal("[a]", lines[0]);
        Assert.Equal("[b]", lines[1]);
        Assert.Equal("[c]", lines[2]);
    }

    // ── Process substitution (fix: Out-File double newlines) ─────────────────

    [SkippableFact]
    public async Task ProcessSub_PasteNoExtraBlankLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "paste <(echo hello) <(echo world)");

        Assert.Equal(0, exitCode);
        Assert.Equal("hello\tworld", stdout.Trim());
    }

    [SkippableFact]
    public async Task ProcessSub_PasteMultiLine_CorrectAlignment()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "paste <(echo -e \"a\\nb\") <(echo -e \"1\\n2\")");

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal("a\t1", lines[0]);
        Assert.Equal("b\t2", lines[1]);
    }

    // ── [[ ]] string comparison (fix: lexicographic vs numeric) ──────────────

    [SkippableFact]
    public async Task ExtendedTest_StringLessThan_LexicographicOrder()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "if [[ \"apple\" < \"banana\" ]]; then echo correct; else echo wrong; fi");

        Assert.Equal(0, exitCode);
        Assert.Contains("correct", stdout);
    }

    // ── Loop iteration cap ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task WhileTrue_IterCapPreventsInfiniteLoop()
    {

        // Set a very low cap so the test completes quickly
        var psi = PsBashTestProcess.Create(["-c", "i=0; while true; do i=$((i+1)); done; echo $i"]);
        psi.Environment["PSBASH_MAX_ITERATIONS"] = "100";

        var (_, stdout, stderr) = await ProcessRunHelper.RunAsync(psi);

        // Should have hit the iteration cap and thrown
        Assert.Contains("loop iteration limit exceeded", stdout + stderr);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FILE LOCKING STRESS TESTS
    //
    // These tests target the Invoke-BashRedirect file I/O path which uses
    // File.WriteAllText/AppendAllText — atomic operations that replaced PS
    // native > operator to avoid file handle leaks in chained commands.
    // ═══════════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task FileLocking_SequentialWritesThenRead_NoCorruption()
    {

        // Rapid sequential writes to same file — tests that handles are released between commands
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo line1 > /tmp/psbash-lock1.txt",
                "echo line2 > /tmp/psbash-lock1.txt",
                "echo line3 > /tmp/psbash-lock1.txt",
                "cat /tmp/psbash-lock1.txt",
                "rm /tmp/psbash-lock1.txt"));

        Assert.Equal(0, exitCode);
        // Last write wins — file should only contain line3
        Assert.Equal("line3", stdout.Trim());
    }

    [SkippableFact]
    public async Task FileLocking_RapidAppendsThenRead_AllLinesPresent()
    {

        // Rapid sequential appends — tests that each >> releases the handle
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo line1 > /tmp/psbash-lock2.txt",
                "echo line2 >> /tmp/psbash-lock2.txt",
                "echo line3 >> /tmp/psbash-lock2.txt",
                "echo line4 >> /tmp/psbash-lock2.txt",
                "echo line5 >> /tmp/psbash-lock2.txt",
                "wc -l /tmp/psbash-lock2.txt",
                "rm /tmp/psbash-lock2.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("5", stdout.Trim());
    }

    [SkippableFact]
    public async Task FileLocking_WriteThenAppendThenCat_NoHandleLeak()
    {

        // Interleave write, append, and read — all three file modes in sequence
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo first > /tmp/psbash-lock3.txt",
                "echo second >> /tmp/psbash-lock3.txt",
                "cat /tmp/psbash-lock3.txt",
                "echo third > /tmp/psbash-lock3.txt",
                "echo ---",
                "cat /tmp/psbash-lock3.txt",
                "rm /tmp/psbash-lock3.txt"));

        Assert.Equal(0, exitCode);
        var parts = stdout.Split("---", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, parts.Length);
        // First cat: first + second
        Assert.Contains("first", parts[0]);
        Assert.Contains("second", parts[0]);
        // Second cat: third (overwrite)
        Assert.Contains("third", parts[1]);
    }

    [SkippableFact]
    public async Task FileLocking_PipelineRedirectChain_EachCommandReleasesHandle()
    {

        // Pipeline output redirected to file, then another command reads it
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo -e \"cherry\\napple\\nbanana\" | sort > /tmp/psbash-lock4.txt",
                "cat /tmp/psbash-lock4.txt",
                "rm /tmp/psbash-lock4.txt"));

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal("apple", lines[0]);
        Assert.Equal("banana", lines[1]);
        Assert.Equal("cherry", lines[2]);
    }

    [SkippableFact]
    public async Task FileLocking_LoopWritesToFile_NoAccumulation()
    {

        // For loop writing to file each iteration — tests handle release between loop bodies
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "for i in 1 2 3 4 5; do echo $i >> /tmp/psbash-lock5.txt; done",
                "wc -l /tmp/psbash-lock5.txt",
                "rm /tmp/psbash-lock5.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("5", stdout.Trim());
    }

    [SkippableFact]
    public async Task FileLocking_TeeAndRedirect_BothFilesWritten()
    {

        // Tee writes to one file, redirect writes to another — both must complete
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo -e \"a\\nb\\nc\" | tee /tmp/psbash-lock6a.txt > /tmp/psbash-lock6b.txt",
                "echo \"tee:\"; cat /tmp/psbash-lock6a.txt",
                "echo \"redir:\"; cat /tmp/psbash-lock6b.txt",
                "rm /tmp/psbash-lock6a.txt /tmp/psbash-lock6b.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("tee:", stdout);
        Assert.Contains("redir:", stdout);
    }

    [SkippableFact]
    public async Task FileLocking_WriteReadWriteRead_RapidAlternation()
    {

        // Rapid write-read alternation on same file — classic file locking trigger
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo alpha > /tmp/psbash-lock7.txt",
                "cat /tmp/psbash-lock7.txt",
                "echo beta > /tmp/psbash-lock7.txt",
                "cat /tmp/psbash-lock7.txt",
                "echo gamma > /tmp/psbash-lock7.txt",
                "cat /tmp/psbash-lock7.txt",
                "rm /tmp/psbash-lock7.txt"));

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal("alpha", lines[0]);
        Assert.Equal("beta", lines[1]);
        Assert.Equal("gamma", lines[2]);
    }

    [SkippableFact]
    public async Task FileLocking_MultipleFilesInOneCommand_AllWritten()
    {

        // Write to multiple different files in quick succession
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo f1 > /tmp/psbash-mf1.txt",
                "echo f2 > /tmp/psbash-mf2.txt",
                "echo f3 > /tmp/psbash-mf3.txt",
                "cat /tmp/psbash-mf1.txt /tmp/psbash-mf2.txt /tmp/psbash-mf3.txt",
                "rm /tmp/psbash-mf1.txt /tmp/psbash-mf2.txt /tmp/psbash-mf3.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("f1", stdout);
        Assert.Contains("f2", stdout);
        Assert.Contains("f3", stdout);
    }

    [SkippableFact]
    public async Task FileLocking_ProcessSubWithRedirect_NoTempFileConflict()
    {

        // Process substitution creates temp files — verify no conflicts with redirects
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "paste <(echo col1) <(echo col2) > /tmp/psbash-psub-redir.txt",
                "cat /tmp/psbash-psub-redir.txt",
                "rm /tmp/psbash-psub-redir.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("col1", stdout);
        Assert.Contains("col2", stdout);
    }

    [SkippableFact]
    public async Task FileLocking_AppendInWhileLoop_AllIterationsWritten()
    {

        // For loop appending to file each iteration — tests handle release between loop bodies
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "for i in 1 2 3 4 5 6 7 8 9 10; do echo \"line $i\" >> /tmp/psbash-wloop.txt; done",
                "wc -l /tmp/psbash-wloop.txt",
                "head -n 1 /tmp/psbash-wloop.txt",
                "tail -n 1 /tmp/psbash-wloop.txt",
                "rm /tmp/psbash-wloop.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("10", stdout); // 10 lines
        Assert.Contains("line 1", stdout); // first line
        Assert.Contains("line 10", stdout); // last line
    }

    [SkippableFact]
    public async Task FileLocking_SedInPlace_FileUpdatedCorrectly()
    {

        // sed -i modifies file in place — tests that file handle is properly released
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo -e \"hello world\\nfoo bar\" > /tmp/psbash-sed.txt",
                "sed -i 's/world/earth/' /tmp/psbash-sed.txt",
                "cat /tmp/psbash-sed.txt",
                "rm /tmp/psbash-sed.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("hello earth", stdout);
        Assert.Contains("foo bar", stdout);
    }

    [SkippableFact]
    public async Task FileLocking_RedirectOverwriteChainOf10_LastValueOnly()
    {

        // 10 rapid overwrites to same file — stress test handle release
        var commands = new List<string>();
        for (int i = 0; i < 10; i++)
            commands.Add($"echo {i} > /tmp/psbash-chain.txt");
        commands.Add("cat /tmp/psbash-chain.txt");
        commands.Add("rm /tmp/psbash-chain.txt");

        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ", commands));

        Assert.Equal(0, exitCode);
        Assert.Equal("9", stdout.Trim());
    }

    // ── Pipeline negation ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task Negation_TrueCommand_ExitCodeIsOne()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "! true; echo $?");

        Assert.Equal("1", stdout.Trim());
    }

    [SkippableFact]
    public async Task Negation_FalseCommand_ExitCodeIsZero()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "! false; echo $?");

        Assert.Equal("0", stdout.Trim());
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ERROR SCENARIO TESTS
    //
    // Verify that commands set correct exit codes on failure and that
    // control flow operators propagate exit codes correctly.
    // ═══════════════════════════════════════════════════════════════════════════

    // ── File error exit codes ───────────────────────────────────────────────

    [SkippableFact]
    public async Task Error_CatNonexistentFile_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "cat nonexistent_file_abc.txt; echo \"exit:$?\"");

        Assert.Contains("exit:1", stdout);
    }

    [SkippableFact]
    public async Task Error_LsNonexistentDir_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "ls nonexistent_dir_xyz/; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    [SkippableFact]
    public async Task Error_HeadNonexistentFile_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "head nonexistent_file_abc.txt; echo \"exit:$?\"");

        Assert.Contains("exit:1", stdout);
    }

    [SkippableFact]
    public async Task Error_SortNonexistentFile_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "sort nonexistent_file_abc.txt; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    [SkippableFact]
    public async Task Error_CpNonexistentSource_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "cp nonexistent_src_abc dest; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    // ── Usage error exit codes ──────────────────────────────────────────────

    [SkippableFact]
    public async Task Error_GrepNoArgs_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "grep; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    [SkippableFact]
    public async Task Error_SedNoExpression_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "sed; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    [SkippableFact]
    public async Task Error_AwkNoProgram_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "awk; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    // ── Exit code propagation in control flow ───────────────────────────────

    [SkippableFact]
    public async Task ControlFlow_FalseAndEcho_OutputsNothing()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "false && echo yes");

        Assert.DoesNotContain("yes", stdout);
    }

    [SkippableFact]
    public async Task ControlFlow_TrueOrEcho_OutputsNothing()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "true || echo no");

        Assert.DoesNotContain("no", stdout);
    }

    [SkippableFact]
    public async Task ControlFlow_TrueAndEcho_OutputsSuccess()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "true && echo success");

        Assert.Contains("success", stdout);
    }

    [SkippableFact]
    public async Task ControlFlow_FalseOrEcho_OutputsFallback()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "false || echo fallback");

        Assert.Contains("fallback", stdout);
    }

    // ── Stderr content verification ─────────────────────────────────────────

    [SkippableFact]
    public async Task Error_CatNonexistentFile_StderrHasNoWriteErrorPrefix()
    {

        var (_, _, stderr) = await RunShellAsync(
            "-c", "cat nonexistent_file_abc.txt");

        Assert.DoesNotContain("Write-Error", stderr);
        Assert.DoesNotContain("FullyQualifiedErrorId", stderr);
    }
}
