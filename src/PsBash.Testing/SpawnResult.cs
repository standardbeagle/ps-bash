namespace PsBash.Testing;

/// <summary>
/// Captured result of running a process to completion: stdout, stderr, exit
/// code, and wall-clock duration in milliseconds.
///
/// Unified across the test suites — previously each suite had its own tuple or
/// record (Escalation: <c>(int, string, string)</c>; Canary: <c>ModeResult</c>;
/// Differential: <c>OracleResult</c>). Suite-specific records may still wrap
/// this for domain naming, but the spawn primitive always returns this shape.
/// </summary>
public sealed record SpawnResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    long WallMs);
