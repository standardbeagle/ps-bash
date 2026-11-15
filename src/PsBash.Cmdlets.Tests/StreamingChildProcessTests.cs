using System.Collections.Generic;
using System.Management.Automation;
using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The pure part of the streaming child spawn (<see cref="ChildOutputAssembler"/>): output arrives in chunks cut
/// wherever the OS pipe cut them, and the records it makes must be exactly the ones the buffered path
/// (<see cref="BashRuntime.EmitBashLines"/> over the whole CRLF-normalized text) made. Hand-written asserts: this is
/// ps-bash's record model, which bash has no counterpart for. Checked at EVERY cut position, CRLF split included.
/// </summary>
public class StreamingChildProcessTests
{
    public static TheoryData<string> Outputs => new()
    {
        "",
        "\n",
        "a",
        "a\n",
        "a\nb",
        "a\nb\n",
        "a\n\n",
        "\n\nx",
        "one\r\ntwo\r\n",
        "one\r\ntwo",
        "lone\rcr\n",
        "ends-with-cr\r",
        "héllo\r\nwörld\n",
    };

    [Theory]
    [MemberData(nameof(Outputs))]
    public void Stdout_AnyChunking_GivesTheBufferedRecords(string output)
    {
        var expected = Records(BashRuntime.EmitBashLines(output.Replace("\r\n", "\n")));
        for (int cut = 0; cut <= output.Length; cut++)
        {
            for (int cut2 = cut; cut2 <= output.Length; cut2++)
            {
                var asm = new ChildOutputAssembler();
                // The records StreamingChildProcess emits for what the assembler hands it.
                var got = new List<object>();
                var lines = new List<string>();
                foreach (var chunk in new[] { output[..cut], output[cut..cut2], output[cut2..] })
                {
                    lines.Clear();
                    asm.Stdout(chunk, lines);
                    foreach (var l in lines) got.Add(BashRuntime.NewBashObject(l));
                }
                if (asm.StdoutEnd() is { } rest)
                    got.Add(BashRuntime.NewBashObject(rest, "PsBash.TextOutput", noTrailingNewline: true));
                Assert.Equal(expected, Records(got));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Outputs))]
    public void Stderr_AnyChunking_GivesTheBufferedText(string output)
    {
        for (int cut = 0; cut <= output.Length; cut++)
        {
            var asm = new ChildOutputAssembler();
            var sb = new StringBuilder();
            sb.Append(asm.Stderr(output[..cut]));
            sb.Append(asm.Stderr(output[cut..]));
            sb.Append(asm.StderrEnd());
            Assert.Equal(output.Replace("\r\n", "\n"), sb.ToString());
        }
    }

    [Fact]
    public void Stderr_PromptWithoutNewline_IsEmittedAtOnce()
    {
        // `read -p "Name: "` writes its prompt to stderr with no newline; holding it for a newline would hide it.
        Assert.Equal("Name: ", new ChildOutputAssembler().Stderr("Name: "));
    }

    // Oracle (bash 5.2): only an argv with no -c and no operand reads its PROGRAM from stdin; `bash -s a b` too.
    // Everything else gets the pipe as data on its stdin, which ps-bash now streams.
    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "-s" }, true)]
    [InlineData(new[] { "-e", "-x" }, true)]
    [InlineData(new[] { "-i" }, false)]
    [InlineData(new[] { "-c", "cat" }, false)]
    [InlineData(new[] { "script.sh" }, false)]
    [InlineData(new[] { "-s", "arg" }, false)]
    [InlineData(new[] { "-" }, false)]
    public void ProgramOnStdin_OnlyWhenTheArgvNamesNoProgram(string[] argv, bool expected)
        => Assert.Equal(expected, InvokeBashBashCommand.ProgramOnStdin(argv));

    private static List<(string, bool)> Records(IEnumerable<object> objects)
    {
        var list = new List<(string, bool)>();
        foreach (var o in objects)
            list.Add((BashRuntime.GetBashText(o), o is PSObject pso && pso.Properties["NoTrailingNewline"]?.Value is true));
        return list;
    }
}
