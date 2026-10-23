using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class SubtitleLanguageTests
{
    internal static string FileText(string content) => $"WEBVTT\n\n00:01.000 --> 00:04.000\n{content}\n";

    [Theory]
    [InlineData("en", "en")]
    [InlineData("en-US", "en-US")]
    [InlineData("EN-us", "en-US")]
    [InlineData("fr-FR", "fr-FR")]
    [InlineData("ja", "ja")]
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-Hant-TW", "zh-Hant-TW")]
    [InlineData("zh-CN", "zh-Hans-CN")]
    [InlineData("ZH-tw", "zh-Hant-TW")]
    [InlineData("ar-SA", "ar-SA")]
    [InlineData("hi-IN", "hi-IN")]
    [InlineData("th-TH", "th-TH")]
    [InlineData("es-419", "es-419")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("de-DE", "de-DE")]
    public void KnownLanguageNamesAreRetainedWithoutBecomingDialogue(string name, string canonical)
    {
        var track = Parse($"<lang {name}>日本語 😀 café</lang>");
        Assert.NotNull(track);
        var cue = track.CueAt(1500)!;
        Assert.Equal("日本語 😀 café", cue.Text);
        Assert.Equal(new SubtitleStyleRange(0, cue.Text.Length, SubtitleStyle.None, Language: canonical), Assert.Single(cue.Styles));
        Assert.Null(track.CueAt(999));
        Assert.Null(track.CueAt(4000));
        Assert.Null(PlainSubtitleTrack.Parse("language.srt", SubtitleStyleTests.FileText(".srt", $"<lang {name}>Dialogue</lang>")));
    }

    [Fact]
    public void LanguageNestedWithColorsAndStylesRestoresItsParents()
    {
        var cue = Parse("A<lang.yellow.bg_blue ja>日<i>本</i><lang ar-SA><u>ع</u></lang>語</lang>Z")!.CueAt(1500)!;
        Assert.Equal("A日本ع語Z", cue.Text);
        Assert.Equal(new[]
        {
            new SubtitleStyleRange(1, 1, SubtitleStyle.None, 0xFFFF00, 0x0000FF, "ja"),
            new SubtitleStyleRange(2, 1, SubtitleStyle.Italic, 0xFFFF00, 0x0000FF, "ja"),
            new SubtitleStyleRange(3, 1, SubtitleStyle.Underline, 0xFFFF00, 0x0000FF, "ar-SA"),
            new SubtitleStyleRange(4, 1, SubtitleStyle.None, 0xFFFF00, 0x0000FF, "ja")
        }, cue.Styles);
    }

    [Fact]
    public void VoiceAndLanguageSpansKeepTheirIndependentScopes()
    {
        var cue = Parse("<v Narrator><lang ja>日<lang en-US><b>A</b></lang>本</lang> Plain")!.CueAt(1500)!;
        Assert.Equal("日A本 Plain", cue.Text);
        Assert.Equal(new[]
        {
            new SubtitleStyleRange(0, 1, SubtitleStyle.None, Language: "ja"),
            new SubtitleStyleRange(1, 1, SubtitleStyle.Bold, Language: "en-US"),
            new SubtitleStyleRange(2, 1, SubtitleStyle.None, Language: "ja")
        }, cue.Styles);
        Assert.Null(Parse("<v Narrator><lang ja>Missing language end"));
    }

    [Fact]
    public void IdenticalConsecutiveTextCanChangeLanguageAndThenRestoreDefault()
    {
        const string text = "WEBVTT\n\n00:00.000 --> 00:02.000\n<lang ja>同じ</lang>\n\n00:02.000 --> 00:04.000\n<lang zh-Hans>同じ</lang>\n\n00:04.000 --> 00:06.000\n同じ\n";
        var track = PlainSubtitleTrack.Parse("language.vtt", text)!;
        Assert.Equal(track.TextAt(1000), track.TextAt(3000));
        Assert.Equal("ja", Assert.Single(track.CueAt(1000)!.Styles).Language);
        Assert.Equal("zh-Hans", Assert.Single(track.CueAt(3000)!.Styles).Language);
        Assert.Empty(track.CueAt(5000)!.Styles);
    }

    [Fact]
    public void LanguageAnnotationWhitespaceAndEscapedLiteralMarkupStayDistinct()
    {
        var cue = Parse("<lang\t ja-JP \t>&lt;lang ar-SA&gt;literal&lt;/lang&gt;</lang>")!.CueAt(1500)!;
        Assert.Equal("<lang ar-SA>literal</lang>", cue.Text);
        Assert.Equal("ja-JP", Assert.Single(cue.Styles).Language);
    }

    [Theory]
    [InlineData("<lang>Missing language</lang>")]
    [InlineData("<lang >Missing language</lang>")]
    [InlineData("<lang en_US>Underscore</lang>")]
    [InlineData("<lang en US>Multiple values</lang>")]
    [InlineData("<lang en--US>Empty component</lang>")]
    [InlineData("<lang -en>Leading hyphen</lang>")]
    [InlineData("<lang en->Trailing hyphen</lang>")]
    [InlineData("<lang 123>Numeric language</lang>")]
    [InlineData("<lang x-private>Private language</lang>")]
    [InlineData("<lang qzz>Unknown language</lang>")]
    [InlineData("<lang en-QQ>Unknown region</lang>")]
    [InlineData("<lang en-US-u-ca-japanese>Unsupported extension</lang>")]
    [InlineData("<lang 日本語>Non-ASCII name</lang>")]
    [InlineData("<lang &amp;>Escaped name</lang>")]
    [InlineData("<LANG en>Uppercase tag</LANG>")]
    [InlineData("<language en>Unknown tag</language>")]
    [InlineData("<lang.custom en>Custom class</lang>")]
    [InlineData("<lang.red.blue en>Competing colors</lang>")]
    [InlineData("<lang.bg_red.bg_blue en>Competing fills</lang>")]
    [InlineData("<lang en>Unclosed")]
    [InlineData("<lang en><b>Crossed</lang></b>")]
    [InlineData("<lang en>Wrong close</lang.en>")]
    [InlineData("<lang en\nUS>Multiline annotation</lang>")]
    public void UnknownOrMalformedMeaningKeepsCompatibility(string content) => Assert.Null(Parse(content));

    [Fact]
    public void LanguageRangesRetainTheDepthLengthAndUnicodeLimits()
    {
        var open = string.Concat(Enumerable.Repeat("<lang ja>", 17));
        var close = string.Concat(Enumerable.Repeat("</lang>", 17));
        Assert.Null(Parse(open + "Dialogue" + close));
        Assert.Null(Parse("<lang " + new string('a', 86) + ">Dialogue</lang>"));
        Assert.Null(Parse("<lang ja>" + new string('A', 2048) + "</lang>"));
        Assert.Null(Parse("<lang ja>" + (char)0xD83D + "</lang>" + (char)0xDE00));
    }

    private static PlainSubtitleTrack? Parse(string content) => PlainSubtitleTrack.Parse("language.webvtt", FileText(content));
}
