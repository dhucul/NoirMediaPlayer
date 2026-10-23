using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class SubtitleAlignmentTests
{
    internal static string FileText(string text) => $"1\n00:00:01,000 --> 00:00:04,000\n{text}\n";

    [Theory]
    [InlineData(1, SubtitleAlignment.BottomLeft)]
    [InlineData(2, SubtitleAlignment.BottomCenter)]
    [InlineData(3, SubtitleAlignment.BottomRight)]
    [InlineData(4, SubtitleAlignment.MiddleLeft)]
    [InlineData(5, SubtitleAlignment.MiddleCenter)]
    [InlineData(6, SubtitleAlignment.MiddleRight)]
    [InlineData(7, SubtitleAlignment.TopLeft)]
    [InlineData(8, SubtitleAlignment.TopCenter)]
    [InlineData(9, SubtitleAlignment.TopRight)]
    public void LeadingSubRipAlignmentKeepsTextAndInlineStyleRanges(int number, object alignment)
    {
        var track = PlainSubtitleTrack.Parse("alignment.srt", FileText($"{{\\an{number}}}<b>日本語 😀</b>\n<font color=\"red\"><u>café</u></font>"));
        Assert.NotNull(track);
        var cue = track.CueAt(1500)!;
        Assert.Equal((SubtitleAlignment)alignment, cue.Alignment);
        Assert.Equal("日本語 😀\ncafé", cue.Text);
        Assert.Equal(new[]
        {
            new SubtitleStyleRange(0, 6, SubtitleStyle.Bold),
            new SubtitleStyleRange(7, 4, SubtitleStyle.Underline, 0xFF0000)
        }, cue.Styles);
        Assert.Null(track.CueAt(999));
        Assert.Null(track.CueAt(4000));
    }

    [Fact]
    public void AlignmentBelongsToEachCueAndDoesNotLeakAcrossIdenticalText()
    {
        var track = PlainSubtitleTrack.Parse("alignment.srt",
            FileText("{\\an8}Same") + "\n2\n00:00:04,000 --> 00:00:06,000\n{\\an5}Same\n\n3\n00:00:06,000 --> 00:00:08,000\nSame\n")!;
        Assert.Equal(SubtitleAlignment.TopCenter, track.CueAt(1500)!.Alignment);
        Assert.Equal(SubtitleAlignment.MiddleCenter, track.CueAt(4500)!.Alignment);
        Assert.Equal(SubtitleAlignment.BottomCenter, track.CueAt(6500)!.Alignment);
        Assert.Equal(track.TextAt(1500), track.TextAt(6500));
        Assert.Equal(SubtitleAlignment.TopCenter, track.CueAt(1500)!.Alignment);
    }

    [Theory]
    [InlineData("{\\an0}Zero")]
    [InlineData("{\\an10}Two digits")]
    [InlineData("{\\an-1}Negative")]
    [InlineData("{\\an}Missing value")]
    [InlineData("{\\an8Missing brace")]
    [InlineData("{\\an8 }Space")]
    [InlineData("{\\an 8}Space")]
    [InlineData("{\\AN8}Uppercase")]
    [InlineData("{\\a6}Legacy alignment")]
    [InlineData("{\\an8\\pos(10,20)}Combined override")]
    [InlineData("{\\an8}{\\pos(10,20)}Position")]
    [InlineData("{\\an8}{\\an7}Multiple alignments")]
    [InlineData("{\\an8}{\\move(0,0,10,10)}Animation")]
    [InlineData("{\\an8}{\\fs30}Size")]
    [InlineData("{\\an8}{\\k20}Karaoke")]
    [InlineData("{\\an8}<font face=\"Arial\">Font</font>")]
    [InlineData(" {\\an8}Leading space")]
    [InlineData("<b>{\\an8}Inside span</b>")]
    [InlineData("Dialogue{\\an8}Later override")]
    [InlineData("First\n{\\an8}Later line")]
    [InlineData("{\\an8}")]
    public void UnreadyOrMalformedOverridesRetainTheOriginalCompatibilityMeaning(string text) =>
        Assert.Null(PlainSubtitleTrack.Parse("alignment.srt", FileText(text)));

    [Theory]
    [InlineData(".vtt")]
    [InlineData(".webvtt")]
    [InlineData(".ass")]
    [InlineData(".ssa")]
    public void SubRipAlignmentDoesNotAdmitOtherSubtitleFormats(string extension)
    {
        var content = extension is ".vtt" or ".webvtt"
            ? "WEBVTT\n\n00:01.000 --> 00:04.000\n{\\an8}Top\n"
            : "[Script Info]\nScriptType: v4.00+\n[Events]\nDialogue: {\\an8}Top\n";
        Assert.Null(PlainSubtitleTrack.Parse("alignment" + extension, content));
    }

    [Fact]
    public void AlignmentRetainsWholeCueLengthAndUnicodeLimits()
    {
        Assert.NotNull(PlainSubtitleTrack.Parse("alignment.srt", FileText("{\\an8}" + new string('A', 2042))));
        Assert.Null(PlainSubtitleTrack.Parse("alignment.srt", FileText("{\\an8}" + new string('A', 2043))));
        Assert.Null(PlainSubtitleTrack.Parse("alignment.srt", FileText("{\\an8}One\nTwo\nThree\nFour\nFive")));
        Assert.Null(PlainSubtitleTrack.Parse("alignment.srt", FileText("{\\an8}<b>" + (char)0xD83D + "</b>" + (char)0xDE00)));
    }
}
