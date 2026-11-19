using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Runs ONE child command in another directory (<c>env -C DIR</c>, <c>find -execdir</c>) and puts everything back
/// afterwards. A shell has three notions of "where am I", and a child must see the new directory through all of them:
/// <list type="bullet">
/// <item>the PowerShell location: in-process commands (<c>ls</c>, <c>cat f</c>) and the start directory of a program;</item>
/// <item>the process working directory (<see cref="Environment.CurrentDirectory"/>): .NET path resolution;</item>
/// <item>the shell's logical cwd (<c>$global:__PsBashCwd</c>), which <c>pwd</c> prints.</item>
/// </list>
/// <c>$PWD</c> / <c>$OLDPWD</c> stay as they were: GNU <c>env -C d printenv PWD</c> prints the caller's directory.
/// REGRESSION: <c>env -C</c> moved only the location, so <c>env -C DIR pwd</c> printed the original directory while
/// <c>env -C DIR ls</c> listed DIR. Guard: <c>ChildDirectoryGuardTests</c> (no other cmdlet moves the location).
/// </summary>
internal sealed class ChildDirectory : IDisposable
{
    private const string StackName = "psbash-child-dir";
    private const string LogicalCwd = "global:__PsBashCwd";

    private readonly PSCmdlet _cmdlet;
    private readonly string _previousProcessCwd;
    private readonly object? _previousLogical;
    private bool _pushed;
    private bool _disposed;

    private ChildDirectory(PSCmdlet cmdlet)
    {
        _cmdlet = cmdlet;
        _previousProcessCwd = Environment.CurrentDirectory;
        _previousLogical = cmdlet.SessionState.PSVariable.GetValue(LogicalCwd, null);
    }

    /// <summary>Moves all three to <paramref name="fullPath"/> (an existing directory, provider form) until disposed.</summary>
    public static ChildDirectory Enter(PSCmdlet cmdlet, string fullPath)
    {
        var scope = new ChildDirectory(cmdlet);
        try
        {
            cmdlet.SessionState.Path.PushCurrentLocation(StackName);
            scope._pushed = true;
            cmdlet.SessionState.Path.SetLocation(WildcardPattern.Escape(fullPath));
            Environment.CurrentDirectory = fullPath;
            cmdlet.SessionState.PSVariable.Set(LogicalCwd, fullPath);
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Best-effort each: the child may have removed a directory, and none of these may mask its own outcome.
        if (_pushed)
        {
            try { _cmdlet.SessionState.Path.PopLocation(StackName); } catch { /* best-effort */ }
        }
        try { Environment.CurrentDirectory = _previousProcessCwd; } catch { /* best-effort */ }
        try { _cmdlet.SessionState.PSVariable.Set(LogicalCwd, _previousLogical); } catch { /* best-effort */ }
    }
}
