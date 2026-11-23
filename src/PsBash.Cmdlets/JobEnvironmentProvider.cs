using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Provider;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>Env:</c> drive of a background job's runspace (<see cref="BashJobRunspaces.InitialState"/>), in place of
/// PowerShell's Environment provider. Emitted code reads and writes a scalar as <c>$env:NAME</c>, and a function the
/// job calls was emitted before anyone knew it would run in a job, so the job's scalars are served here: from the
/// job's own store (<see cref="PsBash.Core.ScalarOverlay"/>, taken at its <c>&amp;</c>) while the runspace runs a job, from the
/// process environment otherwise (the module's import, an idle runspace).
/// <para>
/// The shell's own runspace uses it too where the process environment cannot hold every value
/// (<see cref="PsBash.Core.ProcessScalars"/>: on Linux an invalid byte became U+FFFD in <c>$env:NAME</c>); the host
/// installs it then (<c>RunspaceTemplate.InstallShellEnvironmentDrive</c>).
/// </para>
/// <para>
/// Same behavior as the provider it replaces: <c>$env:X</c> / <c>${env:X}</c> (content), <c>$env:X = v</c> (an empty
/// value is a set variable), <c>$env:X = $null</c> and <c>Remove-Item Env:X</c> (unset), <c>Test-Path Env:X</c>,
/// <c>Get-ChildItem Env:</c> (DictionaryEntry items). See <c>docs/specs/background-jobs.md</c> section 3.
/// </para>
/// </summary>
[CmdletProvider(ProviderName, ProviderCapabilities.ShouldProcess)]
public sealed class JobEnvironmentProvider : ContainerCmdletProvider, IContentCmdletProvider
{
    public const string ProviderName = "BashJobEnvironment";

    /// <summary>
    /// The <c>Env:</c> drive only where PowerShell's Environment provider is absent (a job runspace,
    /// <see cref="BashJobRunspaces.InitialState"/>). <c>Import-Module PsBash.Cmdlets.dll</c> registers every public
    /// provider of the assembly in the importing session too, whose <c>Env:</c> drive already exists ("A drive with the
    /// name 'Env' already exists" failed the import): there it adds no drive and is never used.
    /// </summary>
    protected override Collection<PSDriveInfo> InitializeDefaultDrives()
    {
        try
        {
            if (SessionState.Provider.GetAll().Any(p => p.Name == "Environment")) return [];
        }
        catch (Exception) { /* no provider table yet: a job runspace's initial state */ }
        return [new PSDriveInfo("Env", ProviderInfo, "", "The shell variables", null)];
    }

    // The store resolves the job's ScalarOverlay or the process environment per call.
    private static string? Read(string name) => BashVariableStore.Get(name);

    private static void Write(string name, string? value)
    {
        if (!string.IsNullOrEmpty(name)) BashVariableStore.Set(name, value);
    }

    private static List<KeyValuePair<string, string>> All() => [.. BashVariableStore.Enumerate()];

    private static string Name(string path) => path.TrimStart('\\', '/');

    protected override bool IsValidPath(string path) => true;

    protected override bool ItemExists(string path)
    {
        var name = Name(path);
        return name.Length == 0 || Read(name) is not null;
    }

    protected override void GetItem(string path)
    {
        var name = Name(path);
        if (name.Length == 0) { WriteItemObject(PSDriveInfo, path, true); return; }
        if (Read(name) is { } value) WriteItemObject(new DictionaryEntry(name, value), name, false);
    }

    protected override void SetItem(string path, object value) => Write(Name(path), ToText(value));

    protected override void ClearItem(string path) => Write(Name(path), "");

    protected override void GetChildItems(string path, bool recurse)
    {
        foreach (var (name, value) in All()) WriteItemObject(new DictionaryEntry(name, value), name, false);
    }

    protected override void GetChildNames(string path, ReturnContainers returnContainers)
    {
        foreach (var (name, _) in All()) WriteItemObject(name, name, false);
    }

    protected override bool HasChildItems(string path) => Name(path).Length == 0;

    protected override void RemoveItem(string path, bool recurse) => Write(Name(path), null);

    protected override void NewItem(string path, string itemTypeName, object newItemValue) => Write(Name(path), ToText(newItemValue) ?? "");

    protected override void RenameItem(string path, string newName)
    {
        var name = Name(path);
        var value = Read(name);
        Write(newName, value);
        Write(name, null);
    }

    protected override void CopyItem(string path, string copyPath, bool recurse) => Write(Name(copyPath), Read(Name(path)));

    private static string? ToText(object? value)
    {
        if (value is PSObject pso) value = pso.BaseObject;
        return value switch
        {
            null => null,
            DictionaryEntry e => e.Value?.ToString(),
            _ => LanguagePrimitives.ConvertTo<string>(value),
        };
    }

    public IContentReader GetContentReader(string path) => new Reader(Name(path));
    public object? GetContentReaderDynamicParameters(string path) => null;
    public IContentWriter GetContentWriter(string path) => new Writer(Name(path));
    public object? GetContentWriterDynamicParameters(string path) => null;
    public void ClearContent(string path) => Write(Name(path), null);
    public object? ClearContentDynamicParameters(string path) => null;

    private sealed class Reader(string name) : IContentReader
    {
        private bool _done;

        public IList Read(long readCount)
        {
            if (_done || JobEnvironmentProvider.Read(name) is not { } value) return Array.Empty<object>();
            _done = true;
            return new object[] { value };
        }

        public void Seek(long offset, SeekOrigin origin) { }
        public void Close() { }
        public void Dispose() { }
    }

    private sealed class Writer(string name) : IContentWriter
    {
        public IList Write(IList content)
        {
            string? value;
            if (content.Count == 0) value = null;
            else if (content.Count == 1) value = ToText(content[0]);
            else
            {
                var sb = new System.Text.StringBuilder();
                foreach (var o in content) sb.Append(ToText(o));
                value = sb.ToString();
            }
            JobEnvironmentProvider.Write(name, value);
            return content;
        }

        public void Seek(long offset, SeekOrigin origin) { }
        public void Close() { }
        public void Dispose() { }
    }
}
