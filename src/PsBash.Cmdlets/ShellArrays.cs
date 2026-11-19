using System.Collections;
using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// The arrays bash computes on each read that live in no variable of ps-bash's own (<see cref="ShellVariables.Computed"/>;
/// the emitter maps a read of each in <c>PsEmitter.ShellArrayExpr</c>): <c>GROUPS</c>, <c>DIRSTACK</c>,
/// <c>BASH_ALIASES</c>.
/// </summary>
public static class ShellArrays
{
    /// <summary><c>GROUPS</c>: the shell's group ids, primary first (<see cref="PsBash.Core.Platform.ProcessInfo.GroupIds"/>).</summary>
    public static object[] Groups()
    {
        var ids = PsBash.Core.Platform.ProcessInfo.GroupIds();
        var values = new object[ids.Count];
        for (int i = 0; i < ids.Count; i++) values[i] = ids[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
        return values;
    }

    /// <summary><c>DIRSTACK</c>: the directory stack as <c>dirs -l</c> lists it, the current directory first.</summary>
    public static object[] DirStack(SessionState session)
    {
        var entries = new List<object>();
        var pwd = BashVariableStore.Get("PWD");
        entries.Add(pwd ?? BashPath.ToShell(session.Path.CurrentFileSystemLocation.ProviderPath));
        foreach (var entry in session.Path.LocationStack(null))
            entries.Add(BashPath.ToShell(entry.Path));
        return [.. entries];
    }

    /// <summary><c>BASH_ALIASES</c>: an associative array of the defined aliases, name to replacement text.</summary>
    public static IDictionary Aliases(SessionState session)
    {
        var table = new Hashtable(StringComparer.Ordinal);
        object? value = session.PSVariable.GetValue("global:BashUserAliases");
        if ((value is PSObject pso ? pso.BaseObject : value) is IDictionary aliases)
            foreach (DictionaryEntry e in aliases)
                if (e.Key is string name) table[name] = e.Value is PSObject v ? v.BaseObject : e.Value;
        return table;
    }
}
