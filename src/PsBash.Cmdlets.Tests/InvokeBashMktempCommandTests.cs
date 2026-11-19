using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// <c>Invoke-BashMktemp</c> from PsBash.psm1 to a binary cmdlet
/// (<see cref="PsBash.Cmdlets.InvokeBashMktempCommand"/>).
///
/// Oracle: GNU coreutils 9.4 mktemp (WSL Ubuntu-24.04): the X run, the template's directory, <c>$TMPDIR</c>, and
/// the GNU diagnostics. With no template and <c>$TMPDIR</c> unset the file goes to ps-bash's <c>/tmp</c> stand-in,
/// <c>{TempPath}/ps-bash/proc-sub/</c> (ps-bash-specific: asserted by hand).
///
/// Failure-surface axes covered (per Directive 3):
/// empty input (no args), unicode/template, alias resolution, <c>--help</c>,
/// quoting/injection (Directive 12). Pipeline / large-input / streaming
/// axes do not apply: mktemp produces a single name and exits.
/// </summary>
public class InvokeBashMktempCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly List<string> _createdPaths = new();
    private readonly SharedPwshFixture _fixture;

    public InvokeBashMktempCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        foreach (var p in _createdPaths)
        {
            try
            {
                if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                else if (File.Exists(p)) File.Delete(p);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var lines = result.Select(o => o?.ToString() ?? "").ToArray();
        // Track every emitted path for cleanup.
        foreach (var l in lines)
        {
            if (!string.IsNullOrEmpty(l) && (File.Exists(l) || Directory.Exists(l)))
            {
                _createdPaths.Add(l);
            }
        }
        return lines;
    }

    private static readonly string ExpectedRoot =
        Path.Combine(Path.GetTempPath(), "ps-bash", "proc-sub");

    [SkippableFact]
    public void Mktemp_DashP_HonorsDirectory_NoBinderCrash()
    {
        // Regression: bare -p prefix-collides with -ProgressAction (PS 7.4) and the
        // binder crashed ("ambiguous") before the cmdlet ran. The P decoy binds -p DIR
        // and DIR overrides the proc-sub target. A passing invocation proves no crash.
        var dir = Path.Combine(Path.GetTempPath(), $"psb-mktemp-p-{Guid.NewGuid():N}".Substring(0, 25));
        Directory.CreateDirectory(dir);
        _createdPaths.Add(dir);

        var lines = RunLines($"(Invoke-BashMktemp -p '{dir.Replace("'", "''")}').Path");

        Assert.Single(lines);
        Assert.True(File.Exists(lines[0]), $"expected a file at {lines[0]}");
        Assert.StartsWith(
            new DirectoryInfo(dir).FullName,
            new FileInfo(lines[0]).FullName,
            StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void Mktemp_NoArgs_CreatesEmptyFileInProcSubDir()
    {
        var lines = RunLines("(Invoke-BashMktemp).Path");
        Assert.Single(lines);
        var path = lines[0];
        Assert.True(File.Exists(path), $"expected file to exist: {path}");
        Assert.Equal(0, new FileInfo(path).Length);
        // Path lives under an exported $TMPDIR (GNU; macOS always sets it), else {Temp}/ps-bash/proc-sub/...
        string? tmpdir = Environment.GetEnvironmentVariable("TMPDIR");
        string root = string.IsNullOrEmpty(tmpdir) ? ExpectedRoot : Path.TrimEndingDirectorySeparator(tmpdir);
        Assert.StartsWith(root, path,
            // case-insensitive on Windows
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Mktemp_DashD_CreatesDirectory()
    {
        var lines = RunLines("(Invoke-BashMktemp -d).Path");
        Assert.Single(lines);
        var path = lines[0];
        Assert.True(Directory.Exists(path), $"expected directory: {path}");
        Assert.False(File.Exists(path));
    }

    // ---- GNU template / directory rules (oracle: coreutils 9.4 mktemp, WSL Ubuntu-24.04) ----

    /// <summary>A fresh scratch directory that the script runs in.</summary>
    private string Scratch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psb-mkt-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        _createdPaths.Add(dir);
        return dir;
    }

    private CmdResult RunIn(string dir, string script)
        => CmdResult.Run(_fixture.AcquireFresh(), $"Set-Location -LiteralPath '{dir.Replace("'", "''")}'; {script}");

    // REGRESSION: the X run was kept and a random name appended ('myapp.XXXXXX' -> 'myapp.ab3cd.efg'); GNU replaces
    // the last run of X's (same length, [A-Za-z0-9]) and keeps what follows it.
    [Theory]
    [InlineData("myapp.XXXXXX", "myapp.", 6, "")]
    [InlineData("q.XXX.txt", "q.", 3, ".txt")]
    [InlineData("$(throw).XXX", "$(throw).", 3, "")]   // injection probe: a literal name, never evaluated
    public void Mktemp_Template_ReplacesTheXRunInTheCurrentDirectory(string template, string prefix, int xs, string suffix)
    {
        var dir = Scratch();
        var r = RunIn(dir, $"Invoke-BashMktemp '{template}'");
        r.AssertSuccess();
        var name = Assert.Single(r.Lines);
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(prefix) + "[A-Za-z0-9]{" + xs + "}"
            + System.Text.RegularExpressions.Regex.Escape(suffix) + "$", name);
        Assert.True(File.Exists(Path.Combine(dir, name)), name);
    }

    // REGRESSION: a template's directory was dropped and the file planted in ps-bash/proc-sub.
    [SkippableFact]
    public void Mktemp_TemplateWithDirectory_IsRelativeToTheCurrentDirectory()
    {
        var dir = Scratch();
        Directory.CreateDirectory(Path.Combine(dir, "p"));
        var r = RunIn(dir, "Invoke-BashMktemp p/leaf.XXX");
        r.AssertSuccess();
        var name = Assert.Single(r.Lines);
        Assert.Matches("^p/leaf\\.[A-Za-z0-9]{3}$", name);
        Assert.True(File.Exists(Path.Combine(dir, name)));

        RunIn(dir, "Invoke-BashMktemp -d nosuch/aXXX")
            .AssertFailed(1, "mktemp: failed to create directory via template ‘nosuch/aXXX’: No such file or directory");
    }

    // REGRESSION: $TMPDIR was ignored. No template / -t / --tmpdir use it; -p DIR wins over it except under -t.
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void Mktemp_HonorsTmpdir()
    {
        // The store is the process environment; off Windows .NET's own temp path reads TMPDIR, so a relative value
        // would leak into parallel classes. Unix coverage: Plan_IsGnuMktemp + the differential case.
        Skip.IfNot(OperatingSystem.IsWindows(), "TMPDIR is the .NET temp path off Windows");
        var dir = Scratch();
        Directory.CreateDirectory(Path.Combine(dir, "t"));
        Directory.CreateDirectory(Path.Combine(dir, "p"));
        string Run(string args)
        {
            var r = RunIn(dir, "$env:TMPDIR = $null; [PsBash.Cmdlets.BashVariableStore]::Set('TMPDIR', 't'); "
                + "[PsBash.Cmdlets.BashVariableStore]::Export('TMPDIR'); try { Invoke-BashMktemp " + args
                + " } finally { [PsBash.Cmdlets.BashVariableStore]::Unexport('TMPDIR'); [PsBash.Cmdlets.BashVariableStore]::Set('TMPDIR', $null) }");
            r.AssertSuccess();
            return Assert.Single(r.Lines);
        }
        Assert.Matches("^t/tmp\\.[A-Za-z0-9]{10}$", Run(""));
        Assert.Matches("^t/abc\\.[A-Za-z0-9]{4}$", Run("-t abc.XXXX"));
        Assert.Matches("^t/abc\\.[A-Za-z0-9]{4}$", Run("--tmpdir abc.XXXX"));
        Assert.Matches("^p/abc\\.[A-Za-z0-9]{4}$", Run("-p p abc.XXXX"));
        Assert.Matches("^t/abc\\.[A-Za-z0-9]{4}$", Run("-t -p p abc.XXXX"));
        Assert.Matches("^abc\\.[A-Za-z0-9]{4}$", Run("abc.XXXX"));
        Assert.True(Directory.Exists(Path.Combine(dir, Run("-d").Replace('/', Path.DirectorySeparatorChar))));
    }

    [Theory]
    [InlineData("abc", "mktemp: too few X's in template ‘abc’")]
    [InlineData("-t a/bXXX", "mktemp: invalid template, ‘a/bXXX’, contains directory separator")]
    [InlineData("--suffix=.x aXXXb", "mktemp: with --suffix, template ‘aXXXb’ must end in X")]
    [InlineData("aXXX bXXX", "mktemp: too many templates")]
    [InlineData("-Z", "mktemp: invalid option -- 'Z'")]
    public void Mktemp_BadTemplateOrOption_Exit1(string args, string message)
        => RunIn(Scratch(), "Invoke-BashMktemp " + args).AssertFailed(1, message);

    // -u: a name that does not exist, nothing created (it was a literal '-u' template).
    [SkippableFact]
    public void Mktemp_DryRun_CreatesNothing()
    {
        var dir = Scratch();
        var r = RunIn(dir, "Invoke-BashMktemp -u aXXXX");
        r.AssertSuccess();
        Assert.False(File.Exists(Path.Combine(dir, Assert.Single(r.Lines))));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    // Direct invocation of the colliding short flags (-d: -Debug, -p: -ProgressAction).
    [SkippableFact]
    public void Mktemp_DirectInvocation_DashDAndDashP()
    {
        var dir = Scratch();
        Directory.CreateDirectory(Path.Combine(dir, "p"));
        var r = RunIn(dir, "Invoke-BashMktemp -d -p p aXXX");
        r.AssertSuccess();
        var name = Assert.Single(r.Lines);
        Assert.Matches("^p/a[A-Za-z0-9]{3}$", name);
        Assert.True(Directory.Exists(Path.Combine(dir, name)));
    }

    [Theory]
    [InlineData(null, false, null, null, null, "D", "tmp.", 10)]
    [InlineData("a.XXX", false, null, null, "T", null, "a.", 3)]
    [InlineData("a.XXX", true, null, null, "T", "T", "a.", 3)]
    [InlineData("a.XXX", true, "P", null, "", "P", "a.", 3)]
    [InlineData("a.XXX", false, "", null, "", "D", "a.", 3)]
    [InlineData("aXXX", false, null, ".s", null, null, "a", 3)]
    public void Plan_IsGnuMktemp(string? template, bool t, string? dirArg, string? suffix, string? tmpdir, string? dir, string prefix, int xs)
    {
        var plan = InvokeBashMktempCommand.Plan(template, t, dirArg, suffix, tmpdir, "D");
        Assert.Null(plan.Error);
        Assert.Equal(dir, plan.Dir);
        Assert.Equal(prefix, plan.Template[..plan.XStart]);
        Assert.Equal(xs, plan.XCount);
        Assert.Equal(suffix ?? "", plan.Template[(plan.XStart + plan.XCount)..]);
    }
    [SkippableFact]
    public void Mktemp_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashMktemp --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("mktemp", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public void Mktemp_ViaAlias_Works()
    {
        var lines = RunLines("(mktemp).Path");
        Assert.Single(lines);
        Assert.True(File.Exists(lines[0]));
    }

    [SkippableFact]
    public void Mktemp_EmitsTypedMktempOutput()
    {
        // The PSObject's typename must be 'PsBash.MktempOutput' so any
        // ps1xml view or downstream consumer depending on the type marker
        // keeps working. Probe via PSTypeNames.
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(
            "(Invoke-BashMktemp).PSTypeNames -join ','").Invoke();
        Assert.Single(result);
        var types = result[0]?.ToString() ?? "";
        Assert.Contains("PsBash.MktempOutput", types);

        // Cleanup: also fetch and remove the created file.
        pwsh.Commands.Clear();
        var pathRes = pwsh.AddScript("(Invoke-BashMktemp).Path").Invoke();
        foreach (var r in pathRes)
        {
            var p = r?.ToString();
            if (p != null && File.Exists(p)) _createdPaths.Add(p);
        }
    }
}
