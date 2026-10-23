using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class SubtitleBackgroundTests
{
    [Theory]
    [InlineData("white", 0xFFFFFFu)]
    [InlineData("lime", 0x00FF00u)]
    [InlineData("cyan", 0x00FFFFu)]
    [InlineData("red", 0xFF0000u)]
    [InlineData("yellow", 0xFFFF00u)]
    [InlineData("magenta", 0xFF00FFu)]
    [InlineData("blue", 0x0000FFu)]
    [InlineData("black", 0x000000u)]
    public void WebVttBackgroundsKeepTheirStandardOpaqueColors(string name, uint rgb)
    {
        var track = Parse($"<c.bg_{name}><b>日本語 😀</b></c>");
        Assert.NotNull(track);
        var cue = track.CueAt(1000)!;
        Assert.Equal("日本語 😀", cue.Text);
        Assert.Equal(new SubtitleStyleRange(0, cue.Text.Length, SubtitleStyle.Bold, Background: rgb), Assert.Single(cue.Styles));
    }

    [Theory]
    [InlineData("yellow.bg_blue")]
    [InlineData("bg_blue.yellow")]
    public void ForegroundAndBackgroundClassesCanAppearInEitherOrder(string classes)
    {
        var track = Parse($"<c.{classes}><i>Caption</i></c>");
        Assert.NotNull(track);
        Assert.Equal(new SubtitleStyleRange(0, 7, SubtitleStyle.Italic, 0xFFFF00, 0x0000FF), Assert.Single(track.CueAt(1000)!.Styles));
    }

    [Fact]
    public void NestedBackgroundsRestoreParentColorsAndStylesAcrossUnicodeAndLines()
    {
        var track = Parse("A<c.red.bg_blue>B<b>C<c.bg_yellow>D<i>😀</i></c>E</b>F</c>G\n<c.bg_lime><c>H</c></c>I");
        Assert.NotNull(track);
        var cue = track.CueAt(1000)!;
        Assert.Equal("ABCD😀EFG\nHI", cue.Text);
        Assert.Equal(new[]
        {
            new SubtitleStyleRange(1, 1, SubtitleStyle.None, 0xFF0000, 0x0000FF),
            new SubtitleStyleRange(2, 1, SubtitleStyle.Bold, 0xFF0000, 0x0000FF),
            new SubtitleStyleRange(3, 1, SubtitleStyle.Bold, 0xFF0000, 0xFFFF00),
            new SubtitleStyleRange(4, 2, SubtitleStyle.Bold | SubtitleStyle.Italic, 0xFF0000, 0xFFFF00),
            new SubtitleStyleRange(6, 1, SubtitleStyle.Bold, 0xFF0000, 0x0000FF),
            new SubtitleStyleRange(7, 1, SubtitleStyle.None, 0xFF0000, 0x0000FF),
            new SubtitleStyleRange(10, 1, SubtitleStyle.None, Background: 0x00FF00)
        }, cue.Styles);
    }

    [Theory]
    [InlineData("<c.bg_green>Nonstandard</c>")]
    [InlineData("<c.bg_Blue>Case</c>")]
    [InlineData("<c.bg_#0000FF>Hex</c>")]
    [InlineData("<c.bg_transparent>Alpha</c>")]
    [InlineData("<c.bg_blue.bg_red>Competing backgrounds</c>")]
    [InlineData("<c.bg_blue.bg_blue>Duplicate backgrounds</c>")]
    [InlineData("<c.red.blue>Competing text colors</c>")]
    [InlineData("<c.red.red.bg_blue>Duplicate text colors</c>")]
    [InlineData("<c.red.bg_blue.custom>Custom class</c>")]
    [InlineData("<c.red.bg_blue.bg_yellow>Multiple backgrounds</c>")]
    [InlineData("<c.bg_blue.>Trailing separator</c>")]
    [InlineData("<c..bg_blue>Empty class</c>")]
    [InlineData("<c.bg_blue >Whitespace</c>")]
    [InlineData("<c.bg_blue style='color:red'>CSS attribute</c>")]
    [InlineData("<c.bg_blue>Unclosed")]
    [InlineData("<c.bg_blue><b>Crossed</c></b>")]
    public void UnsupportedOrAmbiguousBackgroundMarkupRetainsCompatibility(string text) => Assert.Null(Parse(text));

    [Fact]
    public void BackgroundMarkupKeepsExistingDepthStyleCountAndSurrogateLimits()
    {
        Assert.Null(Parse(string.Concat(Enumerable.Repeat("<c.bg_blue>", 17)) + "A" + string.Concat(Enumerable.Repeat("</c>", 17))));
        Assert.Null(Parse(string.Concat(Enumerable.Repeat("<c.bg_blue>A</c>B", 257))));
        Assert.Null(Parse("<c.bg_blue>" + (char)0xD83D + "</c>" + (char)0xDE00));
        Assert.Null(PlainSubtitleTrack.Parse("caption.srt", SubtitleStyleTests.FileText(".srt", "<c.bg_blue>WebVTT only</c>")));
        var literal = Parse("&lt;c.bg_blue&gt;literal&lt;/c&gt;");
        Assert.NotNull(literal);
        Assert.Empty(literal.CueAt(1000)!.Styles);
        Assert.Equal("<c.bg_blue>literal</c>", literal.TextAt(1000));
    }

    private static PlainSubtitleTrack? Parse(string text) => PlainSubtitleTrack.Parse("caption.vtt", SubtitleStyleTests.FileText(".vtt", text));
}
