using System.Globalization;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    [Theory]
    [InlineData("ja-JP", "zh-CN")]
    [InlineData("zh-CN", "ja-JP")]
    [InlineData("ar-SA", "en-US")]
    [InlineData("hi-IN", "en-US")]
    public async Task NativeLanguage_ExplicitLanguageOverridesAmbientCultureInOverlayAndSnapshot(string language, string ambient) => await WithWindow(_ =>
    {
        const string text = "骨直令葛辻 日本語 العربية हिन्दी 😀";
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            var plain = new SubtitlePresenter();
            var referenceCue = new SubtitleCue(1000, 4000, text);
            plain.SetCue(referenceCue);
            var expectedOverlay = CaptionPixels(RenderCaption(plain));
            var expectedSnapshot = CaptionPixels(SubtitlePresenter.Composite(CaptionBackground(), referenceCue));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(ambient);
            var track = PlainSubtitleTrack.Parse("language.vtt", SubtitleLanguageTests.FileText($"<lang {language}>{text}</lang>"));
            Assert.NotNull(track);
            var presenter = new SubtitlePresenter();
            presenter.SetCue(track.CueAt(1500));
            Assert.Equal(expectedOverlay, CaptionPixels(RenderCaption(presenter)));
            Assert.Equal(expectedSnapshot, CaptionPixels(SubtitlePresenter.Composite(CaptionBackground(), track.CueAt(1500))));
        }
        finally { CultureInfo.CurrentUICulture = original; }
        return Task.CompletedTask;
    });

    [Fact]
    public async Task NativeLanguage_LanguageChangesCanSelectDifferentCjkGlyphsWithoutStalePixels() => await WithWindow(_ =>
    {
        const string text = "骨直令葛辻";
        SubtitleCue Cue(string language)
        {
            var track = PlainSubtitleTrack.Parse("language.vtt", SubtitleLanguageTests.FileText($"<lang {language}>{text}</lang>"));
            Assert.NotNull(track);
            return track.CueAt(1500)!;
        }
        var presenter = new SubtitlePresenter();
        presenter.SetCue(Cue("ja-JP"));
        var japanese = CaptionPixels(RenderCaption(presenter));
        presenter.SetCue(Cue("zh-CN"));
        var chinese = CaptionPixels(RenderCaption(presenter));
        Assert.NotEqual(japanese, chinese);
        presenter.SetCue(Cue("ja-JP"));
        Assert.Equal(japanese, CaptionPixels(RenderCaption(presenter)));
        return Task.CompletedTask;
    });
}
