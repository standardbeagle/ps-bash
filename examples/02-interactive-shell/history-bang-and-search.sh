#!/usr/bin/env ps-bash
# ---------------------------------------------------------------------------
# Bang-history (!!, !$, ^old^new) and Ctrl-R — walkthrough.
# Bang expansion runs even non-interactively, so the first half is runnable:
#   ps-bash examples/02-interactive-shell/history-bang-and-search.sh
# Ctrl-R is interactive only (notes at the bottom).
# ---------------------------------------------------------------------------

echo "ps-bash brings bash bang-history to a PowerShell-backed shell."

git status            # pretend this was a real command
# !!  -> the previous command in full. In an interactive session:
#   sudo !!           expands to: sudo git status

echo /etc/hosts
# !$  -> last argument of the previous command:
#   cat !$            expands to: cat /etc/hosts

# ^old^new -> quick substitution on the previous command (first match):
echo wip
#   ^wip^done         re-runs the echo as: echo done

# Other forms:
#   !n      command number n in this session (1-based)
#   !-n     the n-th command back (!-1 == !!)
#   !str    most recent command starting with str
#   !?str?  most recent command containing str

# ---------------------------------------------------------------------------
# Ctrl-R (interactive only): a full-screen, fuzzy, RANKED history search —
# closer to fzf/atuin than bash's one-line (reverse-i-search).
#   - type to filter; ranking boosts commands run in THIS directory, recently,
#     and often.
#   - Ctrl-R / Ctrl-S cycle matches, arrows move the selection.
#   - Ctrl-G toggles current-dir vs all-history scope.
#   - Tab edits the selected command before running; Enter runs it.
# Try it: launch `ps-bash`, run a few commands, then press Ctrl-R.
# ---------------------------------------------------------------------------
echo "Done. Launch 'ps-bash' and press Ctrl-R to try ranked history search."
