namespace PsBash.Testing;

/// <summary>
/// THE way a test writes the process environment: a scope that remembers each variable's value from BEFORE the test
/// touched it and puts that value back on <see cref="Dispose"/> (unset stays unset). Clearing to null instead
/// deleted <c>TERM</c> for the rest of the run (GrepOptionsGnuTests' Dispose), and every later class that read it
/// saw an environment no shell starts with.
/// <para>
/// The process environment is shared by every thread, so a scope alone does not make a write safe: the class that
/// opens one runs in a collection with <c>DisableParallelization = true</c> (Cmdlets.Tests:
/// <c>ProcessEnvironmentCollection</c>), so no other class runs while the value is changed. Guard:
/// <see cref="ProcessEnvironmentGuard"/>, called from <c>ProcessEnvironmentGuardTests</c> in each test project.
/// </para>
/// <para>
/// <see cref="Set(string, string?)"/> writes now; <see cref="Preserve"/> writes nothing and only restores, for a
/// variable the code under test writes (a PowerShell <c>$env:X = ...</c>, <c>read x</c>); <see cref="PreserveAll"/>
/// restores the whole environment, for a script that writes many names or names the test does not spell out. A .NET
/// quirk the restore inherits: setting a variable to <c>""</c> deletes it, so an empty prior value comes back unset.
/// </para>
/// </summary>
public sealed class ProcessEnv : IDisposable
{
    private readonly List<(string Name, string? Prior)> _saved = [];
    private Dictionary<string, string>? _all;
    private bool _disposed;

    private ProcessEnv() { }

    /// <summary>Sets <paramref name="name"/> (null unsets it) until the scope is disposed.</summary>
    public static ProcessEnv Set(string name, string? value) => new ProcessEnv().Change(name, value);

    /// <summary>Sets every variable in <paramref name="vars"/> (a null value unsets it) until the scope is disposed.</summary>
    public static ProcessEnv Set(params (string Name, string? Value)[] vars)
    {
        var scope = new ProcessEnv();
        foreach (var (name, value) in vars) scope.Change(name, value);
        return scope;
    }

    /// <summary>Restores <paramref name="names"/> to their current values on dispose, whatever writes them meanwhile.</summary>
    public static ProcessEnv Preserve(params string[] names)
    {
        var scope = new ProcessEnv();
        foreach (var name in names) scope.Remember(name);
        return scope;
    }

    /// <summary>
    /// Restores the WHOLE process environment on dispose: a variable added meanwhile is removed, a changed or removed
    /// one gets its value back. For a script that writes names the test does not list (or a cmdlet that does).
    /// </summary>
    public static ProcessEnv PreserveAll() => new() { _all = Snapshot() };

    private static Dictionary<string, string> Snapshot()
    {
        var all = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            all[(string)e.Key] = (string?)e.Value ?? "";
        return all;
    }

    /// <summary>Writes <paramref name="name"/> inside this scope; the first write of a name records the value to restore.</summary>
    public ProcessEnv Change(string name, string? value)
    {
        Remember(name);
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    private void Remember(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var (saved, _) in _saved)
            if (string.Equals(saved, name, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return;
        _saved.Add((name, Environment.GetEnvironmentVariable(name)));
    }

    /// <summary>Puts every variable back as it was before this scope first touched it, newest first.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var i = _saved.Count - 1; i >= 0; i--)
            Environment.SetEnvironmentVariable(_saved[i].Name, _saved[i].Prior);
        if (_all is null) return;
        foreach (var (name, value) in Snapshot())
            if (!_all.TryGetValue(name, out var prior)) Environment.SetEnvironmentVariable(name, null);
            else if (!string.Equals(prior, value, StringComparison.Ordinal)) Environment.SetEnvironmentVariable(name, prior);
        foreach (var (name, prior) in _all)
            if (Environment.GetEnvironmentVariable(name) is null) Environment.SetEnvironmentVariable(name, prior);
    }
}
