using System.Text.RegularExpressions;

namespace PsBash.Core.Tests;

/// <summary>Helpers for tests that assert the exact emitted text of a function.</summary>
internal static class PsBuildTestText
{
    /// <summary>Drops the bash-source comment a function definition carries (PsBuild.FunctionSourceMark), which has its own tests.</summary>
    public static string StripFunctionSource(string emitted) => Regex.Replace(
        Regex.Replace(emitted, @"<#bash:[A-Za-z0-9+/=]*#> ", ""),
        @"; \[PsBash\.Cmdlets\.FunctionSources\]::Defined\(\$ExecutionContext\.SessionState, '[^']*'\)", "");

    /// <summary>A top-level statement of a script that can set a trap ends with the signal poll (PsBuild.SignalTrapPoll), which has its own tests.</summary>
    public static string WithSignalPoll(string statement) => statement + "; " + PsBash.Core.Parser.PsBuild.SignalTrapPoll;
}
