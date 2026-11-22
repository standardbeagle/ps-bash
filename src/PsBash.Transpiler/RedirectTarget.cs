namespace PsBash.Core;

/// <summary>
/// The target of a file redirect that expanded to anything but one word (<c>x='a b'; cmd &gt; $x</c>, an empty <c>$e</c>,
/// <c>&gt; a-{1,2}</c>): bash's <c>$x: ambiguous redirect</c>, naming the word as written. The emitter passes
/// <see cref="Ambiguous"/> in place of the path, and the redirect stage fails the open like any other
/// (<c>InvokeBashRedirectCommand.TryOpen</c>: status 1, the command does not run, <c>set -e</c> ends the script).
/// A NUL can be in no path, so the marker can never name a real file.
/// </summary>
public static class RedirectTarget
{
    private const string Marker = "\0ambiguous\0";

    /// <summary>The stand-in path for an ambiguous target written as <paramref name="shown"/>.</summary>
    public static string Ambiguous(string shown) => Marker + shown;

    /// <summary>True when <paramref name="path"/> is an <see cref="Ambiguous"/> stand-in; <paramref name="shown"/> = the word.</summary>
    public static bool IsAmbiguous(string? path, out string shown)
    {
        if (path is not null && path.StartsWith(Marker, StringComparison.Ordinal))
        {
            shown = path[Marker.Length..];
            return true;
        }
        shown = "";
        return false;
    }
}
