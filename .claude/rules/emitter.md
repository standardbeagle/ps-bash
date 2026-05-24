---
paths:
  - "src/PsBash.Transpiler/Parser/PsEmitter.cs"
---

# EMITTER. Ref: @docs/specs/emitter-strategy.md

文言：透傳為本——映命名、轉全參，旗由運行時解。勿譯旗、勿抽旗、勿臆旗、勿映原生cmdlet。逗號花括號之旗須引號。

## PASSTHROUGH PRINCIPLE
Map bash command name → `Invoke-Bash*`, forward ALL args unchanged. Runtime parses flags.

NEVER in the emitter:
- translate bash flag → PS named param (`-d` → `-Delimiter`).
- extract + re-emit specific flags (`-n N` out of head).
- assume which flags a command supports.
- map to native PS cmdlets (`Select-Object`/`Measure-Object`/`Sort-Object`).

## PIPE-TARGET MAPPING
- all pipe targets in `TryEmitMappedCommand` use `EmitPassthrough`.
- add a command: case in `TryEmitMappedCommand` → `EmitPassthrough("Invoke-BashFoo", args)`. No custom `EmitFoo` unless quoting needs it.

## QUOTING (NeedsPassthroughQuoting)
Quote a flag arg containing `,` (PS array sep) or `{`/`}` (PS scriptblock): emit `"-F,"`, `"-I{}"`.
