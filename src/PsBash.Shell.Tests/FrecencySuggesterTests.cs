using Xunit;
using PsBash.Host.Shell;

namespace PsBash.Shell.Tests;

/// <summary>
/// Ghost-text suffix logic for cd/z/zi jump lines (FrecencySuggester), driven by a
/// fake store so the append-correctness rules are asserted deterministically.
/// </summary>
public class FrecencySuggesterTests
{
    private sealed class FakeFrecency : IFrecencyStore
    {
        public IReadOnlyList<FrecencyMatch> Result = Array.Empty<FrecencyMatch>();
        public IReadOnlyList<string>? LastKeywords;

        public Task AddAsync(string path) => Task.CompletedTask;

        public Task<IReadOnlyList<FrecencyMatch>> QueryAsync(IReadOnlyList<string> keywords, int limit = 50)
        {
            LastKeywords = keywords;
            return Task.FromResult(Result);
        }
    }

    private static FakeFrecency WithTop(string path)
        => new() { Result = new[] { new FrecencyMatch { Path = path, Score = 10.0 } } };

    [Fact]
    public async Task EmptyArg_SuggestsFullPath()
    {
        var store = WithTop("/home/user/projects");
        var sut = new FrecencySuggester(store);

        Assert.Equal("/home/user/projects", await sut.SuggestSuffixAsync("z "));
        Assert.Equal("/home/user/projects", await sut.SuggestSuffixAsync("cd "));
        Assert.Empty(store.LastKeywords!);   // empty arg ranks all dirs
    }

    [Fact]
    public async Task ZKeyword_CompletesToBasename()
    {
        var store = WithTop("/home/user/projects");
        var sut = new FrecencySuggester(store);

        var suffix = await sut.SuggestSuffixAsync("z proj");

        Assert.Equal("ects", suffix);                       // "proj" + "ects" = "projects"
        Assert.Equal(new[] { "proj" }, store.LastKeywords);
    }

    [Fact]
    public async Task ZiKeyword_CompletesToBasename()
    {
        var sut = new FrecencySuggester(WithTop("/srv/www/api"));
        Assert.Equal("pi", await sut.SuggestSuffixAsync("zi a"));
    }

    [Fact]
    public async Task CdWithNonEmptyToken_DefersToBaseCompletion()
    {
        // A bare basename is not a guaranteed subdir of cwd, so cd gets no frecency ghost.
        var sut = new FrecencySuggester(WithTop("/home/user/projects"));
        Assert.Null(await sut.SuggestSuffixAsync("cd proj"));
    }

    [Fact]
    public async Task KeywordNotPrefixOfBasename_NoSuggestion()
    {
        var sut = new FrecencySuggester(WithTop("/home/user/myproject"));
        Assert.Null(await sut.SuggestSuffixAsync("z proj"));   // "myproject" doesn't start with "proj"
    }

    [Theory]
    [InlineData("z ./foo")]     // path-like token → base completion owns it
    [InlineData("z ~/d")]
    [InlineData("z /abs")]
    [InlineData("z a b")]       // multi-keyword → Tab handles
    [InlineData("ls foo")]      // not a jump command
    [InlineData("z")]           // no argument region yet
    [InlineData("cdx foo")]     // not exactly cd/z/zi
    public async Task NonApplicableLines_NoSuggestion(string line)
    {
        var sut = new FrecencySuggester(WithTop("/home/user/projects"));
        Assert.Null(await sut.SuggestSuffixAsync(line));
    }

    [Fact]
    public async Task NoMatches_NoSuggestion()
    {
        var sut = new FrecencySuggester(new FakeFrecency());   // empty result
        Assert.Null(await sut.SuggestSuffixAsync("z proj"));
        Assert.Null(await sut.SuggestSuffixAsync("cd "));
    }
}
