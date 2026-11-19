using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// Prompt <c>\#</c> and <c>\!</c> in <c>${PS1@P}</c> of a script file are the shell reader's counts: <c>\#</c> the
/// top-level input lines read (<c>a; b</c> and a multi-line <c>if</c> are one each; a <c>$(...)</c> sees its line's
/// number; an <c>eval</c>'s text is not read), <c>\!</c> the history entries under <c>set -o history</c>, which
/// <c>history</c> lists as bash's <c>cmdhist</c> joins them. Both were fixed at 1, and <c>set -o history</c> recorded
/// nothing (oils prompt.spec "\! for history number", "\# for command number"; environment-model.md section 10).
/// </summary>
public class PromptCounterDifferentialTests
{
    private const string Script = """
        cat > s.sh <<'EOF'
        PS1='n=\# h=\!'
        echo "${PS1@P}"
        echo a; echo "${PS1@P}"
        if true; then
          echo "if ${PS1@P}"
        fi
        x=$(echo "${PS1@P}"); echo "sub $x"
        eval 'echo "ev ${PS1@P}"'; echo "after ${PS1@P}"
        set -o history
        history -c
        echo "${PS1@P}"
        for i in 1 2
        do echo "$i ${PS1@P}"
        done
        echo "_${PS1@P}"
        history
        EOF
        """;

    [SkippableFact]
    public Task ScriptFile_PromptCounters_CountInputLinesAndHistory() => EqualAsync(Script, "bash s.sh; echo st=$?");
}
