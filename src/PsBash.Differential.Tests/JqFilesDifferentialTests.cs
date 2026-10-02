using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// jq 1.7 options that read files (<c>-f</c>, <c>--slurpfile</c>, <c>--rawfile</c>, several input files, <c>input_filename</c>, <c>-s</c>
/// over files), compared on stdout, exit status and the filesystem (jq writes nothing, so the tree is a canary). Diagnostics are not
/// compared here (the wording is in <c>JqDifferentialTests</c> and the cmdlet tests).
/// </summary>
public class JqFilesDifferentialTests
{
    private static string Files() => Tree(("a.json", "{\"a\":1}"), ("b.json", "[1,2]\n3"), ("prog.jq", ".a + 1"), ("multi.jq", "def f: . * 2;\n# comment\n.a | f"), ("raw.txt", "line1"), ("stream.json", "1 2 3"));

    [SkippableFact] public Task ProgramFile() => EqualAsync(Files(), "jq -f prog.jq a.json");
    [SkippableFact] public Task ProgramFile_Long() => EqualAsync(Files(), "jq --from-file prog.jq a.json");
    [SkippableFact] public Task ProgramFile_Stdin() => EqualAsync(Files(), "jq -f prog.jq < a.json");
    [SkippableFact] public Task ProgramFile_DefsAndComments() => EqualAsync(Files(), "jq -f multi.jq a.json");
    [SkippableFact] public Task ProgramFile_Missing() => EqualAsync(Files(), "jq -f nosuch.jq a.json");
    [SkippableFact] public Task ProgramFile_WithNullInput() => EqualAsync(Files(), "jq -n -f prog.jq");

    [SkippableFact] public Task SlurpFile() => EqualAsync(Files(), "jq -nc --slurpfile s stream.json '$s'");
    [SkippableFact] public Task SlurpFile_MultipleDocs() => EqualAsync(Files(), "jq -nc --slurpfile s b.json '$s, ($s | length)'");
    [SkippableFact] public Task SlurpFile_Missing() => EqualAsync(Files(), "jq -nc --slurpfile s nosuch.json '$s'");
    [SkippableFact] public Task RawFile() => EqualAsync(Files(), "jq -n --rawfile r raw.txt '$r'");
    [SkippableFact] public Task RawFile_Missing() => EqualAsync(Files(), "jq -n --rawfile r nosuch.txt '$r'");
    [SkippableFact] public Task NamedArgs_AllKinds() => EqualAsync(Files(), "jq -nc --arg a 1 --argjson b '[1]' --slurpfile s stream.json --rawfile r raw.txt '$ARGS'");
    [SkippableFact] public Task NamedArgs_UsedAsVariables() => EqualAsync(Files(), "jq -nc --arg a 1 --argjson b '{\"x\":2}' --slurpfile s stream.json '[$a, $b.x, $s[1]]'");

    [SkippableFact] public Task SeveralFiles() => EqualAsync(Files(), "jq -c . a.json b.json");
    [SkippableFact] public Task SeveralFiles_Slurp() => EqualAsync(Files(), "jq -sc . a.json b.json");
    [SkippableFact] public Task SeveralFiles_NullInputInputs() => EqualAsync(Files(), "jq -nc '[inputs]' a.json b.json");
    [SkippableFact] public Task SeveralFiles_InputFilename() => EqualAsync(Files(), "jq -c '[., input_filename]' a.json b.json");
    [SkippableFact] public Task SeveralFiles_RawInput() => EqualAsync(Files(), "jq -R . raw.txt a.json");
    [SkippableFact] public Task SeveralFiles_RawInputSlurp() => EqualAsync(Files(), "jq -Rs . raw.txt a.json");
    [SkippableFact] public Task SingleFile_Operand() => EqualAsync(Files(), "jq -c '.a' a.json");
    [SkippableFact] public Task Stdin_DashOperand() => EqualAsync(Files(), "jq -c . - < a.json");
    [SkippableFact] public Task Stdin_FilenameIsNull() => EqualAsync(Files(), "jq -c 'input_filename' < a.json");
    [SkippableFact] public Task Files_OptionsAfterOperands() => EqualAsync(Files(), "jq . a.json -c");
    [SkippableFact] public Task Files_MissingFile_ExitsTwo() => EqualAsync(Files(), "jq -c . nosuch.json");
    [SkippableFact] public Task Files_MissingFirst_OthersStillRun() => EqualAsync(Files(), "jq -c . nosuch.json a.json");
    [SkippableFact] public Task Files_MalformedFile() => EqualAsync(Tree(("bad.json", "{\"a\":")), "jq -c . bad.json");
    [SkippableFact] public Task Files_PositionalArgsAfterArgs() => EqualAsync(Files(), "jq -nc '$ARGS.positional' --args a.json b.json");
    [SkippableFact] public Task Files_JsonArgs() => EqualAsync(Files(), "jq -nc '$ARGS.positional' --jsonargs 1 2");
    [SkippableFact] public Task Files_ErrorLocationNamesTheFile() => EqualAsync(Files(), "jq '.a.b' a.json");
    [SkippableFact] public Task Files_Stream() => EqualAsync(Files(), "jq -c --stream . a.json b.json");
    [SkippableFact] public Task Files_ExitStatus() => EqualAsync(Files(), "jq -e '.a == 2' a.json");
    [SkippableFact] public Task Files_RedirectedOutput() => EqualAsync(Files(), "jq -c . a.json > out.txt; cat out.txt");
    [SkippableFact] public Task Files_PipelineInto() => EqualAsync(Files(), "jq -r '.a' a.json | head -n 1");
}
