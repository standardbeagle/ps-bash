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

    [Fact]
    public void TheAllowList_OnlyNamesFilesThatStillExist()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var existing = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Assert.All(Allowed.Keys, k => Assert.Contains(k, existing));
    }
}
