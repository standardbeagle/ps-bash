# Command Assist Specification

How the ps-bash interactive shell turns a natural-language prompt into a reviewed,
optionally-executed shell command by shelling out to an external AI CLI (Claude by
default). This is an **interactive-only** feature — it has no transpiler or runtime
surface; it lives entirely in the host shell.

Source files (all under `src/PsBash.Host/Shell/`, namespace `PsBash.Host.Shell`):

| File | Owns |
|------|------|
| `CommandAssistProvider.cs` | Config load/normalize, provider process spawn, prompt-template rendering + secret redaction, provider-output parsing. |
| `CommandAssistReview.cs` | The dangerous-command classifier (`CommandAssistSafety`), the review request/decision records, and `ApplyDecision` (the authority that decides execute vs insert vs cancel). |
| `LineEditor.cs` | The Ctrl-^ hotkey detection (`IsCommandAssistKey`), the `HandleCommandAssistAsync` buffer dance, and the `CommandAssistResponse`/`CommandAssistRequest` contract types. |
| `InteractiveShell.cs` | Wiring: builds the runner, applies `PSBASH_AI_DISABLE`, and runs the interactive review loop (`RunCommandAssistWithReviewAsync` + the `Prompt*` helpers). |

---

## 1. Data flow

```
key = Ctrl-^ (or Ctrl-` / Ctrl-6)
  └─ LineEditor.IsCommandAssistKey → HandleCommandAssistAsync(ct)
       ├─ if no provider configured → print notice, restore buffer
       └─ _commandAssist(CommandAssistRequest{Buffer,Cursor}, ct)        (InteractiveShell delegate)
            └─ RunCommandAssistWithReviewAsync(runner, request, cwd, ct)
                 loop:
                   ├─ runner.GenerateAsync(request, cwd, providerName, ct)   → spawns AI CLI
                   │     └─ render template → Process → parse output → CommandAssistProviderResult
                   ├─ PromptCommandAssistReview(review)                       → reads stdin
                   │     ├─ retry          → loop again (same provider)
                   │     ├─ switch provider → pick name, loop again
                   │     └─ execute/insert/cancel → CommandAssistReview.ApplyDecision
                   └─ returns CommandAssistResponse{Execute|Insert|Cancel, Command}
       └─ Execute → LineEditor returns the command to the shell (it runs)
          Insert  → buffer replaced with the command (user edits/runs)
          Cancel  → original buffer restored
```

The provider call is bounded by the LineEditor's cancellation token; any
`CommandAssistProviderException` (or other exception) is caught in
`HandleCommandAssistAsync`, printed as `ps-bash: command assist failed: <msg>`, and
the original buffer is restored. **The hotkey never leaves the line in a broken state.**

---

## 2. Hotkey

`IsCommandAssistKey` matches any of:

- `` (Ctrl-^ — the canonical binding, RS control char)
- `Ctrl` + `Oem3` (Ctrl-`` ` ``)
- `Ctrl` + `D6` (Ctrl-6)

These are aliases for the same terminal control code across keyboard layouts/terminals.

---

## 3. Configuration

### 3.1 Resolution order (`CommandAssistConfig.Load`)

1. `PSBASH_AI_CONFIG` env var → explicit path.
2. else `{PSBASH_HOME or %UserProfile%}/.psbash/ai-providers.json`.

If the file does not exist → the built-in default config (a single `claude` provider).
If the file exists but is unreadable / malformed JSON → `CommandAssistProviderException`
(surfaced to the user; the feature is treated as misconfigured, not crashed).

`PSBASH_AI_DISABLE` set to exactly `1` (an `== "1"` check in `InteractiveShell`, not
the general truthy parsing used elsewhere) disables the feature entirely —
`commandAssistConfigError` is set and the hotkey reports it instead of spawning anything.

### 3.2 Config schema (`ai-providers.json`)

```json
{
  "defaultProvider": "claude",
  "providers": [
    {
      "name": "claude",
      "executable": "claude",
      "args": ["-p", "{{prompt}}"],
      "promptTemplate": "…optional override…",
      "workingDirectory": null,
      "environment": { "ANTHROPIC_API_KEY": "{{cwd}}-never-do-this" },
      "timeoutMs": 30000,
      "outputLimit": 8192
    }
  ]
}
```

| Field | Default | Notes |
|-------|---------|-------|
| `defaultProvider` | first provider's name | Used when the user does not pick one. |
| `name` | — (required) | Empty name → `CommandAssistProviderException`. |
| `executable` | — (required) | Empty → exception. Spawn failure (ENOENT) → friendly "not found" error. |
| `args` | `["{{prompt}}"]` | The literal arg `"{{prompt}}"` is replaced by the full rendered prompt; other args are template-rendered too. |
| `promptTemplate` | built-in Claude template | See §4. |
| `workingDirectory` | the shell's cwd | Process working dir. |
| `environment` | `{}` | Applied to the child; a `null` value **removes** that env var; values are template-rendered. |
| `timeoutMs` | `30000` | `<= 0` normalizes to 30000. Hard kill (entire process tree) on timeout. |
| `outputLimit` | `8192` | Max chars captured per stream; the reader keeps draining past the cap so the child never blocks on a full pipe. |

`Normalize()` fills defaults; `Resolve(name)` throws if a requested provider is absent.

---

## 4. Prompt template

`RenderTemplate` substitutes (all Ordinal, no regex injection):

| Token | Value |
|-------|-------|
| `{{buffer}}` | the current input line, **redacted** then truncated to 2000 chars |
| `{{cursor}}` | cursor index as a string |
| `{{cwd}}` | working directory, redacted then truncated to 1000 chars |
| `{{shell}}` | literal `ps-bash` |
| `{{os}}` | `Environment.OSVersion.VersionString` |

`{{prompt}}` is **not** a `RenderTemplate` token — it is handled one level up, in the
args loop, by an exact arg-equality check: an `args` entry equal to `"{{prompt}}"` is
replaced by the whole rendered prompt, while every other arg is passed through
`RenderTemplate` (see §3.2).

**Redaction** (`Redact`): a compiled regex blanks the value of any
`token`/`secret`/`password`/`passwd`/`apikey`/`api_key`/`access_key` `=`-assignment to
`<key>=<redacted>` before the text leaves the process. This is best-effort hygiene, not
a guarantee — the buffer is still sent to an external program.

The built-in Claude template asks for **compact JSON only** (see §5) and instructs the
model to fill `command` only for a single reviewable command, else use
`refusal`/`clarification`/`plan`.

---

## 5. Provider output contract

`ParseProviderOutput` → `NormalizeProviderOutput` first strips Markdown ``` fences.

- **Structured** (first char `{` or `[`): parsed as JSON object with optional fields
  `command`, `explanation`, `refusal`, `clarification`, `plan` (string array).
  - `command` non-empty → `IsExecutable = true`, carries `explanation`.
  - `command` empty → **non-executable**; display text = first non-empty of
    `refusal` → `clarification` → joined `plan` → `explanation`.
  - Malformed JSON / non-object → non-executable, "review it before inserting".
- **Plain text**: a single line → executable; multi-line → non-executable (treated as
  prose, not a command).
- Empty output, or exit code `!= 0`, or a structured result with an empty `command`
  → `CommandAssistProviderException` ("returned no command") in the runner's
  `RunAsync`/`GenerateAsync` path.

Every result is wrapped into a `CommandAssistReviewRequest` carrying `IsExecutable`,
the cwd, and the safety `Warnings` (§6).

---

## 6. Safety classifier (`CommandAssistSafety.Classify`)

A static list of compiled regexes flags potentially destructive commands. Each match
yields a `CommandAssistSafetyFinding { Pattern, Reason }`:

| Label | Matches (case-insensitive) | Reason |
|-------|----------------------------|--------|
| `rm` | `rm` + flags/operand | removes files |
| `git force/reset` | `git reset --hard`, `git clean -f`, `git push --force` | can discard or overwrite work |
| `delete` | `del`, `erase`, `Remove-Item` | removes files |
| `overwrite redirect` | `>\|` | forces overwrite |
| `privilege escalation` | `sudo`, `Start-Process -Verb RunAs` | runs elevated |
| `network install` | `curl`/`wget`/`irm`/`iwr`/… piped into `sh`/`bash`/`pwsh`/`powershell` | pipes network content into a shell |
| `invoke-expression` | `iex`, `Invoke-Expression` | executes a dynamically built string as code (e.g. `iex (irm url)`) |
| `package install` | `npm`/`pnpm`/`yarn`/`pip`/`pipx`/`gem`/`cargo`/`dotnet` + `install`/`add`/`global`/`tool` | installs code or tools |

This is an **advisory heuristic**, not a security boundary — the real boundary is the
human review (§7) plus the `EXECUTE` confirmation gate. It is intentionally
PowerShell-aware (the shell is PowerShell-backed), hence the `Remove-Item` / `iex` /
`Start-Process -Verb RunAs` entries.

---

## 7. Review loop (`InteractiveShell`)

`RunCommandAssistWithReviewAsync` loops: generate → `PromptCommandAssistReview` → act.

`PromptCommandAssistReview` prints provider/cwd/explanation/command, a review-only note
when `!IsExecutable`, and any warnings, then reads a stdin action:

| Input | Action | Guard |
|-------|--------|-------|
| `e`/`execute` | Execute | only offered when `IsExecutable`; if there are warnings, `ConfirmDangerousCommand` requires typing literal **`EXECUTE`** |
| `i`/`insert`/`edit` | Insert into the line buffer | — |
| `r`/`retry` | regenerate from the same provider | — |
| `s`/`switch` | pick another configured provider, regenerate | empty pick → cancel |
| anything else / `c` | Cancel (default) | — |

`CommandAssistReview.ApplyDecision` is the single authority and re-checks the guards
independently of the prompt UI:

```
Insert                                   → CommandAssistResponse.Insert(command)
Execute  when IsExecutable
         and (Warnings.Count == 0 || DangerousConfirmed) → CommandAssistResponse.Execute(command)
_                                        → CommandAssistResponse.Cancelled
```

So a non-executable result can never execute, and a warned command can never execute
without `DangerousConfirmed` — even if a future caller bypasses the prompt.

`SelectCommandAssistProvider` resolves a typed provider name case-insensitively against
the configured list (empty / unknown → null → cancel).

---

## 8. Process-spawn discipline

The provider spawn follows the project's hard rule (see [[process_spawn_contract]] in
MEMORY): `RedirectStandardOutput`/`Error`, started reads **before** `WaitForExitAsync`
(no pipe-buffer deadlock), a linked `CancellationTokenSource` with
`CancelAfter(TimeoutMs)`, `Kill(entireProcessTree: true)` on timeout, and bounded
draining reads. Spawn/IO failures map to `CommandAssistProviderException` with a
diagnostic message; they are never thrown raw into the line editor.

---

## 9. Scope / non-goals

- Interactive shell only. No `-c` / stdin / script-mode entry point.
- Tier-2 `complete -F` style dynamic completion is unrelated (see
  [interactive-completion.md](./interactive-completion.md)).
- The provider is any CLI that reads a prompt arg and writes the §5 contract to stdout;
  Claude (`claude -p`) is the default but nothing is Claude-specific in the host code.
