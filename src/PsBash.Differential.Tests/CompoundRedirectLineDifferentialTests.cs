using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// The line of a redirect error raised inside a multi-line compound command: bash names the line the outermost
/// statement ENDS on (<c>fi</c>, <c>done</c>, <c>)</c>, <c>}</c>, a here-document's last line), except in a <c>for</c> or
/// <c>case</c> body, which names the <c>for</c> / <c>case</c> line. It named the inner command's first line.
/// docs/specs/error-handling.md "Redirect open errors". Recorded from bash 5.2 (cassette). Streams are captured apart
/// (<c>&gt;out 2&gt;err</c>): only the order within each is compared.
/// </summary>
public class CompoundRedirectLineDifferentialTests
{
    private const string Work = "W=\"$(mktemp -d)\"; cd \"$W\"\n";
    private const string Done = "\ncd /; rm -rf \"$W\"";

    [SkippableFact]
    public Task RedirectErrorInACompoundCommand_NamesTheLineBashDoes()
        => AssertOracle.EqualAsync(Work
            + "cat > s.sh <<'EOF'\n"
            + "for i in 1; do\n"
            + "  echo $i\n"
            + "  { :; } < nofileA\n"
            + "  echo after\n"
            + "done\n"
            + "if true; then\n"
            + "  echo x\n"
            + "  while false; do :; done < nofileB\n"
            + "fi\n"
            + "(\n"
            + "  { :; } < nofileE\n"
            + ")\n"
            + "echo z | while read l; do\n"
            + "  { :; } < nofileF\n"
            + "done\n"
            + "true && {\n"
            + "  { :; } < nofileG\n"
            + "}\n"
            + "case x in\n"
            + "  x)\n"
            + "    { :; } < nofileH\n"
            + "    ;;\n"
            + "esac\n"
            + "for ((i=0; i<1; i++)); do\n"
            + "  echo $i\n"
            + "  { :; } < nofileI\n"
            + "done\n"
            + "for i in 1; do\n"
            + "  :\n"
            + "done < nofileJ\n"
            + "while read l; do\n"
            + "  for j in 1; do :; done\n"
            + "  { :; } < nofileK\n"
            + "done <<EOF2\n"
            + "a\n"
            + "EOF2\n"
            + "echo end\n"
            + "EOF\n"
            + "bash s.sh >out 2>err; cat err; echo ---; cat out" + Done,
            timeout: TimeSpan.FromSeconds(60));
}
