using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class SubtitleStyleTests
{
    internal static string FileText(string extension, string content) => extension == ".vtt"
        ? $"WEBVTT\n\n00:01.000 --> 00:04.000\n{content}\n"
        : $"1\n00:00:01,000 --> 00:00:04,000\n{content}\n";

    [Theory]
    [InlineData(".srt")]
    [InlineData(".vtt")]
    public void NestedInlineStyles_PreserveTextUnicodeAndCharacterRanges(string extension)
    {
        var track = PlainSubtitleTrack.Parse("captions" + extension, FileText(extension, "A <b>bold <i>café <u>日本語 😀</u></i></b> plain"));
        Assert.NotNull(track);
        var cue = track.CueAt(1500);
        Assert.NotNull(cue);
        Assert.Equal("A bold café 日本語 😀 plain", cue.Text);
        Assert.Equal(new[]
        {
            new SubtitleStyleRange(2, 5, SubtitleStyle.Bold),
            new SubtitleStyleRange(7, 5, SubtitleStyle.Bold | SubtitleStyle.Italic),
            new SubtitleStyleRange(12, 6, SubtitleStyle.Bold | SubtitleStyle.Italic | SubtitleStyle.Underline)
        }, cue.Styles);
        Assert.Null(track.CueAt(999));
        Assert.Same(cue, track.CueAt(3999));
        Assert.Null(track.CueAt(4000));
        Assert.Same(cue, track.CueAt(1000)); // Backward seeks preserve the full style object.
    }

    [Theory]
    [InlineData(".srt")]
    [InlineData(".vtt")]
    public void EscapedMarkup_IsDecodedOnceAndDoesNotBecomeAStyle(string extension)
    {
        var track = PlainSubtitleTrack.Parse("captions" + extension, FileText(extension, "&lt;b&gt;literal&lt;/b&gt; &amp;lt; <i>A&amp;B&nbsp;C</i>"));
        Assert.NotNull(track);
        var cue = track.CueAt(1000)!;
        Assert.Equal("<b>literal</b> &lt; A&B\u00A0C", cue.Text);
        var style = Assert.Single(cue.Styles);
        Assert.Equal("A&B\u00A0C", cue.Text.Substring(style.Start, style.Length));
        Assert.Equal(SubtitleStyle.Italic, style.Style);
    }

    [Fact]
    public void RepeatedNestedStylesAndMultilineRuns_RestoreTheParentStyle()
    {
        var track = PlainSubtitleTrack.Parse("captions.srt", FileText(".srt", "<b>A<b>B</b>C\n<i>D</i>E</b> F<u>G</u>"));
        Assert.NotNull(track);
        var cue = track.CueAt(1000)!;
        Assert.Equal("ABC\nDE FG", cue.Text);
        Assert.Equal(new[]
        {
            new SubtitleStyleRange(0, 4, SubtitleStyle.Bold),
            new SubtitleStyleRange(4, 1, SubtitleStyle.Bold | SubtitleStyle.Italic),
            new SubtitleStyleRange(5, 1, SubtitleStyle.Bold),
            new SubtitleStyleRange(8, 1, SubtitleStyle.Underline)
        }, cue.Styles);
    }

    [Fact]
    public void IdenticalTextCanHaveDifferentStylesInConsecutiveCues()
    {
        var track = PlainSubtitleTrack.Parse("captions.srt", "1\n00:00:01,000 --> 00:00:02,000\nSame\n\n2\n00:00:02,000 --> 00:00:03,000\n<i>Same</i>\n");
        Assert.NotNull(track);
        Assert.Equal(track.TextAt(1500), track.TextAt(2500));
        Assert.Empty(track.CueAt(1500)!.Styles);
        Assert.Equal(SubtitleStyle.Italic, Assert.Single(track.CueAt(2500)!.Styles).Style);
    }

    [Theory]
    [InlineData("<b>Unclosed")]
    [InlineData("Unexpected</i>")]
    [InlineData("<b><i>Crossed</b></i>")]
    [InlineData("<i class=\"x\">Attributes</i>")]
    [InlineData("<I>Uppercase</I>")]
    [InlineData("<b/>Self closing")]
    [InlineData("<i >Whitespace</i>")]
    [InlineData("<i\n>Whitespace</i>")]
    [InlineData("<i>Missing end")]
    [InlineData("<font color=\"red\" size=\"20\">Font attributes</font>")]
    [InlineData("<ruby>Ruby</ruby>")]
    [InlineData("<c.custom>Class</c>")]
    [InlineData("<v>Voice")]
    [InlineData("Karaoke <00:01.500>text")]
    [InlineData("Break<br>line")]
    [InlineData("<i>{\\an8}Positioned</i>")]
    [InlineData("&unknown;")]
    [InlineData("&#65;")]
    [InlineData("&#x41;")]
    [InlineData("<i></i>")]
    public void UnsupportedOrMalformedMarkup_CannotBeSilentlyStripped(string caption)
    {
        foreach (var extension in new[] { ".srt", ".vtt" })
            Assert.Null(PlainSubtitleTrack.Parse("captions" + extension, FileText(extension, caption)));
    }

    [Fact]
    public void SubRipDialogueAndNamedEscapes_AreKeptWithoutChangingWebVttRules()
    {
        var track = PlainSubtitleTrack.Parse("captions.srt", FileText(".srt", "Tom & Jerry &quot;said&quot; &apos;Hi&apos;"));
        Assert.Equal("Tom & Jerry \"said\" 'Hi'", track?.TextAt(1000));
        Assert.Null(PlainSubtitleTrack.Parse("captions.vtt", FileText(".vtt", "&quot;Unsupported in WebVTT&quot;")));
        var webVtt = PlainSubtitleTrack.Parse("captions.vtt", FileText(".vtt", "<u>&lrm;LTR &rlm;RTL</u>"));
        Assert.Equal("\u200ELTR \u200FRTL", webVtt?.TextAt(1000));
    }

    [Fact]
    public void StyleDepthRangeAndSizeLimits_KeepCompatibilityAvailable()
    {
        var deep = string.Concat(Enumerable.Repeat("<i>", 17)) + "Text" + string.Concat(Enumerable.Repeat("</i>", 17));
        Assert.Null(PlainSubtitleTrack.Parse("captions.srt", FileText(".srt", deep)));
        var runs = "<b>" + string.Concat(Enumerable.Repeat("A<i>B</i>", 129)) + "</b>";
        Assert.True(runs.Length <= 2048);
        Assert.Null(PlainSubtitleTrack.Parse("captions.srt", FileText(".srt", runs)));
        Assert.Null(PlainSubtitleTrack.Parse("captions.srt", FileText(".srt", "<i>" + new string('X', 2048) + "</i>")));
    }

    [Fact]
    public void StyleBoundariesCannotSplitASurrogatePair()
    {
        var caption = "<b>" + (char)0xD83D + "</b>" + (char)0xDE00;
        foreach (var extension in new[] { ".srt", ".vtt" })
            Assert.Null(PlainSubtitleTrack.Parse("captions" + extension, FileText(extension, caption)));
    }

    [Fact]
    public void CueStyleRanges_DoNotRetainTheCallersMutableArray()
    {
        SubtitleStyleRange[] ranges = [new(0, 1, SubtitleStyle.Bold)];
        var cue = new SubtitleCue(0, 1000, "A", ranges);
        ranges[0] = new SubtitleStyleRange(0, 1, SubtitleStyle.Italic);
        Assert.Equal(SubtitleStyle.Bold, Assert.Single(cue.Styles).Style);
    }
}
