using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class SubtitleColorTests
{
    [Theory]
    [InlineData("#ff124A", 0xFF124Au)]
    [InlineData("black", 0x000000u)]
    [InlineData("silver", 0xC0C0C0u)]
    [InlineData("gray", 0x808080u)]
    [InlineData("white", 0xFFFFFFu)]
    [InlineData("maroon", 0x800000u)]
    [InlineData("RED", 0xFF0000u)]
    [InlineData("purple", 0x800080u)]
    [InlineData("fuchsia", 0xFF00FFu)]
    [InlineData("green", 0x008000u)]
    [InlineData("lime", 0x00FF00u)]
    [InlineData("olive", 0x808000u)]
    [InlineData("yellow", 0xFFFF00u)]
    [InlineData("navy", 0x000080u)]
    [InlineData("blue", 0x0000FFu)]
    [InlineData("teal", 0x008080u)]
    [InlineData("aqua", 0x00FFFFu)]
    [InlineData("grey", 0x808080u)]
    [InlineData("magenta", 0xFF00FFu)]
    [InlineData("cyan", 0x00FFFFu)]
    [InlineData("orange", 0xFFA500u)]
    public void SubRipColorsKeepOpaqueRgbValues(string name, uint rgb)
    {
        foreach (var attribute in new[] { $"font color=\"{name}\"", $"font color='{name}'", $"font\tcolor \t= {name} " })
        {
            var track = Parse(".srt", $"<{attribute}>Colored 日本語 😀</font>");
            Assert.NotNull(track);
            var cue = track.CueAt(1000)!;
            Assert.Equal("Colored 日本語 😀", cue.Text);
            Assert.Equal(new SubtitleStyleRange(0, cue.Text.Length, SubtitleStyle.None, rgb), Assert.Single(cue.Styles));
        }
    }

    [Theory]
    [InlineData("white", 0xFFFFFFu)]
    [InlineData("lime", 0x00FF00u)]
    [InlineData("cyan", 0x00FFFFu)]
    [InlineData("red", 0xFF0000u)]
    [InlineData("yellow", 0xFFFF00u)]
    [InlineData("magenta", 0xFF00FFu)]
    [InlineData("blue", 0x0000FFu)]
    [InlineData("black", 0x000000u)]
    public void WebVttStandardForegroundClassesKeepTheirDefinedColors(string name, uint rgb)
    {
        var track = Parse(".vtt", $"<c.{name}><i>Caption</i></c>");
        Assert.NotNull(track);
        Assert.Equal(new SubtitleStyleRange(0, 7, SubtitleStyle.Italic, rgb), Assert.Single(track.CueAt(1000)!.Styles));
    }

    [Theory]
    [InlineData(".srt")]
    [InlineData(".vtt")]
    public void NestedColorsRestoreBothParentColorAndInlineStyles(string extension)
    {
        var red = extension == ".srt" ? "font color='red'" : "c.red";
        var blue = extension == ".srt" ? "font color='#0000FF'" : "c.blue";
        var close = extension == ".srt" ? "font" : "c";
        var track = Parse(extension, $"A<{red}>B<b>C<{blue}>D<i>😀</i></{close}>E</b>F</{close}>G");
        Assert.NotNull(track);
        var cue = track.CueAt(1000)!;
        Assert.Equal("ABCD😀EFG", cue.Text);
        Assert.Equal(new[]
        {
            new SubtitleStyleRange(1, 1, SubtitleStyle.None, 0xFF0000),
            new SubtitleStyleRange(2, 1, SubtitleStyle.Bold, 0xFF0000),
            new SubtitleStyleRange(3, 1, SubtitleStyle.Bold, 0x0000FF),
            new SubtitleStyleRange(4, 2, SubtitleStyle.Bold | SubtitleStyle.Italic, 0x0000FF),
            new SubtitleStyleRange(6, 1, SubtitleStyle.Bold, 0xFF0000),
            new SubtitleStyleRange(7, 1, SubtitleStyle.None, 0xFF0000)
        }, cue.Styles);
    }

    [Fact]
    public void WebVttEmptyClassInheritsItsParentWithoutDroppingFormatting()
    {
        var track = Parse(".vtt", "<b><c.red>A<c>B</c>C</c>D</b>");
        Assert.NotNull(track);
        Assert.Equal(new[]
        {
            new SubtitleStyleRange(0, 3, SubtitleStyle.Bold, 0xFF0000),
            new SubtitleStyleRange(3, 1, SubtitleStyle.Bold)
        }, track.CueAt(1000)!.Styles);
    }

    [Theory]
    [InlineData("<font>Missing color</font>")]
    [InlineData("<font color='transparent'>Alpha</font>")]
    [InlineData("<font color='#80FF0000'>Alpha</font>")]
    [InlineData("<font color='#F00'>Shorthand</font>")]
    [InlineData("<font color='#GG0000'>Invalid</font>")]
    [InlineData("<font color='rgb(255,0,0)'>CSS</font>")]
    [InlineData("<font color='red' face='Arial'>Font</font>")]
    [InlineData("<font color='red' color='blue'>Duplicate</font>")]
    [InlineData("<font COLOR='red'>Attribute case</font>")]
    [InlineData("<font color='red>Missing quote</font>")]
    [InlineData("<font color=red'>Stray quote</font>")]
    [InlineData("<font color= red blue>Two values</font>")]
    [InlineData("<font color=\" red \">Whitespace in value</font>")]
    [InlineData("<font color='red'><b>Crossed</font></b>")]
    [InlineData("<font color='red'>Unclosed")]
    [InlineData("<font color='red'/>Self closing")]
    public void UnsupportedSubRipColorSyntaxRetainsCompatibility(string text) => Assert.Null(Parse(".srt", text));

    [Theory]
    [InlineData("<c.custom>Custom</c>")]
    [InlineData("<c.RED>Case</c>")]
    [InlineData("<c.green>Nonstandard class</c>")]
    [InlineData("<c.#FF0000>Hex class</c>")]
    [InlineData("<c.red.blue>Multiple classes</c>")]
    [InlineData("<c.red.bg_blue.custom>Custom background class</c>")]
    [InlineData("<c.red class='x'>Attribute</c>")]
    [InlineData("<c.red>Crossed</i>")]
    [InlineData("<c.red>Unclosed")]
    [InlineData("<c.red >Whitespace</c>")]
    [InlineData("<font color='red'>SubRip markup</font>")]
    public void UnsupportedWebVttClassesRetainCompatibility(string text) => Assert.Null(Parse(".vtt", text));

    [Fact]
    public void ColorEscapesStayLiteralAndDoNotCrossFormatBoundaries()
    {
        var track = Parse(".srt", "&lt;font color='red'&gt;literal&lt;/font&gt; <font color='blue'>&amp;lt;</font>");
        Assert.NotNull(track);
        Assert.Equal("<font color='red'>literal</font> &lt;", track.TextAt(1000));
        var range = Assert.Single(track.CueAt(1000)!.Styles);
        Assert.Equal("&lt;", track.TextAt(1000).Substring(range.Start, range.Length));
        Assert.Equal(0x0000FFu, range.Foreground);
        Assert.Null(Parse(".srt", "<c.red>WebVTT markup</c>"));
    }

    [Theory]
    [InlineData(".srt")]
    [InlineData(".vtt")]
    public void ColorMarkupObeysTheExistingDepthAndUnicodeBoundaries(string extension)
    {
        var open = extension == ".srt" ? "<font color='red'>" : "<c.red>";
        var close = extension == ".srt" ? "</font>" : "</c>";
        var deep = string.Concat(Enumerable.Repeat(open, 17)) + "A" + string.Concat(Enumerable.Repeat(close, 17));
        Assert.Null(Parse(extension, deep));
        Assert.Null(Parse(extension, open + (char)0xD83D + close + (char)0xDE00));
    }

    private static PlainSubtitleTrack? Parse(string extension, string text) =>
        PlainSubtitleTrack.Parse("captions" + extension, SubtitleStyleTests.FileText(extension, text));
}
