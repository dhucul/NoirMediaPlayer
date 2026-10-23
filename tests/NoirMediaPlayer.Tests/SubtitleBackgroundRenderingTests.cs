using System.Windows;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
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
    public async Task SubtitleBackgrounds_RenderOpaqueColorBehindGlyphsOnOverlayAndSnapshot(string name, uint rgb) => await WithWindow(_ =>
    {
        var foreground = name == "black" ? "white" : "black";
        var cue = BackgroundCue($"<c.{foreground}.bg_{name}><b>Caption background</b></c>");
        var presenter = new SubtitlePresenter();
        presenter.SetCue(cue);
        var overlay = RenderCaption(presenter);
        var snapshot = SubtitlePresenter.Composite(CaptionBackground(), cue);
        Assert.True(ColorCaptionPixels(overlay, rgb) > 1000, $"The overlay must paint an opaque {name} background.");
        Assert.True(ColorCaptionPixels(snapshot, rgb) > 1000, $"The snapshot must paint an opaque {name} background.");
        var plain = BackgroundCue($"<c.{foreground}><b>Caption background</b></c>");
        Assert.True(ColorCaptionPixels(overlay, rgb) > ColorCaptionPixels(SubtitlePresenter.Composite(CaptionBackground(), plain), rgb) + 1000);
        var overlayPixels = CaptionPixels(overlay);
        var snapshotPixels = CaptionPixels(snapshot);
        foreach (var index in Enumerable.Range(0, 640 * 480).Where(index => overlayPixels[index * 4 + 3] == 255))
            Assert.Equal(overlayPixels.AsSpan(index * 4, 4).ToArray(), snapshotPixels.AsSpan(index * 4, 4).ToArray());
        Assert.Equal((byte)0, overlayPixels[(10 * 640 + 10) * 4 + 3]);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(640, 480, 4d / 3)]
    [InlineData(960, 540, 4d / 3)]
    [InlineData(800, 800, 2.35)]
    public async Task SubtitleBackgrounds_WrappedUnicodeAndStyledRunsStayInsideThePicture(int width, int height, double aspect) => await WithWindow(_ =>
    {
        var text = "<c.yellow.bg_blue><b>日本語 café 😀 العربية <i>" + new string('W', 42) + "</i></b>\n<c.bg_red><u>Second line</u></c></c> Outside";
        var presenter = new SubtitlePresenter { PictureAspectRatio = aspect };
        presenter.SetCue(BackgroundCue(text));
        var rendered = RenderCaption(presenter, width, height);
        var pixels = CaptionPixels(rendered);
        var pictureWidth = Math.Min(width, height * aspect);
        var pictureHeight = pictureWidth / aspect;
        var picture = new Rect((width - pictureWidth) / 2, (height - pictureHeight) / 2, pictureWidth, pictureHeight);
        Assert.True(ColorCaptionPixels(rendered, 0x0000FF) > 300);
        Assert.True(ColorCaptionPixels(rendered, 0xFF0000) > 300);
        Assert.True(ColorCaptionPixels(rendered, 0xFFFF00) > 20);
        foreach (var index in Enumerable.Range(0, width * height).Where(index => pixels[index * 4 + 3] > 200))
            Assert.True(picture.Contains(new Point(index % width + 0.5, index / width + 0.5)));
        return Task.CompletedTask;
    });

    [Fact]
    public async Task SubtitleBackgrounds_ConsecutiveIdenticalTextCanChangeOrClearItsFill() => await WithWindow(_ =>
    {
        var presenter = new SubtitlePresenter();
        presenter.SetCue(BackgroundCue("<c.bg_blue>Same caption</c>"));
        Assert.True(ColorCaptionPixels(RenderCaption(presenter), 0x0000FF) > 1000);
        presenter.SetCue(BackgroundCue("<c.bg_red>Same caption</c>"));
        var red = RenderCaption(presenter);
        Assert.True(ColorCaptionPixels(red, 0xFF0000) > 1000);
        Assert.Equal(0, ColorCaptionPixels(red, 0x0000FF));
        presenter.SetCue(new SubtitleCue(0, 4000, "Same caption"));
        Assert.Equal(0, ColorCaptionPixels(RenderCaption(presenter), 0xFF0000));
        presenter.SetCue(null);
        Assert.All(CaptionPixels(RenderCaption(presenter)), value => Assert.Equal((byte)0, value));
        return Task.CompletedTask;
    });

    private static SubtitleCue BackgroundCue(string text) =>
        PlainSubtitleTrack.Parse("background.vtt", SubtitleStyleTests.FileText(".vtt", text))!.CueAt(1000)!;
}
