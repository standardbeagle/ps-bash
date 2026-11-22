using Xunit;
using PsBash.Shell;

namespace PsBash.Shell.Tests;

public class ShellArgsTests
{
    [Fact]
    public void Parse_VerboseBeforeCompactCommand_EnablesVerboseDiagnostics()
    {
        var result = ShellArgs.Parse(["--verbose", "--compact-output", "-c", "git status"]);

        Assert.True(result.Verbose);
        Assert.True(result.CompactOutput);
        Assert.Equal("git status", result.Command);
    }
    // bash: `bash -i -c 'echo ${HISTFILE:+yes}'` prints yes, `bash -c '…'` prints nothing; a HISTFILE already set is kept.
    [Theory]
    [InlineData(true, null, "/home/u", "/home/u/.bash_history")]
    [InlineData(true, null, "C:\\Users\\u\\", "C:\\Users\\u/.bash_history")]
    [InlineData(false, null, "/home/u", null)]
    [InlineData(true, "/tmp/h", "/home/u", null)]
    [InlineData(true, null, null, null)]
    public void DefaultHistFile_SetOnlyForAnInteractiveShellWithoutOne(bool interactive, string? current, string? home, string? expected)
        => Assert.Equal(expected, ShellArgs.DefaultHistFile(interactive, current, home));

    [Fact]
    public void Parse_EmptyArgs_ReturnsDefaults()
    {
        var result = ShellArgs.Parse([]);

        Assert.Null(result.Command);
        Assert.False(result.Interactive);
        Assert.False(result.Login);
        Assert.False(result.ReadFromStdin);
    }

    [Fact]
    public void Parse_CommandFlag_SetsCommand()
    {
        var result = ShellArgs.Parse(["-c", "echo hello"]);

        Assert.Equal("echo hello", result.Command);
        Assert.False(result.Interactive);
        Assert.False(result.Login);
        Assert.False(result.ReadFromStdin);
    }

    [Fact]
    public void Parse_CommandThenVersionFlag_RunsCommand_NotVersionBanner()
    {
        // Bash: `-c 'cmd' --version` runs cmd (--version is $0), no version print.
        var result = ShellArgs.Parse(["-c", "echo hi", "--version"]);

        Assert.Equal("echo hi", result.Command);
        Assert.False(result.ShowVersion);
        Assert.Contains("--version", result.ScriptArgs);
    }

    [Fact]
    public void Parse_CommandThenPositionals_CapturesThemAsScriptArgs()
    {
        // Bash: `-c 'cmd' name a b` → the trailing args are positionals, not dropped.
        var result = ShellArgs.Parse(["-c", "echo $1", "name", "a", "b"]);

        Assert.Equal("echo $1", result.Command);
        Assert.Equal(new[] { "name", "a", "b" }, result.ScriptArgs);
    }

    [Fact]
    public void Parse_InteractiveFlag_SetsInteractive()
    {
        var result = ShellArgs.Parse(["-i"]);

        Assert.True(result.Interactive);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_StdinFlagWithOperands_MakesThemPositionals()
    {
        // bash: `bash -s x -y < s.txt` reads the script from stdin with $0=bash $1=x $2=-y (options end at the first
        // operand; `--` ends them too). REGRESSION: x was taken for a script file ("x: No such file or directory").
        var result = ShellArgs.Parse(["-s", "x", "-y"]);
        Assert.True(result.ReadFromStdin);
        Assert.Null(result.ScriptPath);
        Assert.Equal(["bash", "x", "-y"], result.ScriptArgs);

        var dashed = ShellArgs.Parse(["-s", "--", "-x", "y"]);
        Assert.Null(dashed.ScriptPath);
        Assert.Equal(["bash", "-x", "y"], dashed.ScriptArgs);
    }

    [Fact]
    public void Parse_StdinFlag_SetsReadFromStdin()
    {
        var result = ShellArgs.Parse(["-s"]);

        Assert.True(result.ReadFromStdin);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_LoginLongFlag_SetsLogin()
    {
        var result = ShellArgs.Parse(["--login"]);

        Assert.True(result.Login);
    }

    [Fact]
    public void Parse_LoginShortFlag_SetsLogin()
    {
        var result = ShellArgs.Parse(["-l"]);

        Assert.True(result.Login);
    }

    [Fact]
    public void Parse_LoginWithCommand_SetsBoth()
    {
        var result = ShellArgs.Parse(["--login", "-c", "ls -la"]);

        Assert.True(result.Login);
        Assert.Equal("ls -la", result.Command);
    }

    [Fact]
    public void Parse_LoginShortWithCommand_SetsBoth()
    {
        var result = ShellArgs.Parse(["-l", "-c", "ls -la"]);

        Assert.True(result.Login);
        Assert.Equal("ls -la", result.Command);
    }

    [Fact]
    public void Parse_EndOfOptions_StopsProcessing()
    {
        var result = ShellArgs.Parse(["--", "-i", "-s"]);

        Assert.False(result.Interactive);
        Assert.False(result.ReadFromStdin);
    }

    [Fact]
    public void Parse_FlagsBeforeEndOfOptions_AreProcessed()
    {
        var result = ShellArgs.Parse(["-i", "--", "-s"]);

        Assert.True(result.Interactive);
        Assert.False(result.ReadFromStdin);
    }

    [Fact]
    public void Parse_CommandWithEndOfOptions_CommandParsedBeforeSeparator()
    {
        var result = ShellArgs.Parse(["-c", "echo test", "--", "-i"]);

        Assert.Equal("echo test", result.Command);
        Assert.False(result.Interactive);
    }

    [Fact]
    public void Parse_CommandFlagWithoutArgument_CommandIsNull()
    {
        var result = ShellArgs.Parse(["-c"]);

        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_RcfileWithInteractiveCommand_ConsumesRcfileValue()
    {
        var result = ShellArgs.Parse(["--rcfile", "/dev/null", "-i", "-c", "echo hi"]);

        Assert.Null(result.Error);
        Assert.Null(result.ScriptPath);
        Assert.True(result.Interactive);
        Assert.Equal("echo hi", result.Command);
    }

    [Fact]
    public void Parse_InitFileWithoutValue_IsAnError()
    {
        Assert.NotNull(ShellArgs.Parse(["--init-file"]).Error);
    }

    // bash 5.2 oracle: `bash --rcfile rc -i -c 'echo $x'` sources rc first; `-c` alone, or with --norc, never does.
    [Theory]
    [InlineData(new[] { "--rcfile", "rc", "-i", "-c", "true" }, "rc")]
    [InlineData(new[] { "--init-file", "rc", "-i", "-c", "true" }, "rc")]
    [InlineData(new[] { "--rcfile", "rc", "-c", "true" }, null)]
    [InlineData(new[] { "--norc", "--rcfile", "rc", "-i", "-c", "true" }, null)]
    [InlineData(new[] { "--rcfile", "rc", "--norc", "-i" }, null)]
    [InlineData(new[] { "--rcfile", "rc", "-i" }, "rc")]
    public void Parse_Rcfile_KeptOnlyForAnInteractiveShellWithoutNorc(string[] argv, string? expected)
    {
        var result = ShellArgs.Parse(argv);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.RcFile);
    }

    [Fact]
    public void Parse_RcfileWithInteractiveCommand_SourcesItAfterTheOptionsBeforeTheCommand()
    {
        var result = ShellArgs.Parse(["--rcfile", "my rc", "-e", "-i", "-c", "echo hi"]);

        Assert.Equal("shopt -s " + ShellArgs.InteractiveMark + "; set -e; " + ShellArgs.RcSourceCommand("my rc"), result.Prelude);
        Assert.Equal("echo hi", result.Command);
    }

    [Theory]
    [InlineData("rc", "if [ -r 'rc' ]; then . 'rc'; fi")]
    // Injection probes: the path is one single-quoted word whatever it holds.
    [InlineData("a'; rm -rf x; '", "if [ -r 'a'\\''; rm -rf x; '\\''' ]; then . 'a'\\''; rm -rf x; '\\'''; fi")]
    [InlineData("$(id)`x`", "if [ -r '$(id)`x`' ]; then . '$(id)`x`'; fi")]
    public void RcSourceCommand_SourcesTheFileAsOneQuotedWord_SkippingAnUnreadableOne(string rcFile, string expected)
        => Assert.Equal(expected, ShellArgs.RcSourceCommand(rcFile));

    [Fact]
    public void InteractiveHostFlags_PassTheRcfileAbsolute_AndADeviceAsWritten()
    {
        Assert.Equal(["--rcfile=" + Path.GetFullPath("rc")], ShellArgs.Parse(["--rcfile", "rc", "-i"]).InteractiveHostFlags());
        Assert.Equal(["--rcfile=/dev/null"], ShellArgs.Parse(["--rcfile", "/dev/null", "-i"]).InteractiveHostFlags());
        Assert.Equal(["--no-profile"], ShellArgs.Parse(["--norc", "--rcfile", "rc", "-i"]).InteractiveHostFlags());
    }
    [Fact]
    public void Parse_OptionFlags_BuildAPreludeForTheCommand()
    {
        var r = ShellArgs.Parse(["-eu", "-o", "pipefail", "-O", "nullglob", "+O", "extglob", "-c", "true"]);
        Assert.Null(r.Error);
        Assert.Equal("set -e; set -u; set -o pipefail; shopt -s nullglob; shopt -u extglob", r.Prelude);
        Assert.Equal("true", r.Command);
    }

    [Fact] public void Parse_OptionNameWithMetacharacters_IsRefused() => Assert.NotNull(ShellArgs.Parse(["-o", "x;rm", "-c", "true"]).Error);

    [Fact]
    public void Parse_CommandFlagAtEnd_CommandIsNull()
    {
        var result = ShellArgs.Parse(["-i", "-c"]);

        Assert.True(result.Interactive);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_AllFlagsCombined_AllSet()
    {
        var result = ShellArgs.Parse(["-i", "-s", "--login", "-c", "whoami"]);

        Assert.True(result.Interactive);
        Assert.True(result.ReadFromStdin);
        Assert.True(result.Login);
        Assert.Equal("whoami", result.Command);
    }

    [Fact]
    public void Parse_UnknownFlags_ReportsError()
    {
        var result = ShellArgs.Parse(["--verbose", "-k", "-c", "echo hi"]);

        Assert.Equal("echo hi", result.Command);
        Assert.False(result.Interactive);
        Assert.Equal("unsupported option '-k'", result.Error);
    }

    // Oracle: `bash -x -c 'echo traced'` traces; `bash -c -e 'false; echo ce'` is status 1 (options may follow -c,
    // the first non-option word is the command; `-e` used to BE the command, status 127).
    [Theory]
    [InlineData(new[] { "-x", "-c", "echo hi" }, "set -x")]
    [InlineData(new[] { "-c", "-e", "false; echo ce" }, "set -e")]
    [InlineData(new[] { "-c", "-x", "-u", "true" }, "set -x; set -u")]
    [InlineData(new[] { "-xe", "-c", "true" }, "set -x; set -e")]
    // Oracle: `bash -v -c 'echo $-'` prints hvBc (-v was "unsupported option").
    [InlineData(new[] { "-v", "-c", "true" }, "set -v")]
    [InlineData(new[] { "-xv", "-c", "true" }, "set -x; set -v")]
    public void Parse_SetOptionFlags_AnywhereBeforeTheCommand_JoinThePrelude(string[] argv, string prelude)
    {
        var r = ShellArgs.Parse(argv);
        Assert.Null(r.Error);
        Assert.Equal(prelude, r.Prelude);
        Assert.Equal(argv[^1], r.Command);
    }

    // Oracle (bash 5.2): `bash -x -v -- foo.sh` and `bash -x -v - foo.sh` run foo.sh traced (a lone `-` ends the
    // options as `--` does; it was "unsupported option '-'"); options with a script used to be refused.
    [Theory]
    [InlineData("--")]
    [InlineData("-")]
    public void Parse_SetOptionFlags_WithAScriptFile_JoinThePrelude(string endOfOptions)
    {
        var r = ShellArgs.Parse(["-x", "-v", endOfOptions, "foo.sh", "a"]);
        Assert.Null(r.Error);
        Assert.Equal("foo.sh", r.ScriptPath);
        Assert.Equal(["a"], r.ScriptArgs);
        Assert.Equal("set -x; set -v", r.Prelude);
    }

    [Fact]
    public void Parse_SetOptionFlags_WithNothingToRun_AreRefused()
    {
        var r = ShellArgs.Parse(["-e"]);
        Assert.Equal("-e, -u, -x, -v, -o and -O are only supported together with -c, -s or a script file", r.Error);
    }

    // The options go on the script's first line, so $LINENO is unchanged; a #! line after the `;` is a comment.
    [Theory]
    [InlineData(null, "echo $LINENO\n", "echo $LINENO\n")]
    [InlineData("set -x; set -v", "#!/bin/sh\necho $LINENO\n", "set -x; set -v; #!/bin/sh\necho $LINENO\n")]
    public void WithPrelude_PutsTheOptionsOnTheFirstLine(string? prelude, string script, string expected) =>
        Assert.Equal(expected, ShellArgs.WithPrelude(prelude, script));

    // Oracle (bash 5.2): `export SHELLOPTS; set -x; bash -c 'echo 2'` traces `+ echo 2` in the child; a SHELLOPTS in
    // the environment at startup turns on each option it lists. Always-on and interactive-only names change nothing,
    // unknown names are ignored, and xtrace goes last so no other startup command is traced.
    [Theory]
    [InlineData("-c|true", null, null)]
    [InlineData("-c|true", "", null)]
    [InlineData("-c|true", "braceexpand:hashall:interactive-comments:monitor:emacs", null)]
    [InlineData("-c|true", "braceexpand:hashall:interactive-comments:xtrace", "set -o xtrace")]
    [InlineData("-c|true", "xtrace:errexit:pipefail:bogus;rm", "set -o errexit; set -o pipefail; set -o xtrace")]
    [InlineData("-u|-c|true", "xtrace:nounset", "set -o nounset; set -u; set -o xtrace")]
    public void WithInheritedShellOpts_TurnsOnTheListedOptions(string argv, string? shellopts, string? expectedPrelude) =>
        Assert.Equal(expectedPrelude, ShellArgs.Parse(argv.Split('|')).WithInheritedShellOpts(shellopts).Prelude);

    // Oracle (bash 5.2): `-c -- 'echo two'` and `-c - 'echo one'` run the command; `+c 'echo hi'` is accepted;
    // `-oo errexit noglob` and `-eo noglob` give each bundled `o` the next word.
    [Theory]
    [InlineData("-c|--|echo two", "echo two", null)]
    [InlineData("-c|-|echo one", "echo one", null)]
    [InlineData("+c|echo hi", "echo hi", null)]
    [InlineData("-oo|errexit|noglob|-c|true", "true", "set -o errexit; set -o noglob")]
    [InlineData("-eo|noglob|-c|true", "true", "set -e; set -o noglob")]
    [InlineData("-c|-o|pipefail|true", "true", "set -o pipefail")]
    public void Parse_DashCForms_MatchBash(string argvText, string command, string? prelude)
    {
        var r = ShellArgs.Parse(argvText.Split('|'));
        Assert.Null(r.Error);
        Assert.Equal(command, r.Command);
        Assert.Equal(prelude, r.Prelude);
    }

    // Oracle: `bash -c -z 'echo z'` is "-z: invalid option", status 2. ps-bash ran `-z` as the command (127).
    [Theory]
    [InlineData("-c|-z|echo z")]
    [InlineData("-c|---|echo three")]
    [InlineData("-c|-z")]
    public void Parse_UnknownOptionAfterDashC_IsAnError(string argvText)
    {
        var r = ShellArgs.Parse(argvText.Split('|'));
        Assert.NotNull(r.Error);
        Assert.Null(r.Command);
    }

    // Oracle: `bash -n -c 'echo x'` runs nothing (status 0); `bash -n s.sh` is a syntax check, valid without -c.
    [Theory]
    [InlineData("-n -c true")]
    [InlineData("-c -n true")]
    [InlineData("-n s.sh")]
    public void Parse_NoExecFlag_SetsNoExecWithoutAPrelude(string argvText)
    {
        var r = ShellArgs.Parse(argvText.Split(' '));
        Assert.Null(r.Error);
        Assert.True(r.NoExec);
        Assert.Null(r.Prelude);
    }

    [Fact]
    public void Parse_CommandWithQuotedString_PreservesCommand()
    {
        var result = ShellArgs.Parse(["-c", "echo \"hello world\""]);

        Assert.Equal("echo \"hello world\"", result.Command);
    }

    [Fact]
    public void Parse_CommandWithRedirection_PreservesFullCommand()
    {
        var result = ShellArgs.Parse(["-c", "echo err 2>/dev/null"]);

        Assert.Equal("echo err 2>/dev/null", result.Command);
    }

    [Fact]
    public void Parse_RecordValueEquality_Works()
    {
        var a = ShellArgs.Parse(["-c", "echo hi"]);
        var b = ShellArgs.Parse(["-c", "echo hi"]);

        Assert.Equal(a, b);
    }

    // Regression: Claude Code on Windows invokes ps-bash as `-lc "cmd"`.
    // Prior parser had no `-lc` case, dropped both flags + command, fell into
    // interactive mode. Captured live via PSBASH_TRACE.
    [Fact]
    public void Parse_BundledLoginCommand_ExpandsShortFlags()
    {
        var result = ShellArgs.Parse(["-lc", "echo hi"]);

        Assert.True(result.Login);
        Assert.Equal("echo hi", result.Command);
    }

    [Fact]
    public void Parse_BundledCommandLogin_ExpandsShortFlags()
    {
        var result = ShellArgs.Parse(["-cl", "echo hi"]);

        Assert.True(result.Login);
        Assert.Equal("echo hi", result.Command);
    }

    [Fact]
    public void Parse_BundledInteractiveCommand_ExpandsShortFlags()
    {
        var result = ShellArgs.Parse(["-ic", "echo hi"]);

        Assert.True(result.Interactive);
        Assert.Equal("echo hi", result.Command);
    }

    // Oracle: `bash -i -c 'echo $-'` is himBHc and `test -o emacs` is true; `bash -c` has neither. The prelude marks
    // this shell interactive (first, so a later `-o vi` still wins over the emacs default).
    [Fact]
    public void Parse_InteractiveCommand_PreludeMarksTheShellInteractive()
    {
        Assert.Equal("shopt -s psbash_interactive; set -x", ShellArgs.Parse(["-i", "-x", "-c", "echo hi"]).Prelude);
        Assert.Null(ShellArgs.Parse(["-c", "echo hi"]).Prelude);
        Assert.Null(ShellArgs.Parse(["-i"]).Prelude);
    }

    // Regression: Claude Code also invokes ps-bash as `-c -l "cmd"`.
    // Prior parser greedily took `-l` as the command, then dropped the
    // real command, then tried to execute `-l` as a PowerShell command,
    // producing `"The term '-l' is not recognized"`. Captured live.
    [Fact]
    public void Parse_CommandThenLogin_SkipsLoginFlagAndTakesRealCommand()
    {
        var result = ShellArgs.Parse(["-c", "-l", "echo hi"]);

        Assert.True(result.Login);
        Assert.Equal("echo hi", result.Command);
    }

    [Fact]
    public void Parse_CommandThenMultipleFlags_SkipsAllAndTakesRealCommand()
    {
        var result = ShellArgs.Parse(["-c", "-l", "-i", "--noprofile", "echo hi"]);

        Assert.True(result.Login);
        Assert.True(result.Interactive);
        Assert.True(result.NoProfile);
        Assert.Equal("echo hi", result.Command);
    }

    // Real ps-bash invocation captured from Claude Code's snapshot bootstrap.
    // The command string starts with `shopt` and contains pipes, redirects,
    // quotes — must round-trip intact through the -c skip logic.
    [Fact]
    public void Parse_ClaudeCodeSnapshotPattern_PreservesFullCommand()
    {
        var cmd = "shopt -u extglob 2>/dev/null || true && eval 'git status' < /dev/null && pwd -P >| /tmp/x";
        var result = ShellArgs.Parse(["-c", "-l", cmd]);

        Assert.True(result.Login);
        Assert.Equal(cmd, result.Command);
    }

    // The CURRENT Claude Code wrapper (2026-05-28): the snapshot bootstrap now
    // injects a TEMP/TMP env-setup as a multi-var bare assignment before the
    // command. The full string — multi-var assignment, pipes, redirects, quotes,
    // force-clobber `>|` — must round-trip intact through the `-c -l` skip logic.
    [Fact]
    public void Parse_ClaudeCodeEnvSetupWrapper_PreservesFullCommand()
    {
        var cmd = "shopt -u extglob 2>/dev/null || true && "
                + "TEMP='C:\\Temp' TMP='C:\\Temp' && "
                + "eval 'git status' < /dev/null && pwd -P >| /tmp/x";
        var result = ShellArgs.Parse(["-c", "-l", cmd]);

        Assert.True(result.Login);
        Assert.Equal(cmd, result.Command);
    }

    // Regression: `ps-bash -c "git log --oneline -20"` was reported to fail
    // with "The term '-l' is not recognized" — i.e. somewhere `--oneline` was
    // being peeled apart as a short-flag collision (-o / -n / -e / -l / -i / -n / -e).
    // The Args layer must pass the full quoted command string to the
    // transpiler intact, even though it contains `--word` tokens whose first
    // character is also a recognized short flag.
    [Theory]
    [InlineData("git log --oneline -20")]
    [InlineData("git log --list")]
    [InlineData("git diff --name-only HEAD~1")]
    [InlineData("grep --include='*.cs' -r foo .")]
    [InlineData("ls --long --color=auto")]
    public void Parse_CommandWithLongFlagStartingWithShortFlagLetter_PreservesFullCommand(string cmd)
    {
        var result = ShellArgs.Parse(["-c", cmd]);

        Assert.Equal(cmd, result.Command);
        Assert.False(result.Login);
        Assert.False(result.Interactive);
        Assert.False(result.ReadFromStdin);
    }

    [Fact]
    public void Parse_UnixPathsFlag_SetsTrue()
    {
        var result = ShellArgs.Parse(["--unix-paths", "-c", "echo hi"]);
        Assert.True(result.UnixPaths);
    }

    [Fact]
    public void Parse_WindowsPathsFlag_SetsFalse()
    {
        var result = ShellArgs.Parse(["--windows-paths", "-c", "echo hi"]);
        Assert.False(result.UnixPaths);
    }

    [Fact]
    public void Parse_NoPathsFlag_LeavesUnixPathsNull()
    {
        var result = ShellArgs.Parse(["-c", "echo hi"]);
        Assert.Null(result.UnixPaths);
    }

    [Fact]
    public void Parse_RecordWithExpression_CreatesModifiedCopy()
    {
        var original = ShellArgs.Parse(["-i"]);
        var modified = original with { Command = "ls" };

        Assert.True(modified.Interactive);
        Assert.Equal("ls", modified.Command);
        Assert.Null(original.Command);
    }

    // ── M3: file-arg (ScriptPath / ScriptArgs) ───────────────────────────────

    [Fact]
    public void Parse_ScriptPathAndArgs_SetsScriptPathAndScriptArgs()
    {
        var result = ShellArgs.Parse(["script.sh", "a", "b"]);

        Assert.Equal("script.sh", result.ScriptPath);
        Assert.Equal(["a", "b"], result.ScriptArgs);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_LoginFlagBeforeScriptPath_SetsLoginAndScriptPath()
    {
        var result = ShellArgs.Parse(["-l", "script.sh", "a"]);

        Assert.True(result.Login);
        Assert.Equal("script.sh", result.ScriptPath);
        Assert.Equal(["a"], result.ScriptArgs);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_EndOfOptionsThenScriptPath_SetsScriptPath()
    {
        var result = ShellArgs.Parse(["--", "script.sh"]);

        Assert.Equal("script.sh", result.ScriptPath);
        Assert.Empty(result.ScriptArgs);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_CommandFlagTakesPrecedenceOverScriptPath()
    {
        // When -c is given, ScriptPath should not be set from remaining args
        var result = ShellArgs.Parse(["-c", "echo hi"]);

        Assert.Equal("echo hi", result.Command);
        Assert.Null(result.ScriptPath);
    }

    [Fact]
    public void Parse_NoPositionalArgs_ScriptPathIsNull()
    {
        var result = ShellArgs.Parse(["-l"]);

        Assert.Null(result.ScriptPath);
        Assert.Empty(result.ScriptArgs);
    }

    [Fact]
    public void Parse_ScriptPathOnly_NoScriptArgs()
    {
        var result = ShellArgs.Parse(["script.sh"]);

        Assert.Equal("script.sh", result.ScriptPath);
        Assert.Empty(result.ScriptArgs);
    }

    // Bash: everything after the script path is positional ($1..), flags included.
    // Oracle: `bash s.sh -v -e -c x` -> $1=-v $2=-e $3=-c $4=x; `bash s.sh --version --help`
    // hands both to the script instead of printing anything.
    [Fact]
    public void Parse_FlagsAfterScriptPath_AreScriptArgs()
    {
        var result = ShellArgs.Parse(["s.sh", "-v", "-e", "-c", "x"]);

        Assert.Equal("s.sh", result.ScriptPath);
        Assert.Equal(["-v", "-e", "-c", "x"], result.ScriptArgs);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_InfoFlagsAfterScriptPath_AreScriptArgsNotInfoRequests()
    {
        var result = ShellArgs.Parse(["s.sh", "--version", "--help", "--verbose", "-lc"]);

        Assert.Equal("s.sh", result.ScriptPath);
        Assert.Equal(["--version", "--help", "--verbose", "-lc"], result.ScriptArgs);
        Assert.False(result.ShowVersion);
        Assert.False(result.ShowHelp);
        Assert.False(result.Verbose);
        Assert.False(result.Login);
    }

    [Fact]
    public void Parse_OptionsBeforeScriptPath_StillParsed_BundleAfterTimeoutValueStillExpands()
    {
        var result = ShellArgs.Parse(["--timeout", "5", "-lc", "echo hi"]);

        Assert.Equal("5", result.Timeout);
        Assert.True(result.Login);
        Assert.Equal("echo hi", result.Command);
    }

    // PTY-9 follow-on: `--ps` / `--raw-ps` flag — bypasses bash transpile and
    // forwards the command body to the host runspace as raw PowerShell. The
    // in-band entry point for tests that need to drive raw PS probes
    // ([Console]::ReadKey, etc.) under the same launcher pipeline as bash.
    [Fact]
    public void Parse_RawPsFlag_SetsRawPowerShell()
    {
        var result = ShellArgs.Parse(["--ps", "-c", "echo 'raw'"]);

        Assert.True(result.RawPowerShell);
        Assert.Equal("echo 'raw'", result.Command);
    }

    [Fact]
    public void Parse_RawPsLongAlias_SetsRawPowerShell()
    {
        var result = ShellArgs.Parse(["--raw-ps", "-c", "$PSVersionTable"]);

        Assert.True(result.RawPowerShell);
        Assert.Equal("$PSVersionTable", result.Command);
    }

    [Fact]
    public void Parse_NoRawPsFlag_DefaultsFalse()
    {
        var result = ShellArgs.Parse(["-c", "echo hi"]);

        Assert.False(result.RawPowerShell);
    }

    // Regression: `ps-bash --version` had no parser case, fell through to the
    // default (a `-`-prefixed token is not a ScriptPath), leaving Command and
    // ScriptPath null — which dropped the launcher into interactive/stdin mode
    // and hung when no tty was attached.
    [Fact]
    public void Parse_VersionLongFlag_SetsShowVersion()
    {
        var result = ShellArgs.Parse(["--version"]);

        Assert.True(result.ShowVersion);
        Assert.Null(result.Command);
        Assert.Null(result.ScriptPath);
    }

    [Fact]
    public void Parse_VersionShortFlag_SetsShowVersion()
    {
        var result = ShellArgs.Parse(["-V"]);

        Assert.True(result.ShowVersion);
    }

    [Fact]
    public void Parse_HelpLongFlag_SetsShowHelp()
    {
        var result = ShellArgs.Parse(["--help"]);

        Assert.True(result.ShowHelp);
        Assert.Null(result.Command);
        Assert.Null(result.ScriptPath);
    }

    [Fact]
    public void Parse_NoInfoFlags_DefaultsFalse()
    {
        var result = ShellArgs.Parse(["-c", "echo hi"]);

        Assert.False(result.ShowVersion);
        Assert.False(result.ShowHelp);
    }

    [Fact]
    public void Parse_CompactOutputFlag_SetsTrue()
    {
        var result = ShellArgs.Parse(["--compact-output", "-c", "echo hi"]);

        Assert.True(result.CompactOutput);
        Assert.Equal("echo hi", result.Command);
    }

    [Theory]
    [InlineData("--caveman")]
    [InlineData("--wenyan")]
    public void Parse_CompactOutputAliases_SetTrue(string flag)
    {
        var result = ShellArgs.Parse([flag, "-c", "echo hi"]);

        Assert.True(result.CompactOutput);
    }

    [Fact]
    public void Parse_NoCompactOutputFlag_SetsFalse()
    {
        var result = ShellArgs.Parse(["--no-compact-output", "-c", "echo hi"]);

        Assert.False(result.CompactOutput);
    }

    [Fact]
    public void Parse_NoCompactOutputFlag_LeavesNull()
    {
        var result = ShellArgs.Parse(["-c", "echo hi"]);

        Assert.Null(result.CompactOutput);
    }

    [Fact]
    public void Parse_CommandThenCompactOutput_SkipsFlagAndTakesRealCommand()
    {
        var result = ShellArgs.Parse(["-c", "--compact-output", "echo hi"]);

        Assert.True(result.CompactOutput);
        Assert.Equal("echo hi", result.Command);
    }

    // A literal `--version` passed as the -c command body must remain the
    // command — only a top-level `--version` flag flips ShowVersion.
    [Fact]
    public void Parse_VersionAsCommandBody_DoesNotSetShowVersion()
    {
        var result = ShellArgs.Parse(["-c", "echo --version"]);

        Assert.False(result.ShowVersion);
        Assert.Equal("echo --version", result.Command);
    }

    // ── --timeout flag (per-command idle timeout the caller can set) ─────────

    [Fact]
    public void Parse_NoTimeoutFlag_TimeoutIsNull()
    {
        Assert.Null(ShellArgs.Parse(["-c", "echo hi"]).Timeout);
    }

    [Fact]
    public void Parse_TimeoutSeconds_SetsTimeout()
    {
        var result = ShellArgs.Parse(["--timeout", "600", "-c", "echo hi"]);

        Assert.Equal("600", result.Timeout);
        Assert.Equal("echo hi", result.Command);
    }

    [Fact]
    public void Parse_TimeoutEqualsForm_SetsTimeout()
    {
        Assert.Equal("600", ShellArgs.Parse(["--timeout=600", "-c", "echo hi"]).Timeout);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("0")]
    [InlineData("infinite")]
    public void Parse_TimeoutDisableValues_PassThroughVerbatim(string value)
    {
        // The launcher forwards the raw value to PSBASH_TIMEOUT; IpcWorker owns
        // interpretation (none/0/off/infinite => unbounded).
        Assert.Equal(value, ShellArgs.Parse(["--timeout", value, "-c", "echo hi"]).Timeout);
    }

    [Fact]
    public void Parse_TimeoutWithoutValue_DoesNotThrow()
    {
        // Trailing --timeout with no value: tolerated, leaves Timeout null.
        var result = ShellArgs.Parse(["--timeout"]);
        Assert.Null(result.Timeout);
    }
}
