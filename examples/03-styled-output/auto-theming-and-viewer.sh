#!/usr/bin/env ps-bash
# ---------------------------------------------------------------------------
# Auto-theming + the interactive viewer.
# Run static parts:  ps-bash examples/03-styled-output/auto-theming-and-viewer.sh
# The Show-Styled viewer is interactive (needs a real terminal) — notes below.
# ---------------------------------------------------------------------------

echo "== ping auto-themes by latency (the 'net' sheet, no -Style needed) =="
# Invoke-BashPing emits PingReply objects carrying a latency class
# (.ok / .slow / .high / .timeout), so the right sheet is chosen automatically.
ping -c 3 example.com | Format-Styled

echo
echo "== Make styled output the default for ALL object output =="
# Bash commands keep their fast text path; only PowerShell objects get styled.
#   export PSBASH_DEFAULT_FORMAT=styled        # static ANSI everywhere
#   export PSBASH_DEFAULT_FORMAT=interactive   # full-screen viewer in a TTY

# ---------------------------------------------------------------------------
# Show-Styled (INTERACTIVE): a full-screen, navigable, expandable viewer that
# auto-picks a sheet from the first row's kind:
#   FileInfo/DirectoryInfo -> fs    Process/Service -> procsvc
#   ErrorRecord -> error            PingReply/TraceHop -> net    else -> object
# Keys:  up/down or j/k move, Enter/Space expands a row's detail, q quits.
# Try it in `ps-bash`:
#   ls -la | Show-Styled
#   ping example.com | Show-Styled
# ---------------------------------------------------------------------------
echo "Done. In 'ps-bash' try:  ls -la | Show-Styled"
