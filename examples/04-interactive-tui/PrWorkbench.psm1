# ===========================================================================
# PrWorkbench.psm1 — a custom `browse` workbench, built entirely in PowerShell.
#
#   Import-Module PsBash
#   Import-Module ./examples/04-interactive-tui/PrWorkbench.psm1
#   Get-DemoPullRequests | browse        # n/p move, s select, a action, q quit
#
# This is "interactive TUI construction from a psm1 file": define actions, wrap
# them in an adapter for your object type, register it. No C#, no TUI framework.
# ===========================================================================

# 1. Actions. A scriptblock receives $Current (focused row) and $Items (the
#    selection). Mark mutating ones -Destructive so they preview unless -Force.
$approve = New-BrowseAction -Name 'approve' -Description 'Approve this PR' -Script {
    param($Current, [object[]]$Items)
    foreach ($pr in $Items) { "would: gh pr review $($pr.Number) --approve" }
}

$close = New-BrowseAction -Name 'close' -Description 'Close PR' -Destructive -Script {
    param($Current, [object[]]$Items)
    foreach ($pr in $Items) { "would: gh pr close $($pr.Number)" }
}

# 2. Register an adapter: which columns to show + which actions apply, keyed by
#    type name. Register-BrowseAdapter runs in PsBash's own scope (a bare
#    `$script:BrowseAdapters += ...` from your module would write the wrong scope),
#    and user adapters take precedence over the built-ins.
Register-BrowseAdapter -Name 'pr' `
    -TypeNames @('Demo.PullRequest') `
    -DisplayProperties @('Number', 'Title', 'Author', 'State') `
    -Actions @($approve, $close)

# --- A demo data source so the example is self-contained --------------------
function Get-DemoPullRequests {
    @(
        [PSCustomObject]@{ PSTypeName = 'Demo.PullRequest'; Number = 412; Title = 'Fix rg-shim transpile'; Author = 'andy';  State = 'open'  }
        [PSCustomObject]@{ PSTypeName = 'Demo.PullRequest'; Number = 408; Title = 'Add z/zi docs';          Author = 'robin'; State = 'draft' }
        [PSCustomObject]@{ PSTypeName = 'Demo.PullRequest'; Number = 401; Title = 'Styled output guide';    Author = 'sam';   State = 'open'  }
    )
}

Export-ModuleMember -Function Get-DemoPullRequests
