using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashFile</c> function
/// (REFACTOR-2 follow-on). Detects the type of each operand file via a small
/// magic-byte table (PNG, JPEG, PDF, Zip, ELF, GIF, RIFF) plus an
/// ASCII-text / data fallback derived from a control-byte scan of the full
/// content, matching GNU coreutils <c>file</c> behavior as implemented by the
/// psm1 oracle.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet
/// reproduces its exact behavior:
/// <list type="bullet">
/// <item>Reads the first 16 bytes of each operand for magic-byte detection.
/// On a magic-byte match (PNG/JPEG/PDF/Zip/ELF/GIF/RIFF) emits the matching
/// type description (and MIME type with <c>-i</c>).</item>
/// <item>On no magic-byte match, reads the full file and scans every byte:
/// bytes &lt; 0x07 or in [0x0E..0x1F] excluding 0x1B (ESC) mark the file as
/// non-text. All-text → "ASCII text" (<c>text/plain</c>); else → "data"
/// (<c>application/octet-stream</c>). This is the psm1 oracle's exact rule.</item>
/// <item><c>-b</c> brief: emits just the type description without the
/// <c>PATH: </c> prefix.</item>
/// <item><c>-i</c> / <c>--mime</c>: emits MIME type instead of the type
/// description.</item>
/// <item><c>-L</c> / <c>--dereference</c>: accepted (silently — psm1 oracle
/// follows symlinks by default via <see cref="File.OpenRead"/>; the flag is a
/// no-op there).</item>
/// <item>Missing operands emit a bash-style <c>file: cannot open 'PATH' (No
/// such file or directory)</c> error via psm1 <c>Write-BashError</c>
/// (parameter-bound <see cref="CommandInvocationIntrinsics.InvokeScript(string,object[])"/>,
/// AOT-safe) and continue.</item>
/// </list>
///
/// Output: typed <c>PsBash.TextOutput</c> PSObject with <c>BashText</c>,
/// <c>FileName</c>, <c>FileType</c>, and <c>MimeType</c> note properties,
/// matching the psm1 oracle's <c>[PSCustomObject]</c> shape.
///
/// Common-parameter collisions (per the playbook table): <c>-i</c>
/// prefix-collides with <c>-InformationAction</c> / <c>-InformationVariable</c>,
/// declared here as an explicit <see cref="SwitchParameter"/>. <c>-b</c> and
/// <c>-L</c> have no prefix collision and stay in <see cref="Arguments"/>.
///
/// psm1-only dependencies: glob expansion via
/// <see cref="FileSystemHelpers.ResolveOperandPaths"/> (same slice
/// cat/strings/checksum use); <c>--help</c> delegates to psm1
/// <c>Show-BashHelp</c>; bash-style errors delegate to psm1
/// <c>Write-BashError</c> via
/// <see cref="FileSystemHelpers.WriteBashError"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFile")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashFileCommand : PSCmdlet
{
    /// <summary>
    /// GNU file's <c>-i</c> (<c>--mime</c>). <c>-i</c> prefix-collides with
    /// <c>-InformationAction</c> / <c>-InformationVariable</c>, so it MUST be
    /// declared as an explicit <see cref="SwitchParameter"/>.
    /// </summary>
    [Parameter]
    public SwitchParameter i { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "file", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "file"))
            {
                WriteObject(line);
            }
            return;
        }

        bool brief = false;
        bool mime = i.IsPresent;
        var operands = new List<string>();

        // psm1 oracle: -ceq case-sensitive for short flags, -eq for long
        // forms. We mirror that exactly via StringComparer.Ordinal.
        for (int k = 0; k < args.Length; k++)
        {
            var arg = args[k];
            if (string.Equals(arg, "-b", StringComparison.Ordinal)
                || string.Equals(arg, "--brief", StringComparison.Ordinal))
            {
                brief = true;
                continue;
            }
            if (string.Equals(arg, "-i", StringComparison.Ordinal)
                || string.Equals(arg, "--mime", StringComparison.Ordinal))
            {
                // -i may also arrive as the declared SwitchParameter above;
                // accept the literal token form for parity with the oracle.
                mime = true;
                continue;
            }
            if (string.Equals(arg, "-L", StringComparison.Ordinal)
                || string.Equals(arg, "--dereference", StringComparison.Ordinal))
            {
                // psm1 oracle: silently consumed (follow-symlinks is the
                // default behavior of File.OpenRead).
                continue;
            }
            operands.Add(arg);
        }

        foreach (var raw in operands)
        {
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                if (!File.Exists(filePath) && !Directory.Exists(filePath))
                {
                    // Match the psm1 oracle's "cannot open" wording exactly.
                    // The error path uses the resolved-but-missing path
                    // string, which on Windows may contain backslashes; the
                    // psm1 oracle did not normalize, so neither do we.
                    FileSystemHelpers.WriteBashError(
                        this,
                        $"file: cannot open '{filePath}' (No such file or directory)");
                    continue;
                }

                byte[] headBytes;
                try
                {
                    using var stream = BashFileSystem.OpenRead(filePath);
                    var buf = new byte[16];
                    int read = stream.Read(buf, 0, 16);
                    if (read <= 0)
                    {
                        headBytes = Array.Empty<byte>();
                    }
                    else if (read == 16)
                    {
                        headBytes = buf;
                    }
                    else
                    {
                        headBytes = new byte[read];
                        Array.Copy(buf, headBytes, read);
                    }
                }
                catch
                {
                    // psm1 oracle: catch -> $bytes = @() then fall through
                    // to the ReadAllBytes content scan path. We mirror that.
                    headBytes = Array.Empty<byte>();
                }

                string? fileType = null;
                string mimeType = "application/octet-stream";

                if (headBytes.Length >= 8
                    && headBytes[0] == 0x89 && headBytes[1] == 0x50
                    && headBytes[2] == 0x4E && headBytes[3] == 0x47)
                {
                    fileType = "PNG image data";
                    mimeType = "image/png";
                }
                else if (headBytes.Length >= 2
                    && headBytes[0] == 0xFF && headBytes[1] == 0xD8)
                {
                    fileType = "JPEG image data";
                    mimeType = "image/jpeg";
                }
                else if (headBytes.Length >= 4
                    && headBytes[0] == 0x25 && headBytes[1] == 0x50
                    && headBytes[2] == 0x44 && headBytes[3] == 0x46)
                {
                    fileType = "PDF document";
                    mimeType = "application/pdf";
                }
                else if (headBytes.Length >= 4
                    && headBytes[0] == 0x50 && headBytes[1] == 0x4B
                    && headBytes[2] == 0x03 && headBytes[3] == 0x04)
                {
                    fileType = "Zip archive data";
                    mimeType = "application/zip";
                }
                else if (headBytes.Length >= 4
                    && headBytes[0] == 0x7F && headBytes[1] == 0x45
                    && headBytes[2] == 0x4C && headBytes[3] == 0x46)
                {
                    fileType = "ELF executable";
                    mimeType = "application/x-executable";
                }
                else if (headBytes.Length >= 4
                    && headBytes[0] == 0x47 && headBytes[1] == 0x49
                    && headBytes[2] == 0x46 && headBytes[3] == 0x38)
                {
                    fileType = "GIF image data";
                    mimeType = "image/gif";
                }
                else if (headBytes.Length >= 4
                    && headBytes[0] == 0x52 && headBytes[1] == 0x49
                    && headBytes[2] == 0x46 && headBytes[3] == 0x46)
                {
                    fileType = "RIFF data";
                    mimeType = "application/octet-stream";
                }

                if (fileType == null)
                {
                    // Stream-scan the bytes (same test the psm1 oracle applied to
                    // the whole array) and stop at the first non-text byte — a
                    // binary is classified after a few KB, and an all-text file is
                    // never held in memory. An unreadable path / directory reports
                    // "data" rather than throwing (conservative coreutils parity).
                    bool allText = true;
                    try
                    {
                        using var s = BashFileSystem.OpenRead(filePath);
                        var buffer = new byte[65536];
                        int read;
                        while (allText && (read = s.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            for (int i = 0; i < read; i++)
                            {
                                byte b = buffer[i];
                                // psm1 oracle: b < 0x07 OR (b > 0x0D and b < 0x20 and b != 0x1B)
                                if (b < 0x07 || (b > 0x0D && b < 0x20 && b != 0x1B))
                                {
                                    allText = false;
                                    break;
                                }
                            }
                        }
                    }
                    catch
                    {
                        allText = false;
                    }

                    if (allText)
                    {
                        fileType = "ASCII text";
                        mimeType = "text/plain";
                    }
                    else
                    {
                        fileType = "data";
                        mimeType = "application/octet-stream";
                    }
                }

                string display = mime ? mimeType : fileType;
                string bashText = brief
                    ? display
                    : $"{filePath}: {display}";

                var obj = new PSObject();
                obj.TypeNames.Insert(0, "PsBash.TextOutput");
                obj.Properties.Add(new PSNoteProperty("BashText", bashText));
                obj.Properties.Add(new PSNoteProperty("FileName", filePath));
                obj.Properties.Add(new PSNoteProperty("FileType", fileType));
                obj.Properties.Add(new PSNoteProperty("MimeType", mimeType));
                WriteObject(obj);
            }
        }
    }
}
