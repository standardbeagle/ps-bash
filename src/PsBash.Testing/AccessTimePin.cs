namespace PsBash.Testing;

/// <summary>
/// Access times a test sets and then checks, directly or through the product (<c>ls -u</c>, <c>find -anewer</c>,
/// <c>touch -m</c> leaving the access time alone). With last-access updates on (Windows "System Managed", the
/// default), another process that reads a freshly written file (Defender, the search indexer) moves its access
/// time to now, and that can land between the test's set and its check: <c>ls -u</c> listed <c>bbbb.h</c> first
/// and <c>find -anewer</c> matched an old file on beagle-ab (fleet s24m, 2 of 3 reruns).
/// <para>
/// <see cref="Set"/> pins a time and applies it. <see cref="Run{T}"/> applies every pin, runs the check, and when a
/// pinned file's access time is not what the check leaves it at (the pin, or the <c>after</c> value the check
/// itself sets), applies the pins and runs the check again, up to <see cref="Tries"/> times. A product that
/// moves the time moves it on every try, so its failure still fails the test.
/// </para>
/// Guard: <c>Core.Tests/AccessTimePinGuardTests</c> (a test sets an access time only through this type).
/// </summary>
public sealed class AccessTimePin
{
    /// <summary>How many times <see cref="Run{T}"/> runs a check whose pinned times were moved meanwhile.</summary>
    public const int Tries = 5;

    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, DateTime> _pins = new(StringComparer.Ordinal);

    /// <summary>Pins <paramref name="path"/> (a file or a directory) at <paramref name="time"/> and applies it now.</summary>
    public void Set(string path, DateTime time)
    {
        _pins[path] = time.ToUniversalTime();
        Apply(path, _pins[path]);
    }

    /// <summary>Runs <paramref name="check"/> with every pin applied, again while a pinned time moved meanwhile.</summary>
    /// <param name="check">The step that reads the times (or acts on the files); the test asserts on its result.</param>
    /// <param name="after">Pinned paths whose access time the check itself sets, with that time.</param>
    public T Run<T>(Func<T> check, params (string Path, DateTime Time)[] after)
    {
        for (int attempt = 1; ; attempt++)
        {
            foreach (var (path, time) in _pins) Apply(path, time);
            T result = check();
            if (attempt == Tries || Holds(after)) return result;
        }
    }

    /// <summary><see cref="Run{T}"/> for a check with no result.</summary>
    public void Run(Action check, params (string Path, DateTime Time)[] after)
        => Run(() => { check(); return 0; }, after);

    private bool Holds((string Path, DateTime Time)[] after)
    {
        foreach (var (path, pinned) in _pins)
        {
            DateTime expected = pinned;
            foreach (var (p, t) in after)
                if (string.Equals(p, path, StringComparison.Ordinal)) expected = t.ToUniversalTime();
            if ((AccessTimeUtc(path) - expected).Duration() > Tolerance) return false;
        }
        return true;
    }

    private static DateTime AccessTimeUtc(string path)
        => Directory.Exists(path) ? Directory.GetLastAccessTimeUtc(path) : File.GetLastAccessTimeUtc(path);

    private static void Apply(string path, DateTime utc)
    {
        if (Directory.Exists(path)) Directory.SetLastAccessTimeUtc(path, utc);
        else File.SetLastAccessTimeUtc(path, utc);
    }
}
