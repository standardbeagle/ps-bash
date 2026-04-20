# Known Issues

## `eval "$(cmd)"` is rejected at parse time

**Status:** by design as of v0.8.14.

**Symptom:** transpiling `eval "$(any-command)"`, `eval \`cmd\``, or `eval $((expr))`
raises a `ParseException` with a message ending in
"inline the command's output statically instead of running it inside eval".

**Why it's this way:** `eval` is resolved at **parse time** in `PsEmitter.EmitEval`.
The arg's `CompoundWord` parts are walked and the bash source the args
represent is reconstructed, then fed back through `BashTranspiler.Transpile`
inline. For static bodies (literals, quoted literals, variable references)
this is lossless — the reconstructed source re-parses to the same effective
AST as if the user had typed the eval body at top level.

Command substitution (`$(cmd)` / backquotes), arithmetic expansion (`$((...))`),
and process substitution (`<(...)` / `>(...)` ) all compute the eval body at
*runtime*, which the transpiler cannot reach. These raise `ParseException`
instead of silently hanging or producing broken pwsh.

**Workaround:** replace `eval "$(cmd)"` with the literal output of `cmd`.
For `fnm`:

```bash
# Regenerate this block when the active node version changes:
#   fnm env --shell bash
# then paste the lines here, replacing the `eval` line.
export PATH="…/fnm_multishells/…":"$PATH"
export FNM_MULTISHELL_PATH="…"
export FNM_DIR="…"
# …and so on.
```

Same pattern works for `direnv hook bash`, `ssh-agent -s`, `dircolors`, and
any other `eval "$(tool shell-init)"` idiom — one-time paste, no runtime eval.

**Regression guards:** `Transpile_EvalWithCommandSubstitution_ThrowsParseException`,
`Transpile_EvalWithBackquoteCommandSub_ThrowsParseException`, and
`Transpile_EvalWithArithmeticExpansion_ThrowsParseException` in
`BashTranspilerTests`.
