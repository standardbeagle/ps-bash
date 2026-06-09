using System.Management.Automation;
using System.Security.Cryptography;
using System.Text;

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

        // Check mode (-c / --check): verify a checksum file. NOT implemented by
        // the binary cmdlet. `-c` prefix-collides with the -Confirm common
        // parameter, so each cmdlet declares a `C` decoy and passes checkMode
        // here; `--check` (no collision) arrives as an operand. Emit the
        // policy-compliant "recognized but not supported" error instead of
        // silently hashing the checksum file as if it were data.
        if (checkMode || Array.IndexOf(arguments, "--check") >= 0)
        {
            FileSystemHelpers.WriteBashError(
                cmdlet,
                $"{commandName}: option '-c' (--check) is recognized but not supported by ps-bash");
            FileSystemHelpers.SetLastExitCode(cmdlet, 1);
            return;
        }

        var operands = new List<string>();
        foreach (var a in arguments)
        {
            operands.Add(a);
        }

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

                    cmdlet.WriteObject(MakeOutput(hex, filePath, algorithmLabel));
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
            cmdlet.WriteObject(MakeOutput(hex, "-", algorithmLabel));
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

    private static PSObject MakeOutput(string hex, string fileName, string algorithmLabel)
    {
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.TextOutput");
        obj.Properties.Add(new PSNoteProperty("BashText", $"{hex}  {fileName}"));
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
