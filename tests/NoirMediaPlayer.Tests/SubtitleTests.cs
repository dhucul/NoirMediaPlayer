using System.IO;
using System.Text;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class SubtitleTests
{
    [Fact]
    public void PlainSrt_HandlesCueBoundariesGapsMultilineAndBackwardSeeks()
    {
        var track = PlainSubtitleTrack.Parse("captions.srt", "\uFEFF1\r\n00:00:01,000 --> 00:00:02,000\r\nFirst\r\nline\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\n日本語 café\r\n");
        Assert.NotNull(track);
        Assert.Equal(string.Empty, track.TextAt(999));
        Assert.Equal("First\nline", track.TextAt(1000));
        Assert.Equal("First\nline", track.TextAt(1999));
        Assert.Equal(string.Empty, track.TextAt(2000));
        Assert.Equal("日本語 café", track.TextAt(3000));
        Assert.Equal(string.Empty, track.TextAt(4000));
        Assert.Equal("First\nline", track.TextAt(1200));
    }

    [Theory]
    [InlineData("<font color=\"#FF0000\" face=\"Arial\">Font attributes</font>")]
    [InlineData("{\\an8\\pos(10,20)}Positioned")]
    [InlineData("{y:b}Bold")]
    [InlineData("One\nTwo\nThree\nFour\nFive")]
    [InlineData("Bad\0text")]
    public void Srt_LeavesUnsupportedMarkupOrUnusualContentToVlc(string caption) =>
        Assert.Null(PlainSubtitleTrack.Parse("captions.srt", $"1\n00:00:01,000 --> 00:00:02,000\n{caption}\n"));

    [Theory]
    [InlineData("00:00:02,000 --> 00:00:01,000")]
    [InlineData("00:00:01,000 --> 00:00:01,000")]
    [InlineData("00:61:01,000 --> 00:62:02,000")]
    [InlineData("00:00:01,000 --> 00:00:02,000 X1:0 X2:100")]
    public void PlainSrt_DoesNotDiscardTimingOrPositioningInformation(string timing) =>
        Assert.Null(PlainSubtitleTrack.Parse("captions.srt", $"1\n{timing}\nCaption\n"));

    [Fact]
    public void PlainSrt_OverlappingCuesRetainCompatibilityRendering() =>
        Assert.Null(PlainSubtitleTrack.Parse("captions.srt", "1\n00:00:01,000 --> 00:00:03,000\nFirst\n\n2\n00:00:02,000 --> 00:00:04,000\nSecond\n"));

    [Fact]
    public void PlainSrt_UsesBomEncodingAndRejectsInvalidUtf8()
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirCaptions-" + Guid.NewGuid() + ".srt");
        try
        {
            File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nFrançais\n", Encoding.Unicode);
            Assert.Equal("Français", PlainSubtitleTrack.TryLoad(path)?.TextAt(1000));
            File.WriteAllBytes(path, [0xFF, 0xFF, 0xFF]);
            Assert.Null(PlainSubtitleTrack.TryLoad(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PlainWebVtt_PreservesTimingIdentifiersCommentsAndCharacterReferences()
    {
        var track = PlainSubtitleTrack.Parse("captions.vtt", "\uFEFFWEBVTT Example\r\n\r\nNOTE ignored comment\r\nComment text\r\n\r\nintro\r\n00:01.000 --> 00:02.500\r\nCafé &amp; 日本語\r\nSecond line\r\n\r\n2\r\n01:00:03.000 --> 01:00:04.000\r\n&lt;literal&gt; &nbsp;&lrm;&rlm; &amp;lt;\r\n");
        Assert.NotNull(track);
        Assert.Equal(string.Empty, track.TextAt(999));
        Assert.Equal("Café & 日本語\nSecond line", track.TextAt(1000));
        Assert.Equal(string.Empty, track.TextAt(2500));
        Assert.Equal("<literal> \u00A0\u200E\u200F &lt;", track.TextAt(3_603_000));
        Assert.Equal(string.Empty, track.TextAt(3_604_000));
        Assert.Equal("Café & 日本語\nSecond line", track.TextAt(1200));
    }

    [Theory]
    [InlineData("WEBVTT\n\nSTYLE\n::cue { color: red }\n\n00:01.000 --> 00:02.000\nCaption\n")]
    [InlineData("WEBVTT\n\nREGION\nid:top\n\n00:01.000 --> 00:02.000\nCaption\n")]
    [InlineData("WEBVTT\nX-TIMESTAMP-MAP=LOCAL:00:00:00.000,MPEGTS:900000\n\n00:01.000 --> 00:02.000\nCaption\n")]
    [InlineData("WEBVTT\n\n00:01.000 --> 00:02.000 line:0\nCaption\n")]
    [InlineData("WEBVTT\n\n00:01.000 --> 00:02.000\n<c.custom>Custom class</c>\n")]
    [InlineData("WEBVTT\n\n00:01.000 --> 00:02.000\n<v >Voice\n")]
    [InlineData("WEBVTT\n\n00:01.000 --> 00:02.000\nKaraoke <00:01.500>text\n")]
    [InlineData("WEBVTT\n\n00:01.000 --> 00:03.000\nFirst\n\n00:02.000 --> 00:04.000\nOverlapping\n")]
    [InlineData("WEBVTT\n\n00:60.000 --> 00:61.000\nBad timing\n")]
    [InlineData("WEBVTT\n\n00:02.000 --> 00:01.000\nBackwards\n")]
    [InlineData("WEBVTT\n\n00:01.000 --> 00:02.000\n&#65; &unknown;\n")]
    [InlineData("00:01.000 --> 00:02.000\nMissing header\n")]
    public void PlainWebVtt_UsesCompatibilityForUnsupportedMeaning(string text) =>
        Assert.Null(PlainSubtitleTrack.Parse("captions.vtt", text));

    [Theory]
    [InlineData(".vtt")]
    [InlineData(".webvtt")]
    [InlineData(".WEBVTT")]
    public void PlainWebVtt_RequiresUtf8AndAcceptsItsBom(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirCaptions-" + Guid.NewGuid() + extension);
        const string text = "WEBVTT\n\n00:01.000 --> 00:02.000\nFrançais\n";
        try
        {
            File.WriteAllText(path, text, new UTF8Encoding(true));
            Assert.Equal("Français", PlainSubtitleTrack.TryLoad(path)?.TextAt(1000));
            File.WriteAllText(path, text, Encoding.Unicode);
            Assert.Null(PlainSubtitleTrack.TryLoad(path));
            File.WriteAllBytes(path, [0xFF, 0xFF, 0xFF]);
            Assert.Null(PlainSubtitleTrack.TryLoad(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("WEBVTT\n\nSTYLE\n::cue { color: red; }\n\n00:01.000 --> 00:02.000\nCaption\n")]
    [InlineData("WEBVTT\n\n00:01.000 --> 00:02.000 line:0\nCaption\n")]
    [InlineData("WEBVTT\n\n00:01.000 --> 00:02.000\n<c.custom>Caption</c>\n")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nSubRip content in a WebVTT file\n")]
    public void WebVttAliasDoesNotBypassContentValidation(string text) =>
        Assert.Null(PlainSubtitleTrack.Parse("captions.webvtt", text));
}
