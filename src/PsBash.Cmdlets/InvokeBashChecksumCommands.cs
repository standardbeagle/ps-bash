using System.Management.Automation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Shared file-hashing engine for the md5sum / sha1sum / sha256sum binary
/// cmdlets (REFACTOR-2). Reimplements the psm1 <c>Invoke-BashChecksum</c>
/// helper in C#:
/// <list type="bullet">
/// <item>File mode (one or more operands): hash each file's bytes, emit a
/// typed <c>PsBash.TextOutput</c> PSObject per file with
/// <c>BashText = "&lt;hex&gt;  &lt;path&gt;"</c> and side properties
/// <c>Hash</c> / <c>FileName</c> / <c>Algorithm</c>. Missing files emit a
/// bash-style error via psm1 <c>Write-BashError</c> and continue.</item>
/// <item>Pipeline mode (no operands): concatenate every upstream item's
/// BashText with <c>\n</c> separators plus a final <c>\n</c>, hash the
/// UTF-8 bytes, emit a single PSObject with <c>FileName = "-"</c>.</item>
/// </list>
/// The hex form is lowercase with no separator (matching GNU coreutils).
/// Glob expansion of operands uses
/// <see cref="PSCmdlet.SessionState"/>.<c>Path.GetResolvedProviderPathFromPSPath</c>
/// — the same slice <c>InvokeBashCatCommand</c> reimplements in C# — so we
/// stay off the psm1 <c>Resolve-BashGlob</c> dependency on the hot path.
/// </summary>
internal static class ChecksumEngine
{
    public static void Run(
        PSCmdlet cmdlet,
        HashAlgorithmName algorithmName,
        string algorithmLabel,
        string commandName,
        string[] arguments,
        IList<PSObject>? pipelineInput,
        bool checkMode = false)
    {
        FileSystemHelpers.SetLastExitCode(cmdlet, 0);
        if (FileSystemHelpers.TryHandleVersion(cmdlet, commandName, arguments)) return;
        if (Array.IndexOf(arguments, "--help") >= 0)
        {
            foreach (var line in cmdlet.InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", commandName))
            {
                cmdlet.WriteObject(line);
            }
            return;
        }

        // Check mode (-c / --check): verify the named files against a checksum
        // file. `-c` prefix-collides with -Confirm, so each cmdlet passes
        // checkMode via a `C` decoy; `--check` arrives as an operand.
        if (checkMode || Array.IndexOf(arguments, "--check") >= 0)
        {
            RunCheck(cmdlet, algorithmName, commandName, arguments);
            return;
        }

        // Separate flags from file operands. -b/--binary and -t/--text are the
        // GNU mode tags; previously they fell through as bogus filenames
        // ("No such file"). Binary mode changes the output marker from two
        // spaces to " *" (GNU md5sum convention). `--` ends flag parsing.
        var operands = new List<string>();
        bool binary = false;
        bool pastDoubleDash = false;
        foreach (var a in arguments)
        {
            if (!pastDoubleDash)
            {
                if (a == "--") { pastDoubleDash = true; continue; }
                if (a == "-b" || a == "--binary") { binary = true; continue; }
                if (a == "-t" || a == "--text") { binary = false; continue; }
            }
            operands.Add(a);
        }
        string marker = binary ? " *" : "  ";

        using var hasher = IncrementalHash.CreateHash(algorithmName);

        if (operands.Count > 0)
        {
            foreach (var rawPath in operands)
            {
                foreach (var filePath in ResolveOperandPaths(cmdlet, rawPath))
                {
                    if (!File.Exists(filePath))
                    {
                        FileSystemHelpers.WriteBashError(cmdlet, $"{commandName}: {filePath}: No such file or directory");
                        continue;
                    }

                    string hex;
                    try
                    {
                        // Stream-hash in chunks — never load the whole file. A
                        // checksum of a multi-GB file runs in ~80 KB of memory.
                        using var s = BashFileSystem.OpenRead(filePath);
                        hex = ComputeHexFromStream(algorithmName, s);
                    }
                    catch (Exception ex)
                    {
                        if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                        FileSystemHelpers.WriteBashError(cmdlet, $"{commandName}: {filePath}: {ex.Message}");
                        continue;
                    }

                    cmdlet.WriteObject(MakeOutput(hex, filePath, algorithmLabel, marker));
                }
            }
            return;
        }

        if (pipelineInput is { Count: > 0 })
        {
            var sb = new StringBuilder();
            foreach (var item in pipelineInput)
            {
                sb.Append(BashRuntime.GetBashText(item));
                sb.Append('\n');
            }
            var hex = ComputeHex(algorithmName, Encoding.UTF8.GetBytes(sb.ToString()));
            cmdlet.WriteObject(MakeOutput(hex, "-", algorithmLabel, marker));
        }
    }

    /// <summary>
    /// <c>-c</c> / <c>--check</c>: read checksum file(s) (lines of
    /// <c>HASH  FILENAME</c> or <c>HASH *FILENAME</c>), recompute each named
    /// file's digest, and report <c>FILENAME: OK</c> / <c>FILENAME: FAILED</c>.
    /// <c>--status</c> suppresses all output (exit code only); <c>--quiet</c>
    /// prints only failures. Exit 1 if any line fails or a file is missing.
    /// </summary>
    private static void RunCheck(
        PSCmdlet cmdlet, HashAlgorithmName algorithmName, string commandName, string[] arguments)
    {
        bool status = false, quiet = false, pastDoubleDash = false;
        var checkFiles = new List<string>();
        foreach (var a in arguments)
        {
            if (!pastDoubleDash)
            {
                if (a == "--") { pastDoubleDash = true; continue; }
                if (a == "-c" || a == "--check") continue;
                if (a == "--status") { status = true; continue; }
                if (a == "--quiet") { quiet = true; continue; }
                if (a == "--warn" || a == "--strict" || a == "--ignore-missing") continue;
                if (a == "-b" || a == "--binary" || a == "-t" || a == "--text") continue;
            }
            checkFiles.Add(a);
        }

        int failures = 0;
        var lineRx = new Regex(@"^([0-9A-Fa-f]+)[ \t]+\*?(.+)$");

        foreach (var cf in checkFiles)
        {
            foreach (var checkPath in ResolveOperandPaths(cmdlet, cf))
            {
                if (!File.Exists(checkPath))
                {
                    FileSystemHelpers.WriteBashError(cmdlet, $"{commandName}: {checkPath}: No such file or directory");
                    failures++;
                    continue;
                }

                List<string> lines;
                try { lines = new List<string>(BashFileSystem.ReadLines(checkPath)); }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    FileSystemHelpers.WriteBashError(cmdlet, $"{commandName}: {checkPath}: {ex.Message}");
                    failures++;
                    continue;
                }

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var m = lineRx.Match(line.Trim());
                    if (!m.Success) continue;

                    string expected = m.Groups[1].Value;
                    string fname = m.Groups[2].Value;
                    string fpath;
                    try { fpath = cmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath(fname); }
                    catch { fpath = fname; }

                    if (!File.Exists(fpath))
                    {
                        if (!status) cmdlet.WriteObject(BashRuntime.NewBashObject($"{fname}: FAILED open or read"));
                        failures++;
                        continue;
                    }

                    string actual;
                    try
                    {
                        using var s = BashFileSystem.OpenRead(fpath);
                        actual = ComputeHexFromStream(algorithmName, s);
                    }
                    catch (Exception ex)
                    {
                        if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                        if (!status) cmdlet.WriteObject(BashRuntime.NewBashObject($"{fname}: FAILED open or read"));
                        failures++;
                        continue;
                    }

                    if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!status && !quiet) cmdlet.WriteObject(BashRuntime.NewBashObject($"{fname}: OK"));
                    }
                    else
                    {
                        if (!status) cmdlet.WriteObject(BashRuntime.NewBashObject($"{fname}: FAILED"));
                        failures++;
                    }
                }
            }
        }

        if (failures > 0)
        {
            if (!status)
            {
                FileSystemHelpers.WriteBashError(cmdlet,
                    $"{commandName}: WARNING: {failures} computed checksum(s) did NOT match");
            }
            FileSystemHelpers.SetLastExitCode(cmdlet, 1);
        }
        else
        {
            FileSystemHelpers.SetLastExitCode(cmdlet, 0);
        }
    }

    private static IEnumerable<string> ResolveOperandPaths(PSCmdlet cmdlet, string raw)
    {
        raw = FileSystemHelpers.NormalizeOperandPath(raw);
        // Glob slice mirrors InvokeBashCatCommand: '*'/'?' triggers
        // SessionState resolution; literal paths fall through unchanged so a
        // bash-style "no such file" error can be emitted by the caller.
        if (raw.IndexOf('*') < 0 && raw.IndexOf('?') < 0)
        {
            yield return cmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath(raw);
            yield break;
        }

        var matched = new List<string>();
        try
        {
            foreach (var resolved in cmdlet.SessionState.Path
                         .GetResolvedProviderPathFromPSPath(raw, out _))
            {
                matched.Add(resolved);
            }
        }
        catch
        {
            // No matches — fall through to literal passthrough (the caller
            // will report the missing file).
        }

        if (matched.Count == 0)
        {
            yield return raw;
        }
        else
        {
            foreach (var m in matched) yield return m;
        }
    }

    private static string ComputeHex(HashAlgorithmName name, byte[] bytes)
    {
        // IncrementalHash is per-call here because the API is convenient and
        // each invocation is independent. For a long-lived hasher we'd reuse,
        // but each checksum operation hashes one file's bytes in one shot.
        using var hasher = IncrementalHash.CreateHash(name);
        hasher.AppendData(bytes);
        var hashBytes = hasher.GetHashAndReset();
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Hash a stream in 80 KB chunks — the streaming counterpart of
    /// <see cref="ComputeHex"/> for file input, so a multi-gigabyte file is never
    /// loaded into memory.
    /// </summary>
    private static string ComputeHexFromStream(HashAlgorithmName name, Stream stream)
    {
        using var hasher = IncrementalHash.CreateHash(name);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static PSObject MakeOutput(string hex, string fileName, string algorithmLabel, string marker = "  ")
    {
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.TextOutput");
        obj.Properties.Add(new PSNoteProperty("BashText", $"{hex}{marker}{fileName}"));
        obj.Properties.Add(new PSNoteProperty("Hash", hex));
        obj.Properties.Add(new PSNoteProperty("FileName", fileName));
        obj.Properties.Add(new PSNoteProperty("Algorithm", algorithmLabel));
        return obj;
    }
}

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashMd5sum</c>
/// (REFACTOR-2). MD5 file checksum, matching the GNU coreutils <c>md5sum</c>
/// output shape: <c>&lt;hex&gt;  &lt;path&gt;</c> per file, or
/// <c>&lt;hex&gt;  -</c> in pipeline mode. Delegates to
/// <see cref="ChecksumEngine.Run"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashMd5sum")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashMd5sumCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Bash <c>-c</c> (check). Decoy — prefix-collides with <c>-Confirm</c>.</summary>
    [Parameter] public SwitchParameter C { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null) _pipeline.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        ChecksumEngine.Run(
            this, HashAlgorithmName.MD5, "MD5", "md5sum",
            Arguments ?? Array.Empty<string>(), _pipeline, C.IsPresent);
    }
}

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashSha1sum</c>
/// (REFACTOR-2). SHA-1 file checksum. Delegates to
/// <see cref="ChecksumEngine.Run"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashSha1sum")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashSha1sumCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Bash <c>-c</c> (check). Decoy — prefix-collides with <c>-Confirm</c>.</summary>
    [Parameter] public SwitchParameter C { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null) _pipeline.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        ChecksumEngine.Run(
            this, HashAlgorithmName.SHA1, "SHA1", "sha1sum",
            Arguments ?? Array.Empty<string>(), _pipeline, C.IsPresent);
    }
}

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashSha256sum</c>
/// (REFACTOR-2). SHA-256 file checksum. Delegates to
/// <see cref="ChecksumEngine.Run"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashSha256sum")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashSha256sumCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Bash <c>-c</c> (check). Decoy — prefix-collides with <c>-Confirm</c>.</summary>
    [Parameter] public SwitchParameter C { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null) _pipeline.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        ChecksumEngine.Run(
            this, HashAlgorithmName.SHA256, "SHA256", "sha256sum",
            Arguments ?? Array.Empty<string>(), _pipeline, C.IsPresent);
    }
}
