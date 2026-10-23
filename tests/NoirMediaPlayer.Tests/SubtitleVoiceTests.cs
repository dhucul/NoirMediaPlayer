using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class SubtitleVoiceTests
{
    internal static string FileText(string content) => $"WEBVTT\n\n00:01.000 --> 00:04.000\n{content}\n";

    [Theory]
    [InlineData("<v Alice>Dialogue</v>")]
    [InlineData("<v Alice>Dialogue")]
    [InlineData("<v\t日本語 café 😀>Dialogue</v>")]
    [InlineData("<v  Alice Smith \t>Dialogue</v>")]
    [InlineData("<v Alice &amp; Bob>Dialogue")]
    [InlineData("<v &lt;Narrator&gt;>Dialogue</v>")]
    public void VoiceAnnotationsKeepOnlyTheVisibleDialogue(string markup)
    {
        var track = Parse(markup);
        Assert.NotNull(track);
        Assert.Equal("Dialogue", track.TextAt(1500));
        Assert.Empty(track.CueAt(1500)!.Styles);
        Assert.Null(track.CueAt(999));
        Assert.Null(track.CueAt(4000));
        Assert.Null(PlainSubtitleTrack.Parse("voice.srt", SubtitleStyleTests.FileText(".srt", markup)));
    }

    [Theory]
    [InlineData("b", 1)]
    [InlineData("i", 2)]
    [InlineData("u", 4)]
    [InlineData("c", 0)]
    [InlineData("v", 0)]
    public void DefaultClassesApplyToEverySupportedCueComponent(string name, int style)
    {
        foreach (var classes in new[] { "yellow.bg_blue", "bg_blue.yellow" })
        {
            var annotation = name == "v" ? " Alice" : "";
            var track = Parse($"Before <{name}.{classes}{annotation}>日本語 😀</{name}> after");
            Assert.NotNull(track);
            var cue = track.CueAt(1500)!;
            Assert.Equal("Before 日本語 😀 after", cue.Text);
            Assert.Equal(new SubtitleStyleRange(7, 6, (SubtitleStyle)style, 0xFFFF00, 0x0000FF), Assert.Single(cue.Styles));
        }
    }

    [Fact]
    public void MultipleSpeakersAndNestedStylesRestoreTheSurroundingRuns()
    {
        var cue = Parse("<c.bg_blue><v Alice><b>A<i>B</i>C</b></v> <v Bob><u>D</u></v></c> E")!.CueAt(1500)!;
        Assert.Equal("ABC D E", cue.Text);
        Assert.Equal(new[]
        {
            new SubtitleStyleRange(0, 1, SubtitleStyle.Bold, null, 0x0000FF),
            new SubtitleStyleRange(1, 1, SubtitleStyle.Bold | SubtitleStyle.Italic, null, 0x0000FF),
            new SubtitleStyleRange(2, 1, SubtitleStyle.Bold, null, 0x0000FF),
            new SubtitleStyleRange(3, 1, SubtitleStyle.None, null, 0x0000FF),
            new SubtitleStyleRange(4, 1, SubtitleStyle.Underline, null, 0x0000FF)
        }, cue.Styles);
    }

    [Fact]
    public void AWholeCueVoiceMayOmitItsEndTagWhileRetainingStyledChildren()
    {
        var cue = Parse("<v.red.bg_blue Alice><b>Bold</b> plain\n<i>Italic</i>")!.CueAt(1500)!;
        Assert.Equal("Bold plain\nItalic", cue.Text);
        Assert.Equal(new[]
        {
            new SubtitleStyleRange(0, 4, SubtitleStyle.Bold, 0xFF0000, 0x0000FF),
            new SubtitleStyleRange(4, 7, SubtitleStyle.None, 0xFF0000, 0x0000FF),
            new SubtitleStyleRange(11, 6, SubtitleStyle.Italic, 0xFF0000, 0x0000FF)
        }, cue.Styles);
    }

    [Fact]
    public void EscapedVoiceMarkupStaysVisibleLiteralText()
    {
        var cue = Parse("<v Alice>&lt;v Bob&gt;literal&lt;/v&gt; &amp;lt;</v>")!.CueAt(1500)!;
        Assert.Equal("<v Bob>literal</v> &lt;", cue.Text);
        Assert.Empty(cue.Styles);
    }

    [Theory]
    [InlineData("<v>Missing name</v>")]
    [InlineData("<v >Missing name</v>")]
    [InlineData("<v\t\t>Missing name</v>")]
    [InlineData("<V Alice>Uppercase</V>")]
    [InlineData("<voice Alice>Unknown tag</voice>")]
    [InlineData("<v.custom Alice>Custom class</v>")]
    [InlineData("<b.custom>Custom class</b>")]
    [InlineData("<i.red.blue>Ambiguous colors</i>")]
    [InlineData("<u.bg_red.bg_blue>Ambiguous fills</u>")]
    [InlineData("<v.red. Alice>Empty class</v>")]
    [InlineData("<b.red >Annotation</b>")]
    [InlineData("<v Alice\nSmith>Multiline annotation</v>")]
    [InlineData("<v <Alice>Unknown markup</v>")]
    [InlineData("<v Alice &unknown;>Unknown reference</v>")]
    [InlineData("<v Alice &#65;>Numeric reference</v>")]
    [InlineData("<v Alice & Bob>Unescaped reference</v>")]
    [InlineData("Prefix <v Alice>Unclosed")]
    [InlineData("<v Alice>Closed</v><v Bob>Unclosed")]
    [InlineData("<b><v Alice>Unclosed")]
    [InlineData("<v Alice><b>Unclosed")]
    [InlineData("<v Alice><b>Crossed</v></b>")]
    [InlineData("<v Alice>Wrong close</v.red>")]
    [InlineData("<lang x-private>Unknown language</lang>")]
    [InlineData("<v Alice><ruby>A<rt>B</rt></ruby></v>")]
    [InlineData("<v Alice>Karaoke <00:02.000>text</v>")]
    public void UnmigratedOrMalformedMeaningKeepsCompatibility(string content) => Assert.Null(Parse(content));

    [Fact]
    public void VoiceNamesAndNestedTagsKeepTheExistingBounds()
    {
        Assert.NotNull(Parse("<v " + new string('A', 256) + ">Dialogue</v>"));
        Assert.Null(Parse("<v " + new string('A', 257) + ">Dialogue</v>"));
        var open = string.Concat(Enumerable.Repeat("<v Alice>", 17));
        var close = string.Concat(Enumerable.Repeat("</v>", 17));
        Assert.Null(Parse(open + "Dialogue" + close));
        Assert.Null(Parse("<v Alice>" + new string('A', 2048)));
    }

    [Fact]
    public void VoiceStylesRegionsOverlapsAndOtherLayoutStillKeepCompatibility()
    {
        Assert.Null(PlainSubtitleTrack.Parse("voice.vtt", "WEBVTT\n\nSTYLE\n::cue(v[voice=\"Alice\"]) { color: red; }\n\n00:01.000 --> 00:04.000\n<v Alice>Dialogue\n"));
        Assert.Null(PlainSubtitleTrack.Parse("voice.vtt", FileText("<v Alice>Dialogue").Replace("--> 00:04.000", "--> 00:04.000 line:0")));
        Assert.Null(PlainSubtitleTrack.Parse("voice.vtt", FileText("<v Alice>First") + "\n00:02.000 --> 00:05.000\n<v Bob>Second\n"));
    }

    private static PlainSubtitleTrack? Parse(string content) => PlainSubtitleTrack.Parse("voice.vtt", FileText(content));
}
