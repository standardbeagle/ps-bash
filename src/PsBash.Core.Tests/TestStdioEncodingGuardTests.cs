using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the test-harness invariant: a test that redirects a child's stdout or stderr names the encoding it decodes
/// with (UTF-8: ps-bash and bash write it). Left unset, .NET on Windows decodes with the test host's CONSOLE code page,
/// so the same correct run passed on a 65001 fleet host and failed under a 437 console (<c>é</c> read back as
/// <c>├⌐</c>: <c>AssociativeArrayTests.Order_NonAsciiKeys</c>). <c>PsBash.Testing.ProcessSpawn</c> sets it for every
/// run it starts; a hand-built <c>ProcessStartInfo</c> sets it beside its redirect. A helper that is handed a start
/// info defaults it with <c>??=</c>, never <c>=</c>: the caller may have chosen another (Latin-1 for byte-exact tests).
/// <para>Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check.</para>
/// </summary>
public class TestStdioEncodingGuardTests
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

    [Fact]
    public void EveryRedirectedChildStreamNamesItsEncoding()
    {
        var offenders = new List<string>();
        foreach (var project in Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "src")))
        {
            string name = Path.GetFileName(project);
            if (!name.EndsWith(".Tests", StringComparison.Ordinal) && name != "PsBash.Testing") continue;
            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(project, file);
                if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                string text = File.ReadAllText(file);
                foreach (var stream in new[] { "Output", "Error" })
                {
                    int redirects = Regex.Matches(text, $@"RedirectStandard{stream}\s*=\s*true").Count;
                    int encodings = Regex.Matches(text, $@"Standard{stream}Encoding\s*(\?\?)?=").Count;
                    if (redirects > encodings)
                        offenders.Add($"{name}/{rel}: {redirects} RedirectStandard{stream}, {encodings} Standard{stream}Encoding");
                }
                // A start info that already exists (a helper's parameter) may carry the caller's choice: the byte-exact
                // tests decode as Latin-1. Defaulting it to UTF-8 with `=` overwrote that (RawBytesEndToEndTests: 0xE9
                // came back as `?`, Windows fleet s22e); the default goes in with `??=`.
                foreach (Match m in Regex.Matches(text, @"\.\s*Standard(Output|Error|Input)Encoding\s*=\s*[^;=]*(UTF8|Utf8)[^;]*;"))
                    offenders.Add($"{name}/{rel}: `{m.Value.Trim()}` overwrites the caller's encoding; default it with ??=");
            }
        }
        Assert.True(offenders.Count == 0,
            "Set StandardOutputEncoding / StandardErrorEncoding (UTF-8) beside each redirect, or start the child through "
            + "PsBash.Testing.ProcessSpawn:\n" + string.Join('\n', offenders));
    }
}
