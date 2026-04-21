# PowerShell Typing UX Optimization — Requirements

**Date:** 2026-04-21  
**Status:** Approved for planning  
**Scope:** Interactive shell typing experience

---

## Problem Statement

Users in ps-bash bridge bash and PowerShell workflows, but the typing experience creates friction:
- Bash users face PowerShell verbosity and ceremony
- PowerShell users face bash-syntax-first mental model
- Keystrokes are higher than ideal for common operations
- User's mental model (what they want to type) often mismatches available syntax

**Goal:** Make PowerShell typing feel like a natural, keystroke-minimal extension of bash, where user expectations match reality and patterns are familiar across both shells.

---

## Success Criteria

1. **Keystroke efficiency** — Users type fewer characters for equivalent operations vs. native PowerShell
2. **Bash bridging** — Bash-first users can work naturally without learning PowerShell ceremony
3. **Mental model alignment** — What users type matches what they expect to happen (no surprise interpretations)
4. **Consistency** — Rules are predictable across both bash and PowerShell; users build reliable intuition
5. **Familiarity** — Patterns feel recognizable from other shells (bash, zsh, fish, PowerShell itself)

---

## Recommended Approach (3-Part Strategy)

### **Part 1: Smart Inference Engine (Core)**

Users type in either bash or PowerShell syntax; the shell automatically detects and executes correctly.

**Rules:**
- Bash command names (ls, grep, ps, etc.) → interpreted as bash
- PowerShell cmdlets (Get-Process, Select-Object, etc.) → interpreted as PowerShell
- Mixed pipelines with object property access → detected as hybrid and handled intelligently
  - Example: `ps aux | sort -k3 -rn | head` → bash throughout
  - Example: `Get-Process | Where-Object { $_.CPU -gt 5 }` → PowerShell throughout
  - Example: `ls -la | select Name, Size` → hybrid (bash ls, PowerShell select on result)

**Benefits:**
- Zero extra typing (no escape sequences, mode switches, or prefixes)
- Familiar syntax for both user groups
- Reduces cognitive load — "type what you mean"

**Risk:** Edge cases where intent is ambiguous (handled via explicit opt-in or contextual heuristics in planning phase)

### **Part 2: Progressive Hint System (Accelerator)**

As users type bash, hint when a PowerShell approach would save keystrokes or be more natural.

**Behavior:**
- User types bash command
- Completion system silently generates PowerShell equivalent
- If keystroke count is notably lower (threshold: 15%+ savings), show hint in status line or as subtle suggestion
- User can accept hint with Ctrl+Alt+P or continue with bash

**Examples:**
- User: `ps aux | sort -k3 -rn | head`
- Hint: "PowerShell: `ps | sort CPU -desc | select -first 1`"
- User: `grep -r "pattern" .`
- Hint: "PowerShell: `Select-String -Path * -Pattern 'pattern' -Recurse`"

**Benefits:**
- Teaches without forcing
- Respects bash-first muscle memory
- Zero cost to ignore; cumulative learning over time
- Discoverable for power users

**Non-goal:** Don't hint on every command (noise threshold); only when there's genuine advantage

### **Part 3: Syntactic Sugar Layer (Bridging)**

For the 15-20% of operations where bash and PowerShell differ most, add lightweight affordances that feel natural to both groups.

**Syntax extensions (examples, final list in planning):**
- Property access without Select-Object: `ls -la | select Name, Size` (not `Select-Object -Property Name, Size`)
- Object filtering inline: `ps | where CPU -gt 5` (not `Where-Object { $_.CPU -gt 5 }`)
- Pipe into property: `data | .count` (shorthand for `Select-Object -ExpandProperty count`)
- Object construction: `{Name=.Name, CPU=.CPU}` in pipelines

**Rules:**
- Must feel like a natural extension of bash idiom, not a separate syntax
- Must not conflict with existing bash meaning
- Must be obvious from context (no hidden behaviors)

**Benefits:**
- Bridges the mental model gap for the most common operations
- Reduces ceremony without full PowerShell verbosity
- Familiar to users from other shell experience

---

## Out of Scope (for This Phase)

- Full DSL or new language
- Mode switching (Ctrl+B/Ctrl+P to toggle modes)
- Interactive command builders or visual TUIs for filtering
- Dual-cursor or side-by-side syntax rendering
- Heavy abbreviation system (Direction 5 deferred; can be added later as low-cost accelerator)

---

## Key Decisions

1. **Bash and PowerShell are peers, not master/slave** — Users shouldn't feel forced to pick one. Both syntaxes are first-class.

2. **Inference is opt-out, not opt-in** — Smart detection should happen automatically. Users don't need to declare mode.

3. **Hints are optional, not required** — Progressive disclosure respects existing muscle memory and doesn't break flow.

4. **Sugar is syntactic, not semantic** — Extensions should map to existing PowerShell/bash concepts, not invent new ones.

5. **Consistency over cleverness** — Rules must be learnable and predictable, even if they're slightly verbose in edge cases.

---

## Open Questions for Planning

1. **Inference ambiguity handling** — How do we detect and resolve conflicts when the same command could mean both bash and PowerShell? (e.g., `select` is both bash and PowerShell)
   
2. **Sugar syntax final list** — Which 5-10 extensions deliver the highest keystroke + mental-model ROI?

3. **Hint tuning** — What keystroke savings threshold justifies showing a hint? Where do we show hints (status line, inline, tooltip)?

4. **Completion integration** — How does the new approach interact with existing tab completion, history search, and autosuggestions?

5. **Error messages** — When inference gets it wrong, what does the user see and how do they recover?

6. **Backwards compatibility** — Does the existing bash transpilation still work? (Should be yes, but confirm in planning)

---

## Related Context

- **LineEditor refactoring** (2026-04-21): Auto-redraw architecture is now solid; ready for new feature integration
- **Existing specs:**
  - `docs/specs/completion-providers.md` — current completion architecture
  - `docs/specs/autosuggestions.md` — suggestion system
  - `docs/specs/lineeditor-vt100-design.md` — LineEditor internals
- **Test foundation:** 857 passing tests provide safety net for changes

---

## Acceptance Criteria for Planning Phase

Plan should address:
- [ ] Inference algorithm and ambiguity resolution
- [ ] Final sugar syntax list with examples
- [ ] Integration points with LineEditor, completion, autosuggestions
- [ ] Error handling and recovery paths
- [ ] Test strategy (unit + integration)
- [ ] Phasing (what's MVP vs. phase 2)
