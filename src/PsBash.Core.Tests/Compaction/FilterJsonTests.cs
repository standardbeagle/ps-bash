using PsBash.Core.Runtime.Compaction;
using Xunit;

namespace PsBash.Core.Tests.Compaction;

public class FilterJsonTests
{
    [Fact]
    public void ParseFile_SingleObject_BindsAllFields()
    {
        const string json = """
        {
          "name": "git/status",
          "match": { "command": "git", "args": ["status"] },
          "override": ["git", "status", "--porcelain"],
          "matchOutput": [{ "contains": "nothing to commit", "emit": "clean" }],
          "replace": [{ "pattern": "^\\s+", "with": "" }],
          "stripAnsi": true,
          "trimLines": true,
          "skip": ["^On branch"],
          "keep": [],
          "dedup": true,
          "onSuccess": "ok {{body}}",
          "onFailure": "{{body}}"
        }
        """;

        var specs = FilterJson.ParseFile(json);

        var spec = Assert.Single(specs);
        Assert.Equal("git/status", spec.Name);
        Assert.Equal("git", spec.Match.Command);
        Assert.Equal(["status"], spec.Match.Args);
        Assert.Equal(["git", "status", "--porcelain"], spec.Override);
        Assert.Equal("nothing to commit", Assert.Single(spec.MatchOutput).Contains);
        Assert.Equal("clean", spec.MatchOutput[0].Emit);
        Assert.Equal("^\\s+", Assert.Single(spec.Replace).Pattern);
        Assert.True(spec.StripAnsi);
        Assert.True(spec.TrimLines);
        Assert.Equal("^On branch", Assert.Single(spec.Skip));
        Assert.True(spec.Dedup);
        Assert.Equal("ok {{body}}", spec.OnSuccess);
    }

    [Fact]
    public void ParseFile_Array_ReturnsAllSpecs()
    {
        const string json = """
        [
          { "name": "a", "match": { "command": "x" } },
          { "name": "b", "match": { "command": "y" } }
        ]
        """;

        var specs = FilterJson.ParseFile(json);

        Assert.Equal(2, specs.Count);
        Assert.Equal("a", specs[0].Name);
        Assert.Equal("b", specs[1].Name);
    }

    [Fact]
    public void ParseFile_PropertyNamesAreCaseInsensitive()
    {
        const string json = """{ "Name": "t", "Match": { "Command": "tool" }, "OnSuccess": "ok" }""";

        var spec = Assert.Single(FilterJson.ParseFile(json));

        Assert.Equal("t", spec.Name);
        Assert.Equal("tool", spec.Match.Command);
        Assert.Equal("ok", spec.OnSuccess);
    }

    [Fact]
    public void ParseFile_ToleratesCommentsAndTrailingCommas()
    {
        const string json = """
        {
          // leading comment
          "name": "t",
          "match": { "command": "tool", },
        }
        """;

        var spec = Assert.Single(FilterJson.ParseFile(json));
        Assert.Equal("t", spec.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseFile_EmptyOrWhitespace_ReturnsEmpty(string? json)
        => Assert.Empty(FilterJson.ParseFile(json!));

    [Fact]
    public void ParseFile_OmittedCollections_DefaultToEmptyNotNull()
    {
        var spec = Assert.Single(FilterJson.ParseFile("""{ "name": "t", "match": { "command": "tool" } }"""));

        Assert.Empty(spec.Match.Args);
        Assert.Empty(spec.Skip);
        Assert.Empty(spec.Keep);
        Assert.Empty(spec.Replace);
        Assert.Empty(spec.MatchOutput);
        Assert.Null(spec.Override);
        Assert.Null(spec.OnSuccess);
        Assert.False(spec.Dedup);
    }

    [Fact]
    public void ParseFile_MalformedJson_Throws()
        => Assert.ThrowsAny<System.Text.Json.JsonException>(() => FilterJson.ParseFile("{ not json"));
}
