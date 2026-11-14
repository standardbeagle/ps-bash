namespace PsBash.Shell;

/// <summary>Checks bash makes on a script file before running it (<c>bash file</c>).</summary>
internal static class ScriptFile
{
    /// <summary>How much of the file bash samples (shell.c, open_shell_script).</summary>
    private const int SampleChars = 80;

    /// <summary>
    /// True when bash would refuse the script as binary ("cannot execute binary file", status 126): a NUL
    /// in the first <see cref="SampleChars"/> characters before the first newline (bash check_binary_file).
    /// A NUL on a later line is not checked; bash runs that script.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<char> text)
    {
        var sample = text.Length > SampleChars ? text[..SampleChars] : text;
        foreach (var c in sample)
        {
            if (c == '\n') return false;
            if (c == '\0') return true;
        }
        return false;
    }
}
