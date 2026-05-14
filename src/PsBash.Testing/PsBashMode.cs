namespace PsBash.Testing;

/// <summary>
/// ps-bash execution modes from the QA rubric Directive 4 mode-interaction
/// matrix. M4 (interactive TTY) is intentionally excluded — too flaky for CI.
///
/// M1–M3 are external-process modes the unified <see cref="PsBashRunner"/>
/// drives via <see cref="ProcessSpawn"/>. M5/M6 are in-process cmdlet modes;
/// they are listed here so the enum is the single source of truth across
/// suites, but the in-process invocation itself stays in the Canary suite's
/// PowerShell fixture (different shape — no Process.Start).
/// </summary>
public enum PsBashMode
{
    /// <summary><c>ps-bash -c "script"</c> — one-shot string.</summary>
    M1_CFlag = 1,

    /// <summary><c>echo script | ps-bash</c> — stdin pipe.</summary>
    M2_StdinPipe = 2,

    /// <summary><c>ps-bash script.sh</c> — file argument.</summary>
    M3_FileArg = 3,

    /// <summary><c>Invoke-BashEval</c> cmdlet — in-process.</summary>
    M5_InvokeEval = 5,

    /// <summary><c>Invoke-BashSource</c> cmdlet — in-process, .sh file.</summary>
    M6_InvokeSource = 6,
}
