using System.Collections;
using System.Collections.Concurrent;

namespace PsBash.Core;

/// <summary>
/// The foreground shell's scalar variables: the process environment, plus the exact value of each variable whose value
/// the environment cannot hold. Every reader and writer of a foreground scalar goes through here (<c>BashVariableStore</c>,
/// <see cref="ShellExports"/>, <see cref="ScalarOverlay.Capture"/>); an in-process job has its own store
/// (<see cref="ScalarOverlay"/>).
/// <para>
/// <b>Invariant</b> (docs/specs/runtime-functions.md "Raw bytes"): a value read back is the value written, escaped-byte
/// markers (<see cref="RawBytes"/>) included. The environment of .NET on Linux turns a lone surrogate into U+FFFD, so
/// <c>s=$'\xff'; printf %s "$s"</c> wrote <c>ef bf bd</c> where bash writes <c>ff</c>; every scalar (an assignment,
/// <c>$( )</c>, <c>read</c>, <c>printf -v</c>, a local, an export to a child) lost its invalid bytes there. Whether
/// the environment holds every string is measured once (<see cref="EnvironmentHoldsEveryString"/>), never assumed per
/// OS: where it does (Windows' UTF-16 environment) nothing is kept on the side.
/// </para>
/// <para>
/// A kept value is used only while the environment still has what was stored for it: anything that writes the
/// environment directly (the host's per-command reset, PowerShell code) makes it stale, and it is dropped.
/// </para>
/// </summary>
public static class ProcessScalars
{
    private readonly record struct Kept(string Value, string Stored);

    private static readonly ConcurrentDictionary<string, Kept> s_kept = new(ShellExports.NameComparer);

    private static readonly Lazy<bool> s_holdsEveryString = new(ProbeEnvironment);

    /// <summary>
    /// True when the process environment gives back every string it is given (a lone surrogate too). False on Linux,
    /// where the shell's <c>Env:</c> drive must then read through this store (<c>PsBash.Cmdlets.JobEnvironmentProvider</c>,
    /// installed by the host's session state) for <c>$env:NAME</c> to see an invalid byte.
    /// </summary>
    public static bool EnvironmentHoldsEveryString => s_holdsEveryString.Value;

    private static bool ProbeEnvironment()
    {
        const string name = "__PSBASH_ENV_PROBE";
        var value = "a" + RawBytes.ToMarker(0xFF) + "b";
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            return string.Equals(Environment.GetEnvironmentVariable(name), value, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try { Environment.SetEnvironmentVariable(name, null); } catch (Exception) { }
        }
    }

    /// <summary>The value of <paramref name="name"/> (a store name); null when unset.</summary>
    public static string? Get(string name)
    {
        var stored = Environment.GetEnvironmentVariable(name);
        return stored is null || s_kept.IsEmpty ? stored : Exact(name, stored);
    }

    /// <summary>Assign <paramref name="name"/> (a store name); null unsets it.</summary>
    public static void Set(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
        if (value is null || !RawBytes.ContainsMarker(value))
        {
            if (!s_kept.IsEmpty) s_kept.TryRemove(name, out _);
            return;
        }
        var stored = Environment.GetEnvironmentVariable(name);
        if (stored is not null && !string.Equals(stored, value, StringComparison.Ordinal))
            s_kept[name] = new Kept(value, stored);
        else
            s_kept.TryRemove(name, out _);
    }

    /// <summary>Every set variable with its exact value, under the name the environment has.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Entries()
    {
        bool any = !s_kept.IsEmpty;
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is not string name) continue;
            var stored = e.Value as string ?? "";
            yield return new KeyValuePair<string, string>(name, any ? Exact(name, stored) : stored);
        }
    }

    /// <summary>
    /// <paramref name="value"/> as this store has it, when <paramref name="value"/> is what the environment holds for
    /// <paramref name="name"/> (an entry copied from the process environment into a child's); otherwise unchanged.
    /// </summary>
    public static string ExactIfStored(string name, string value)
        => !s_kept.IsEmpty && s_kept.TryGetValue(name, out var kept) && string.Equals(kept.Stored, value, StringComparison.Ordinal)
            && string.Equals(Environment.GetEnvironmentVariable(name), value, StringComparison.Ordinal)
            ? kept.Value
            : value;

    /// <summary>
    /// True while an EXPORTED scalar's exact value is kept beside the environment (only where the environment cannot
    /// hold it): every program started now gets it in its environment, so it must be started by ps-bash with exact bytes,
    /// not by PowerShell's launcher (<see cref="NativeRunner"/>). A scalar that is not exported never reaches a program's
    /// environment, so it takes no program off the terminal (a <c>line</c> left over from a read loop of a binary file).
    /// A kept value the environment no longer has is dropped here, as it is on a read.
    /// </summary>
    public static bool KeepsAnyExported
    {
        get
        {
            if (s_kept.IsEmpty) return false;
            foreach (var (name, kept) in s_kept)
            {
                if (!string.Equals(Environment.GetEnvironmentVariable(name), kept.Stored, StringComparison.Ordinal))
                {
                    s_kept.TryRemove(new KeyValuePair<string, Kept>(name, kept));
                    continue;
                }
                if (ShellExports.IsExported(name)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Whether a program started with <paramref name="argv"/> must be started by ps-bash with exact bytes
    /// (<see cref="NativeRunner"/>): an exported scalar is kept (<see cref="KeepsAnyExported"/>), or an argument has a
    /// marker the launcher's UTF-8 would turn into U+FFFD. Never where the environment holds every string (Windows: a
    /// child gets UTF-16 there whoever starts it).
    /// </summary>
    public static bool ChildNeedsExactBytes(IEnumerable<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (s_kept.IsEmpty && EnvironmentHoldsEveryString) return false;
        if (KeepsAnyExported) return true;
        foreach (var a in argv)
            if (RawBytes.ContainsMarker(a)) return true;
        return false;
    }

    /// <summary>The host replaced the whole environment (a new command's launcher block): nothing kept is current.</summary>
    public static void Forget() => s_kept.Clear();

    private static string Exact(string name, string stored)
    {
        if (!s_kept.TryGetValue(name, out var kept)) return stored;
        if (string.Equals(kept.Stored, stored, StringComparison.Ordinal)) return kept.Value;
        s_kept.TryRemove(new KeyValuePair<string, Kept>(name, kept));
        return stored;
    }
}
