using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The <c>av</c> stylesheet is wired to the media object kinds. Strata-gated (compiled out when the
/// styling cmdlets are not built), like the other styled-output tests.
/// Oracle note (Directive 1): ps-bash-specific styling surface, no bash equivalent.
/// </summary>
public class MediaStyleSheetTests
{
    [Theory]
    [InlineData("MediaInfo")]
    [InlineData("MediaArtifact")]
    public void MediaKinds_AutoSelectTheAvSheet(string kind)
    {
        Assert.Equal("av", StyledStyles.AutoStyleForKind(kind));
    }

    [Fact]
    public void AvSheet_IsEmbeddedAndStylesBothVocabularies()
    {
        var css = StyledStyles.Resolve("av");

        Assert.Contains("MediaInfo", css);
        Assert.Contains("MediaArtifact", css);
        foreach (var cls in new[] { ".video", ".audio", ".image", ".ok", ".failed", ".planned" })
        {
            Assert.Contains(cls, css);
        }
    }
}
