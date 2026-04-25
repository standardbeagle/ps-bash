namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// Captured result of running a script through one interpreter.
/// </summary>
public sealed record OracleResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    long WallMs);
