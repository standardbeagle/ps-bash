using System.Text.RegularExpressions;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    /// <summary>What the root script reports to the shell's reader (PsBash.Cmdlets.ShellReader) before each input line.</summary>
    internal enum ReaderMode
    {
        /// <summary>Nothing: the script cannot observe the reader (no <c>${v@P}</c>, never turns history on).</summary>
        Off,
        /// <summary>The count of lines read (prompt <c>\#</c>): the script expands a prompt (<c>${v@P}</c>).</summary>
        Count,
        /// <summary>The count, and each line's text for the history list: the script can turn history on.</summary>
        CountAndHistory,
    }

    /// <summary>The <see cref="ReaderMode"/> of the root script being emitted; <see cref="ReaderMode.Off"/> for any other text.</summary>
    [ThreadStatic]
    private static ReaderMode _readerMode;

    // `set -o history`, `shopt -o -s history`: how a script turns history on.
    private static readonly Regex TurnsHistoryOn = new(@"(?:-o|-s)\s+history\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a root script reports its input lines, and with what. Only a script that can observe them pays for the
    /// report: one that expands a prompt (<c>${v@P}</c>: <c>\#</c>, <c>\!</c>) or turns history on (the entries
    /// <c>history</c> lists). Every other script's text is unchanged.
    /// </summary>
    internal static ReaderMode ReaderModeFor(string bash) =>
        TurnsHistoryOn.IsMatch(bash) ? ReaderMode.CountAndHistory
        : bash.Contains("@P", StringComparison.Ordinal) ? ReaderMode.Count
        : ReaderMode.Off;

    /// <summary>
    /// The report (<see cref="PsBuild.ReaderCommand"/>) that goes before input line <paramref name="line"/> (0-based) of
    /// the root script whose parsed root is <paramref name="root"/>; "" when the script reports nothing. Under history
    /// the report carries the line as bash's <c>cmdhist</c> stores it (<see cref="CmdHist.Entry(string)"/>).
    /// </summary>
    private static string ReaderCommandFor(Command root, int line)
    {
        if (_readerMode == ReaderMode.Off) return "";
        if (_readerMode == ReaderMode.Count) return PsBuild.ReaderCommand(null);
        var lines = BashParser.InputLinesOf(root);
        return lines is not null && line < lines.Count
            ? PsBuild.ReaderCommand(CmdHist.Entry(lines.Text(line)))
            : PsBuild.ReaderCommand(null);
    }
}
