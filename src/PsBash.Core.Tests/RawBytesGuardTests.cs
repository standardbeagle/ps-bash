using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Keeps the byte model honest (docs/specs/runtime-functions.md "Raw bytes"): ps-bash DATA text is a string
/// that may carry escaped-byte markers, and every text-to-bytes / bytes-to-text step must go through
/// <see cref="RawBytes"/>. A bare <c>Encoding.UTF8.GetBytes</c> / <c>GetString</c> / <c>new UTF8Encoding</c>
/// turns a marker into U+FFFD (or, decoding, an invalid byte into U+FFFD) — silent data corruption at exit 0,
/// exactly what the codec exists to prevent. The scan covers the cmdlets (where data is produced and
/// consumed) and the IPC protocol; the allow-list names each remaining use and why it is not data.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent.
/// </summary>
public class RawBytesGuardTests
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

    private static readonly Regex Forbidden = new(
        @"Encoding\.UTF8\.(GetBytes|GetString|GetByteCount|GetChars)\(|new\s+(System\.Text\.)?UTF8Encoding\(|Encoding\.Latin1\.(GetBytes|GetString)|File\.(ReadAllText|WriteAllText|AppendAllText)\(",
        RegexOptions.Compiled);

    /// <summary>file name -> why its remaining use is not pipeline DATA.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["InvokeBashFileCommand.cs"] = "file(1) classification: a STRICT decoder that must throw on invalid UTF-8 to classify it",
        ["FileMagic.cs"] = "file(1) magic sniffing: header bytes decoded as Latin-1 / strict UTF-8 only to classify the file, never emitted as data",
        ["InvokeBashPsCommand.cs"] = "/proc command lines: process metadata, not data",
        ["InvokeBashSourceCommand.cs"] = "writes an empty snapshot placeholder (string.Empty)",
        ["BashFileSystem.cs"] = "UTF-16 BOM path of OpenDocumentReader (a BOM-declared UTF-16 file) and doc comments",
        ["InvokeBashTracerouteCommand.cs"] = "ICMP payload (ASCII)",
    };

    [Fact]
    public void Cmdlets_DoNotEncodeOrDecodeDataWithPlainUtf8()
    {
        var offenders = new List<string>();
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            var name = Path.GetFileName(file);
            if (Allowed.ContainsKey(name)) continue;

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)) continue;
                if (Forbidden.IsMatch(lines[i]))
                    offenders.Add($"{name}:{i + 1}: {t}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Data text<->bytes must go through PsBash.Core.RawBytes (GetBytes/GetString/GetByteCount/Encoding), " +
            "never plain UTF-8 or File.*Text — a marker would become U+FFFD. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void HostProtocol_UsesTheRawCodecForEveryPayload()
    {
        var file = Path.Combine(RepoRoot(), "src", "PsBash.Core", "Runtime", "Ipc", "HostProtocol.cs");
        var offenders = File.ReadAllLines(file)
            .Select((l, i) => (l, i))
            .Where(x => !x.l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !x.l.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Where(x => Regex.IsMatch(x.l, @"UTF8Encoding|Encoding\.UTF8\."))
            .Select(x => $"{x.i + 1}: {x.l.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0, "HostProtocol payloads must use RawBytes. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>file name -> why its remaining use is not data a script holds.</summary>
    private static readonly Dictionary<string, string> AllowedInSpawns = new(StringComparer.Ordinal)
    {
        ["UnixPtyAdapter.cs"] = "the pty slave's device name from ptsname(3): a path the kernel makes",
    };

    /// <summary>
    /// The argv, envp and paths a child process is started with are data too: a script's <c>"$s"</c> holding an invalid
    /// byte reaches the program as that byte. REGRESSION: <c>JobProcess.Posix</c> marshalled them with plain UTF-8,
    /// outside the cmdlet scan above, so <c>sh -c 'printf %s "$E"'</c> printed <c>ef bf bd</c> for <c>E=$'\xff'</c>.
    /// </summary>
    [Fact]
    public void ChildSpawns_MarshalArgumentsAndEnvironmentWithTheRawCodec()
    {
        var offenders = new List<string>();
        foreach (var rel in new[] { Path.Combine("src", "PsBash.Transpiler", "Processes"), Path.Combine("src", "PsBash.Shell", "Pty") })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), rel), "*.cs", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (AllowedInSpawns.ContainsKey(name)) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (Forbidden.IsMatch(lines[i]) || t.Contains("UnmanagedType.LPUTF8Str", StringComparison.Ordinal))
                        offenders.Add($"{name}:{i + 1}: {t}");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "A child's argv / envp / path must be marshalled with PsBash.Core.RawBytes.GetBytes. Offenders:\n  " +
            string.Join("\n  ", offenders));
        foreach (var name in AllowedInSpawns.Keys)
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "src", "PsBash.Shell", "Pty", name)), $"{name} is gone: drop it from the allow-list");
    }

    /// <summary>
    /// A foreground scalar is read and written through <see cref="ProcessScalars"/>, never the environment directly:
    /// .NET's environment on Linux turns a marker into U+FFFD, so <c>s=$'\xff'; printf %s "$s"</c> wrote <c>ef bf bd</c>.
    /// </summary>
    [Theory]
    [InlineData("src/PsBash.Cmdlets/BashVariableStore.cs")]
    [InlineData("src/PsBash.Transpiler/ScalarOverlay.cs")]
    [InlineData("src/PsBash.Cmdlets/JobEnvironmentProvider.cs")]
    public void ForegroundScalars_GoThroughProcessScalars(string relativePath)
    {
        var offenders = File.ReadAllLines(Path.Combine(RepoRoot(), relativePath))
            .Select((l, i) => (l, i))
            .Where(x => !x.l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(x => Regex.IsMatch(x.l, @"Environment\.(Get|Set)EnvironmentVariables?\("))
            .Select(x => $"{x.i + 1}: {x.l.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0, "Use ProcessScalars.Get / Set / Entries. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>file (path under src/) -> why its direct environment write cannot make a kept exact value wrong.</summary>
    private static readonly Dictionary<string, string> DirectEnvironmentWrites = new(StringComparer.Ordinal)
    {
        ["PsBash.Transpiler/ProcessScalars.cs"] = "the store itself",
        ["PsBash.Host/Program.cs"] = "host configuration at startup and at the interactive host's end ($PWD, PSBASH_INTERACTIVE, the "
            + "stateful switch): ASCII values outside any command, never one with an invalid byte",
        ["PsBash.Host/Runtime/SdkRunspace.cs"] = "host startup: the execution policy, before any shell variable is set",
        ["PsBash.Host/Runtime/SdkWorker.cs"] = "the reset's sweep: unsets only (an unset variable has no kept value to read)",
    };

    /// <summary>
    /// The process environment is written only through <see cref="ProcessScalars"/>. Its staleness check compares the
    /// environment's value with the stored form of the kept one, and that form cannot tell <c>\xff</c> from <c>\xfe</c>
    /// (both U+FFFD): a direct write of another invalid byte would read back as the old kept value. So nothing writes the
    /// environment around the store, in C# or in PowerShell text (module, emitted code), except the inventoried places.
    /// </summary>
    [Fact]
    public void TheEnvironment_IsWrittenOnlyThroughProcessScalars()
    {
        var root = Path.Combine(RepoRoot(), "src");
        var write = new Regex(@"SetEnvironmentVariable\s*\(|\[(System\.)?Environment\]::SetEnvironmentVariable");
        var offenders = new List<string>();
        foreach (var project in new[] { "PsBash.Cmdlets", "PsBash.Transpiler", "PsBash.Host", "PsBash.Core", "PsBash.Module" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*", SearchOption.AllDirectories))
            {
                if (!(file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".psm1", StringComparison.Ordinal)
                      || file.EndsWith(".ps1", StringComparison.Ordinal))) continue;
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal)) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("#", StringComparison.Ordinal)
                        || t.StartsWith("*", StringComparison.Ordinal)) continue;
                    if (!write.IsMatch(lines[i])) continue;
                    if (DirectEnvironmentWrites.ContainsKey(rel)
                        && (rel != "PsBash.Host/Runtime/SdkWorker.cs" || t.Contains(", null)", StringComparison.Ordinal))) continue;
                    offenders.Add($"{rel}:{i + 1}: {t}");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "Write a shell variable with ProcessScalars.Set (BashVariableStore.Set from a cmdlet): a direct environment write "
            + "can leave a kept exact value that no longer matches. Offenders:\n  " + string.Join("\n  ", offenders));
        foreach (var rel in DirectEnvironmentWrites.Keys)
            Assert.True(File.Exists(Path.Combine(root, rel)), $"{rel} is gone: drop it from the inventory");
    }

    [Fact]
    public void TheAllowList_OnlyNamesFilesThatStillExist()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var existing = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Assert.All(Allowed.Keys, k => Assert.Contains(k, existing));
    }
}
