using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the stand-in argv invariant (<see cref="PsBuild.ArgvLiterals"/>): PowerShell code that receives a bash
/// command's arguments as <c>$args</c> passes them on as literal strings, never by splatting <c>$args</c> itself.
/// <para>
/// The bug class: a splatted <c>$args</c> re-creates every element that was typed as a parameter token, so the callee's
/// binder took it. <c>perl -e '…' &amp;</c> in a background job (JobProgramLookup's stand-in into Invoke-BashNative)
/// failed with "parameter name 'e' is ambiguous"; <c>./x.sh -e</c> dropped the <c>-e</c>. The emitter half
/// (<c>--</c> and <c>-x:y</c>, which never reach <c>$args</c> intact) is pinned by <c>PsEmitterDashOperandTests</c>, the
/// behavior against bash by <c>ScriptFileExecutionDifferentialTests</c> and <c>BackgroundJobLaunchValuesDifferentialTests</c>.
/// </para>
/// <para>
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check is the
/// justified oracle.
/// </para>
/// </summary>
public class StandInArgvGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    private static readonly string[] ProductProjects =
        ["PsBash.Cmdlets", "PsBash.Host", "PsBash.Core", "PsBash.Transpiler", "PsBash.Shell", "PsBash.Module"];

    /// <summary>
    /// Splats of <c>$args</c> that are meant to keep PowerShell's parameter syntax, with the reason. Keyed by file name.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["SdkRunspaceSetup.ps1"] = "the autoload wrapper runs a PowerShell cmdlet the script named (`tnc -Port 80`): "
                                   + "its parameter tokens are that cmdlet's syntax",
    };

    private static readonly Regex ArgsSplat = new(@"@args\b", RegexOptions.Compiled);

    [Fact]
    public void NoCodePassesOnArgumentsBySplattingArgs()
    {
        var offenders = new List<string>();
        foreach (var project in ProductProjects)
        {
            string root = Path.Combine(RepoRoot(), "src", project);
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (ext is not (".cs" or ".ps1" or ".psm1")) continue;
                string rel = Path.GetRelativePath(root, file);
                if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                if (Allowed.ContainsKey(Path.GetFileName(file))) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('#')) continue;
                    if (ArgsSplat.IsMatch(lines[i])) offenders.Add($"{project}/{rel}:{i + 1}: {trimmed}");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "Pass a stand-in's arguments on through PsBuild.ArgvLiterals + PsBuild.ArgvSplat, never @args "
            + "(a splatted $args re-binds `-e` to the callee's parameters):\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void ArgvLiterals_IsAPlainStringCopy_AndArgvSplatNamesIt()
    {
        Assert.Equal("$__psbash_argv = [string[]]$args", PsBuild.ArgvLiterals);
        Assert.Equal("@__psbash_argv", PsBuild.ArgvSplat);
    }

    [Fact]
    public void ProgramFileStandIn_PassesArgumentsAsLiterals()
    {
        var launch = ProgramFile.FromShebang("/t/x.sh", "/bin/sh");
        var script = launch.StandInScript("./x.sh");
        Assert.StartsWith(PsBuild.ArgvLiterals + "; ", script, StringComparison.Ordinal);
        Assert.Contains(PsBuild.ArgvSplat, script, StringComparison.Ordinal);
        Assert.DoesNotMatch(ArgsSplat, script);
    }
}
