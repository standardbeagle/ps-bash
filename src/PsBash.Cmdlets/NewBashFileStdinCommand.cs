using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// The stdin of a command fed <c>&lt; file</c> whose emission is no pipe element (exec's <c>cmd; return</c>, eval / source
/// text): a <see cref="StdinCursor"/> that reads the file one record at a time as the readers in its scope ask
/// (<c>PsBuild.FileStdinScope</c>), exactly as <c>Invoke-BashCat -RawStdinBytes</c> would write it (exact bytes, a
/// missing final newline kept). A block after a pipe starts only once its upstream has finished, so
/// <c>Invoke-BashCat f | . { … }</c> read the whole file first and <c>exec head -c 5 &lt; /dev/zero</c> never ended.
/// The scope's <c>finally</c> closes the cursor, which closes the file.
/// <para>
/// The file is opened here (an open error is reported now, as cat reports it, and the stdin is empty); a read error
/// later ends the input. The first record is offered as a regular file's (<see cref="RegularStdin"/>), as cat does.
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.New, "BashFileStdin")]
[OutputType(typeof(StdinCursor))]
public sealed class NewBashFileStdinCommand : BashCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [AllowEmptyString]
    public string Path { get; set; } = "";

    protected override void EndProcessing()
    {
        string path = Path;
        foreach (var op in FileSystemHelpers.ResolveOperands(this, Path, expandWildcards: false))
        {
            op.Claim(this);
            path = op.Path;
            break;
        }
        IEnumerable<BashFileSystem.TextLine> lines;
        try
        {
            lines = BashFileSystem.ReadTextLines(path, exact: true, exactIfBinary: true);
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"cat: {BashPath.ToShell(path)}: {FileSystemHelpers.ReadErrorMessage(ex, path)}");
            WriteObject(new StdinCursor());
            return;
        }
        bool offer = File.Exists(path) && !FileSystemHelpers.IsNullDevice(path);
        WriteObject(new StdinCursor(Records(lines, offer ? path : null)));
    }

    /// <summary>The file's records, read on demand. A read error ends the input (no cmdlet is running to report it).</summary>
    private static IEnumerable<object> Records(IEnumerable<BashFileSystem.TextLine> lines, string? regularFile)
    {
        using var e = lines.GetEnumerator();
        while (true)
        {
            BashFileSystem.TextLine line;
            try
            {
                if (!e.MoveNext()) yield break;
                line = e.Current;
            }
            catch (IOException) { yield break; }
            catch (UnauthorizedAccessException) { yield break; }
            var record = BashRuntime.NewBashObject(line.Text, noTrailingNewline: !line.HasTrailingNewline);
            if (regularFile is not null)
            {
                long size = 0;
                try { size = new FileInfo(regularFile).Length; } catch (IOException) { }
                RegularStdin.Offer(record, size);
                regularFile = null;
            }
            yield return record;
        }
    }
}
