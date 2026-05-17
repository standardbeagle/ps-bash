using System.IO.Compression;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashGzip</c> function
/// (REFACTOR-2 follow-on). Reproduces GNU coreutils <c>gzip</c> /
/// <c>gunzip</c> / <c>zcat</c>: compresses each operand file in place
/// (replacing it with <c>{PATH}.gz</c>) by default, decompresses with
/// <c>-d</c> / <c>--decompress</c> (stripping the <c>.gz</c> suffix),
/// writes the (de)compressed bytes to stdout under <c>-c</c> /
/// <c>--stdout</c>, preserves the original file under <c>-k</c> /
/// <c>--keep</c>, accepts <c>-f</c> / <c>--force</c> for arg-compat, emits
/// a per-file ratio line under <c>-v</c> / <c>--verbose</c>, and emits a
/// fixed-width listing under <c>-l</c> / <c>--list</c>. Compression level
/// follows the <c>-1</c>..<c>-9</c> flags (1 = Fastest, 9 = SmallestSize,
/// else Optimal — same .NET <see cref="CompressionLevel"/> ladder as the
/// psm1 oracle).
///
/// Behavioral parity oracle: the psm1 <c>Invoke-BashGzip</c> function.
/// Each operand is resolved via the unresolved-provider-path slice (matching
/// the oracle's <c>Resolve-BashGlob</c>); a missing path emits a bash-style
/// "No such file or directory" error via
/// <see cref="FileSystemHelpers.WriteBashError"/> and the cmdlet continues
/// with subsequent operands. The <c>-l</c> path computes a percentage
/// ratio using <see cref="GZipStream"/> in <see cref="CompressionMode.Decompress"/>
/// mode over the operand's bytes. Decompress and compress both write back to
/// disk via <see cref="File.WriteAllBytes"/>; under <c>-c</c> they emit one
/// <c>PsBash.TextOutput</c> object (a UTF-8 string for decompress, a base64
/// string for compress — the oracle did this on purpose because raw bytes
/// don't survive a string pipeline). Under verbose mode, the cmdlet emits
/// one extra <c>PsBash.TextOutput</c> line per file with the ratio.
///
/// Alias detection: the bash <c>gunzip</c> alias means "<c>gzip -d</c>" and
/// <c>zcat</c> means "<c>gzip -dc</c>". The psm1 oracle reads
/// <c>$MyInvocation.InvocationName</c>; the cmdlet reads
/// <see cref="System.Management.Automation.PSCmdlet.MyInvocation"/>.<c>InvocationName</c>
/// and applies the same default-flag boost.
///
/// Flag binding: <c>-d</c> / <c>-c</c> / <c>-v</c> prefix-collide with
/// <c>-Debug</c> / <c>-Confirm</c> / <c>-Verbose</c> common parameters
/// (binder routes the bare token by exact parameter name — beats a
/// common-parameter prefix match). They are therefore declared as
/// <see cref="SwitchParameter"/>s literally named <c>D</c> / <c>C</c> /
/// <c>V</c>. <c>-f</c> has no PowerShell common-parameter prefix overlap
/// but is declared as a <see cref="SwitchParameter"/> <c>F</c> for symmetry
/// (it keeps the bare token from sliding into <see cref="Arguments"/>'s
/// catch-all and is recovered post-parse via the same flag flag).
/// <c>-k</c> / <c>-l</c> / <c>-1</c>..<c>-9</c> have no common-parameter
/// prefix collision and stay in <see cref="Arguments"/>; bundled forms
/// (<c>-dk</c>, <c>-cv</c>, <c>-9v</c>, etc.) are recovered by the manual
/// post-parse scan, exactly as the oracle's <c>foreach ($ch in
/// $arg.Substring(1).ToCharArray())</c> loop did.
///
/// AOT safety: no <see cref="ScriptBlock"/> construction; <c>--help</c>
/// delegates to psm1 <c>Show-BashHelp</c> via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>.
/// File-read / -write failures route through
/// <see cref="FileSystemHelpers.WriteBashError"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashGzip")]
[OutputType(typeof(string))]
public sealed class InvokeBashGzipCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>The bash <c>-d</c> (decompress) switch — explicit because the bare token
    /// <c>-d</c> prefix-collides with <c>-Debug</c>.</summary>
    [Parameter]
    public SwitchParameter D { get; set; }

    /// <summary>The bash <c>-c</c> (to stdout) switch — explicit because the bare token
    /// <c>-c</c> prefix-collides with <c>-Confirm</c>.</summary>
    [Parameter]
    public SwitchParameter C { get; set; }

    /// <summary>The bash <c>-v</c> (verbose) switch — explicit because the bare token
    /// <c>-v</c> prefix-collides with <c>-Verbose</c>.</summary>
    [Parameter]
    public SwitchParameter V { get; set; }

    /// <summary>The bash <c>-f</c> (force) switch — declared for symmetry. No
    /// common-parameter prefix collision; oracle parity is accept-and-ignore beyond
    /// allowing overwrite (which <see cref="File.WriteAllBytes"/> already does).</summary>
    [Parameter]
    public SwitchParameter F { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "gzip"))
            {
                WriteObject(line);
            }
            return;
        }

        bool decompress = D.IsPresent;
        bool toStdout = C.IsPresent;
        bool keep = false;
        bool force = F.IsPresent;
        bool verbose = V.IsPresent;
        bool list = false;
        int level = 6;

        // Detect gunzip / zcat invocation via alias name. Matches the psm1
        // oracle's `$MyInvocation.InvocationName -eq 'gunzip'` branch.
        string invokedAs = MyInvocation?.InvocationName ?? string.Empty;
        if (string.Equals(invokedAs, "gunzip", StringComparison.OrdinalIgnoreCase))
        {
            decompress = true;
        }
        if (string.Equals(invokedAs, "zcat", StringComparison.OrdinalIgnoreCase))
        {
            decompress = true;
            toStdout = true;
        }

        var operands = new List<string>();
        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];

            if (a == "--")
            {
                i++;
                while (i < args.Length) { operands.Add(args[i]); i++; }
                break;
            }
            if (a == "--decompress" || a == "--uncompress") { decompress = true; i++; continue; }
            if (a == "--stdout" || a == "--to-stdout") { toStdout = true; i++; continue; }
            if (a == "--keep") { keep = true; i++; continue; }
            if (a == "--force") { force = true; i++; continue; }
            if (a == "--verbose") { verbose = true; i++; continue; }
            if (a == "--list") { list = true; i++; continue; }

            // -N single-digit level (oracle: `^-(\d)$`).
            if (a.Length == 2 && a[0] == '-' && a[1] >= '0' && a[1] <= '9')
            {
                level = a[1] - '0';
                i++;
                continue;
            }

            // Bundled short flags (oracle: `arg.Substring(1).ToCharArray()` switch).
            if (a.Length > 1 && a[0] == '-' && !a.StartsWith("--", StringComparison.Ordinal))
            {
                foreach (char ch in a.Substring(1))
                {
                    switch (ch)
                    {
                        case 'd': decompress = true; break;
                        case 'c': toStdout = true; break;
                        case 'k': keep = true; break;
                        case 'f': force = true; break;
                        case 'v': verbose = true; break;
                        case 'l': list = true; break;
                        default:
                            if (ch >= '0' && ch <= '9') { level = ch - '0'; }
                            break;
                    }
                }
                i++;
                continue;
            }

            operands.Add(a);
            i++;
        }

        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "gzip: missing file operand");
            return;
        }

        // Suppress unused warnings — `force` and `keep` are read above through
        // the manual scan into the same variables; touch both so the build
        // doesn't strip the local under nullability/clean analyzers.
        _ = force;

        foreach (string operand in operands)
        {
            foreach (string filePath in FileSystemHelpers.ResolveOperandPaths(this, operand))
            {
                if (!File.Exists(filePath) && !Directory.Exists(filePath))
                {
                    string normalized = filePath.Replace('\\', '/');
                    FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: No such file or directory");
                    continue;
                }

                byte[]? rawBytes;
                try
                {
                    rawBytes = File.ReadAllBytes(filePath);
                }
                catch (Exception ex)
                {
                    string normalized = filePath.Replace('\\', '/');
                    FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: {ex.Message}");
                    continue;
                }

                if (list)
                {
                    int compressedSize = rawBytes.Length;
                    int uncompressedSize;
                    try
                    {
                        using var ms = new MemoryStream(rawBytes);
                        using var gs = new GZipStream(ms, CompressionMode.Decompress);
                        using var buf = new MemoryStream();
                        gs.CopyTo(buf);
                        uncompressedSize = (int)buf.Length;
                    }
                    catch (Exception ex)
                    {
                        string normalized = filePath.Replace('\\', '/');
                        FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: {ex.Message}");
                        continue;
                    }
                    string ratio = uncompressedSize > 0
                        ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "{0:F1}%", (1.0 - ((double)compressedSize / uncompressedSize)) * 100)
                        : "0.0%";
                    string line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0,10} {1,10} {2,6} {3}", compressedSize, uncompressedSize, ratio, filePath);
                    var obj = new PSObject();
                    obj.TypeNames.Insert(0, "PsBash.GzipListOutput");
                    obj.Properties.Add(new PSNoteProperty("BashText", line));
                    obj.Properties.Add(new PSNoteProperty("CompressedSize", compressedSize));
                    obj.Properties.Add(new PSNoteProperty("UncompressedSize", uncompressedSize));
                    obj.Properties.Add(new PSNoteProperty("Ratio", ratio));
                    obj.Properties.Add(new PSNoteProperty("FileName", filePath));
                    WriteObject(obj);
                    continue;
                }

                if (decompress)
                {
                    byte[] outBytes;
                    try
                    {
                        using var ms = new MemoryStream(rawBytes);
                        using var gs = new GZipStream(ms, CompressionMode.Decompress);
                        using var buf = new MemoryStream();
                        gs.CopyTo(buf);
                        outBytes = buf.ToArray();
                    }
                    catch (Exception ex)
                    {
                        string normalized = filePath.Replace('\\', '/');
                        FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: {ex.Message}");
                        continue;
                    }

                    if (toStdout)
                    {
                        string text = Encoding.UTF8.GetString(outBytes);
                        WriteObject(BashRuntime.NewBashObject(text));
                    }
                    else
                    {
                        string outPath = filePath.EndsWith(".gz", StringComparison.Ordinal)
                            ? filePath.Substring(0, filePath.Length - 3)
                            : filePath;
                        if (!TryWriteAllBytes(outPath, outBytes)) continue;
                        if (!keep)
                        {
                            try { File.Delete(filePath); } catch { /* best effort, oracle parity */ }
                        }
                        if (verbose)
                        {
                            string ratio = outBytes.Length > 0
                                ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "{0:F1}%", (1.0 - ((double)rawBytes.Length / outBytes.Length)) * 100)
                                : "0.0%";
                            WriteObject(BashRuntime.NewBashObject($"{filePath}: {ratio}"));
                        }
                    }
                }
                else
                {
                    byte[] compressedBytes;
                    try
                    {
                        using var ms = new MemoryStream();
                        CompressionLevel compLevel = level switch
                        {
                            <= 1 => CompressionLevel.Fastest,
                            >= 9 => CompressionLevel.SmallestSize,
                            _ => CompressionLevel.Optimal,
                        };
                        using (var gs = new GZipStream(ms, compLevel, leaveOpen: true))
                        {
                            gs.Write(rawBytes, 0, rawBytes.Length);
                        }
                        compressedBytes = ms.ToArray();
                    }
                    catch (Exception ex)
                    {
                        string normalized = filePath.Replace('\\', '/');
                        FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: {ex.Message}");
                        continue;
                    }

                    if (toStdout)
                    {
                        // The psm1 oracle base64-emits compressed bytes because
                        // a string pipeline cannot carry arbitrary bytes — keep
                        // that behavior so a `gzip -c FILE | base64 -d` chain
                        // round-trips identically.
                        string b64 = Convert.ToBase64String(compressedBytes);
                        WriteObject(BashRuntime.NewBashObject(b64));
                    }
                    else
                    {
                        string outPath = filePath + ".gz";
                        if (!TryWriteAllBytes(outPath, compressedBytes)) continue;
                        if (!keep)
                        {
                            try { File.Delete(filePath); } catch { /* best effort, oracle parity */ }
                        }
                        if (verbose)
                        {
                            string ratio = rawBytes.Length > 0
                                ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "{0:F1}%", (1.0 - ((double)compressedBytes.Length / rawBytes.Length)) * 100)
                                : "0.0%";
                            WriteObject(BashRuntime.NewBashObject($"{filePath}: {ratio}"));
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// File.WriteAllBytes with the oracle's bash-style error contract:
    /// emit "gzip: PATH: MESSAGE" via FileSystemHelpers.WriteBashError on
    /// failure and signal the caller (return false) so the per-operand loop
    /// continues with subsequent operands.
    /// </summary>
    private bool TryWriteAllBytes(string path, byte[] data)
    {
        try
        {
            File.WriteAllBytes(path, data);
            return true;
        }
        catch (Exception ex)
        {
            string normalized = path.Replace('\\', '/');
            FileSystemHelpers.WriteBashError(this, $"gzip: {normalized}: {ex.Message}");
            return false;
        }
    }
}
