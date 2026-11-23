using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// The stdin of a command or compound given <c>&lt;&amp;N</c> / <c>&lt;&amp;$fd</c> whose emission is no pipe element
/// (<c>PsBuild.DescriptorStdinScope</c>): a <see cref="StdinCursor"/> over descriptor N, read one line at a time as the
/// readers in the scope ask, so what they do not read stays in the descriptor (<c>exec 3&lt;f; { read a; } &lt;&amp;3;
/// read b &lt;&amp;3</c>). <c>0</c>-<c>2</c>, and a descriptor that is a copy of the shell's stdin (<c>exec 5&lt;&amp;0</c>),
/// keep the stdin the scope already has: nothing is written, and the scope closes nothing. <c>-</c> (closed) is an empty
/// stdin. A descriptor that is not open is reported as <c>Read-BashFd</c> reports it (status 1), and the stdin is empty.
/// </summary>
[Cmdlet(VerbsCommon.New, "BashFdStdin")]
[OutputType(typeof(StdinCursor))]
public sealed class NewBashFdStdinCommand : BashCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [AllowEmptyString]
    public string Descriptor { get; set; } = "";

    protected override void EndProcessing()
    {
        var word = Descriptor;
        if (word == "-")
        {
            WriteObject(new StdinCursor());
            return;
        }
        if (word.Length == 0 || !word.All(char.IsAsciiDigit) || !int.TryParse(word, out int fd))
        {
            FileSystemHelpers.WriteBashError(this, $"{word}: ambiguous redirect");
            WriteObject(new StdinCursor());
            return;
        }
        if (fd <= 2 || BashFdTable.ReadsShellStdin(fd))
            return;
        string? first;
        try
        {
            first = BashFdTable.ReadLine(fd);
        }
        catch (InvalidOperationException ex)
        {
            FileSystemHelpers.WriteBashError(this, ex.Message);
            WriteObject(new StdinCursor());
            return;
        }
        WriteObject(new StdinCursor(Lines(fd, first)));
    }

    /// <summary>The descriptor's lines from <paramref name="first"/> on, each read when a reader asks for it.</summary>
    private static IEnumerable<object> Lines(int fd, string? first)
    {
        for (var line = first; line is not null;)
        {
            yield return line;
            try { line = BashFdTable.ReadLine(fd); }
            catch (InvalidOperationException) { yield break; }
        }
    }
}
