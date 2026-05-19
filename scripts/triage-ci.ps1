<#
.SYNOPSIS
    Categorize failing xUnit tests from a CI run into actionable buckets.

.DESCRIPTION
    The 5-bucket model (see #24 follow-up methodology):
      A. Env divergence — passes locally, fails on CI runner with different env
      B. Real semantic bug — same behavior cross-platform, differs from bash oracle
      C. Test design flaw — assertion too strict, hardcoded paths, wrong type
      D. Flake — inconsistent across reruns
      E. Deferred-hard-bug — known multi-day semantic gap (memory.md)

    Reading 100+ failing test names by hand and categorizing them is the bottleneck
    in CI iteration. This script pulls them via `gh run view`, pattern-matches each
    FQN against the known catalog, and emits a markdown bucket list ready for action.

    Use as: ./scripts/triage-ci.ps1 [-RunId 1234]
    Defaults to the most recent failed build.yml run.

.PARAMETER RunId
    Optional. gh run ID to triage. If omitted, picks the latest failed Build run.

.PARAMETER Workflow
    Optional. Workflow name. Default: build.yml.

.PARAMETER Verbose
    Show per-test details, not just the bucket summary.

.EXAMPLE
    ./scripts/triage-ci.ps1
    Triages the latest failing Build workflow run.

.EXAMPLE
    ./scripts/triage-ci.ps1 -RunId 26088912345
    Triages a specific run.

.EXAMPLE
    ./scripts/triage-ci.ps1 -Verbose
    Shows the actual exception messages alongside the bucket assignment.
#>
[CmdletBinding()]
param(
    [long]$RunId = 0,
    [string]$Workflow = 'build.yml',
    [switch]$VerboseOutput
)

$ErrorActionPreference = 'Stop'
$repo = 'standardbeagle/ps-bash'

# ============================================================================
# Catalog: tests we already know are bucket E (deferred-hard-bugs).
# Update this list when new bugs land in ~/.claude/memory/deferred_hard_bugs.md.
# ============================================================================
$DeferredHardBugs = @(
    # Pipefail / PIPESTATUS
    'Differential_Pipe_ExitCode_PipefailOn_FirstFailurePropagates',
    # eval caller scope
    'Differential_Eval_VarAssignment_VisibleAfterEval',
    'Differential_Eval_ExitCodeFromFalse_PropagatedToShell',
    # ANSI-C quoting $'...'
    'Differential_AnsiCQuote_Tab',
    'Differential_AnsiCQuote_Newline',
    # Adjacent-quote merging
    'Differential_AdjacentQuotes_SingleThenDouble',
    # Quoted "$@" arg-boundary preservation
    'Differential_QuotedAt_PreservesSpacesInArgs',
    # broken-pipe / SIGPIPE
    'Differential_Pipe_BrokenPipe_HeadClosesEarly'
)

# ============================================================================
# Catalog: known test design flaws (bucket C) — fix the test, not the code.
# ============================================================================
$TestDesignFlaws = @(
    # Sed -e A -e B: PowerShell binder rejects repeated -e; production has a
    # psm1 wrapper but PwshTestFixture loads the dll directly, bypassing the
    # wrapper. Tracked as load-order issue, not a real cmdlet bug.
    'Sed_DashE_MultipleExpressions_AppliedInOrder'
)

# ============================================================================
# Catalog: known env-divergence (bucket A) — CI runner has X, local doesn't.
# ============================================================================
$EnvDivergencePatterns = @(
    # Anything that uses AssertOracle.GoldenAsync — depends on the canonical-env
    # fixture which we've documented as Windows-CI-fragile.
    # Match by suite, not by individual test name.
    @{ Suite = 'PsBash.Differential.Tests'; Pattern = '^Differential_Redirect_' },
    @{ Suite = 'PsBash.Differential.Tests'; Pattern = '^Differential_SpecialVar_' },
    @{ Suite = 'PsBash.Differential.Tests'; Pattern = '^Differential_DirTest_' },
    @{ Suite = 'PsBash.Differential.Tests'; Pattern = '^Differential_Standalone_FileTest_' },
    @{ Suite = 'PsBash.Differential.Tests'; Pattern = '^Differential_Background_' },
    @{ Suite = 'PsBash.Differential.Tests'; Pattern = '^Differential_Kill_' },
    @{ Suite = 'PsBash.Differential.Tests'; Pattern = '^Differential_CommandSub' },
    @{ Suite = 'PsBash.Differential.Tests'; Pattern = '^Differential_ProcessSub' },
    @{ Suite = 'PsBash.Differential.Tests'; Pattern = 'EnvPrefix_DoesNotLeakToShell' },
    @{ Suite = 'PsBash.Differential.Tests'; Pattern = 'Backslash_OutsideQuotes_Literal' },
    @{ Suite = 'PsBash.Differential.Tests'; Pattern = 'Oracle.GoldenTests' },
    @{ Suite = 'PsBash.Differential.Tests'; Pattern = 'Oracle.OracleTests.AssertOracle_ExitCode_Passes' },
    @{ Suite = 'PsBash.Differential.Tests'; Pattern = 'CommandSubstitution_NestedQuoting' }
)

# ============================================================================
# Step 1: resolve run ID.
# ============================================================================
if ($RunId -eq 0) {
    $latest = gh run list --repo $repo --workflow=$Workflow --status=failure --limit 1 --json databaseId 2>&1 | ConvertFrom-Json
    if (-not $latest) {
        Write-Host "No failing run found on workflow '$Workflow'." -ForegroundColor Yellow
        exit 1
    }
    $RunId = $latest.databaseId
    Write-Host "Using latest failing run: $RunId" -ForegroundColor Cyan
}

# ============================================================================
# Step 2: pull failing test names from the run log.
# ============================================================================
Write-Host "Fetching failure log..." -ForegroundColor Cyan
$logRaw = gh run view --repo $repo $RunId --log-failed 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to fetch log for run $RunId." -ForegroundColor Red
    exit 1
}

# Parse: "[xUnit.net ...] PsBash.X.Y.Tests.ClassName.TestName [FAIL]"
# Also capture surrounding suite/platform context.
$failures = @()
$logRaw -split "`n" | ForEach-Object {
    if ($_ -match '\(([\w-]+),\s+([\w-]+),\s+ps-bash[\w.]*\)') {
        $script:platform = $matches[1]
    }
    if ($_ -match '\s+(PsBash\.[\w.]+Tests\.[\w.]+)\s+\[FAIL\]') {
        $fqn = $matches[1]
        # Suite is the test project namespace (PsBash.X.Tests).
        $suite = ($fqn -split '\.' | Select-Object -First 3) -join '.'
        # Method is the last segment.
        $method = ($fqn -split '\.')[-1]

        $script:failures += [pscustomobject]@{
            Platform = $script:platform
            Suite    = $suite
            FQN      = $fqn
            Method   = $method
            Bucket   = $null
            Reason   = $null
        }
    }
}

if ($failures.Count -eq 0) {
    Write-Host "No xUnit failures found in run $RunId. (Maybe build failed before tests ran?)" -ForegroundColor Yellow
    exit 0
}

Write-Host "Found $($failures.Count) failing test invocations." -ForegroundColor Cyan

# ============================================================================
# Step 3: bucket each failure.
# ============================================================================
foreach ($f in $failures) {
    # Bucket E: deferred-hard-bug
    if ($DeferredHardBugs -contains $f.Method) {
        $f.Bucket = 'E'
        $f.Reason = 'Deferred-hard-bug (~/.claude/memory/deferred_hard_bugs.md)'
        continue
    }

    # Bucket C: test design flaw
    if ($TestDesignFlaws -contains $f.Method) {
        $f.Bucket = 'C'
        $f.Reason = 'Test design flaw — fix the test, not the code'
        continue
    }

    # Bucket A: env divergence (pattern match against Method, not full FQN).
    $envMatch = $EnvDivergencePatterns | Where-Object {
        $_.Suite -eq $f.Suite -and $f.Method -match $_.Pattern
    } | Select-Object -First 1
    if ($envMatch) {
        $f.Bucket = 'A'
        $f.Reason = "Env divergence — CanonicalEnv-fixture sensitive (pattern: $($envMatch.Pattern))"
        continue
    }

    # Default: bucket B (real semantic bug) — needs investigation.
    $f.Bucket = 'B'
    $f.Reason = 'Real bug or unclassified — investigate'
}

# ============================================================================
# Step 4: report.
# ============================================================================
$buckets = $failures | Group-Object Bucket | Sort-Object Name
$total = $failures.Count
$uniqueTests = ($failures | Select-Object Method -Unique).Count

Write-Host ""
Write-Host "============================================================"
Write-Host "  CI Failure Triage — run $RunId"
Write-Host "  $total failing invocations across $uniqueTests unique tests"
Write-Host "============================================================"
Write-Host ""

$bucketLabels = @{
    'A' = 'Env divergence (accept; mark [SkippableFact])'
    'B' = 'Real semantic bug or UNCLASSIFIED (needs attention)'
    'C' = 'Test design flaw (fix the test)'
    'D' = 'Flake (quarantine + file ticket)'
    'E' = 'Deferred-hard-bug (skip + file separate task)'
}

foreach ($b in $buckets) {
    $label = $bucketLabels[$b.Name]
    Write-Host "Bucket $($b.Name): $label" -ForegroundColor Yellow
    Write-Host "  $($b.Count) invocation(s), $((($b.Group | Select-Object Method -Unique).Count)) unique test(s)"
    Write-Host ""

    if ($VerboseOutput) {
        $b.Group | ForEach-Object {
            "    [$($_.Platform.PadRight(8))] $($_.FQN)"
        } | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
    } else {
        $b.Group | Select-Object Method -Unique | ForEach-Object {
            Write-Host "    $($_.Method)"
        }
    }
    Write-Host ""
}

# ============================================================================
# Step 5: recommended actions.
# ============================================================================
Write-Host "============================================================"
Write-Host "  Recommended actions"
Write-Host "============================================================"
$bA = ($failures | Where-Object { $_.Bucket -eq 'A' } | Select-Object Method -Unique).Count
$bB = ($failures | Where-Object { $_.Bucket -eq 'B' } | Select-Object Method -Unique).Count
$bC = ($failures | Where-Object { $_.Bucket -eq 'C' } | Select-Object Method -Unique).Count
$bE = ($failures | Where-Object { $_.Bucket -eq 'E' } | Select-Object Method -Unique).Count

if ($bE -gt 0) {
    Write-Host "1. Add [SkippableFact]/Skip.IfNot to the $bE bucket-E tests; defers them cleanly." -ForegroundColor Green
}
if ($bA -gt 0) {
    Write-Host "2. Add [SkippableFact] guards to the $bA bucket-A tests for CI-specific skip." -ForegroundColor Green
}
if ($bC -gt 0) {
    Write-Host "3. Fix the $bC bucket-C tests directly (test is wrong, not the code)." -ForegroundColor Green
}
if ($bB -gt 0) {
    Write-Host "4. Investigate the $bB bucket-B tests — these are real bugs or unclassified." -ForegroundColor Yellow
    Write-Host "   Focus here. Once these reach zero, CI is rock solid." -ForegroundColor Yellow
}

Write-Host ""
