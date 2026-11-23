namespace PsBash.Shell;

/// <summary>Checks bash makes on a script file before running it (<c>bash file</c>).</summary>
internal static class ScriptFile
{
    /// <summary>How much of the file bash samples (shell.c, open_shell_script).</summary>
    private const int SampleChars = 80;

    /// <summary>Why bash will not run a script file: its exit status and diagnostic line.</summary>
    public readonly record struct OpenFailure(int Status, string Message);

    /// <summary>
    /// The one check every <c>ps-bash FILE</c> path makes before reading FILE (bash open_shell_script):
    /// a missing file is 127; a directory is 126 "Is a directory", named by the script as <c>$0</c> as bash does.
    /// Null when the file can be opened for reading.
    /// </summary>
    public static OpenFailure? CheckOpenable(string path)
    {
        if (Directory.Exists(path)) return new OpenFailure(126, $"{path}: {path}: Is a directory");
        if (!File.Exists(path)) return new OpenFailure(127, $"ps-bash: {path}: No such file or directory");
        return null;
    }

    /// <summary>
    /// A read that failed after <see cref="CheckOpenable"/> passed (permission, sharing, gone meanwhile):
    /// bash gives 127 only for a missing file and 126 for every other open failure.
    /// </summary>
    public static OpenFailure ReadFailure(string path, Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => new OpenFailure(127, $"ps-bash: {path}: No such file or directory"),
        UnauthorizedAccessException => new OpenFailure(126, $"ps-bash: {path}: Permission denied"),
        _ => new OpenFailure(126, $"ps-bash: {path}: {ex.Message}"),
    };

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
