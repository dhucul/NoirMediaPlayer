using System.Windows.Media.Imaging;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    private static int ColorCaptionPixels(BitmapSource image, uint rgb)
    {
        var pixels = CaptionPixels(image);
        return Enumerable.Range(0, image.PixelWidth * image.PixelHeight).Count(index => pixels[index * 4 + 3] > 240 &&
            Math.Abs(pixels[index * 4] - (int)(rgb & 255)) < 8 &&
            Math.Abs(pixels[index * 4 + 1] - (int)((rgb >> 8) & 255)) < 8 &&
            Math.Abs(pixels[index * 4 + 2] - (int)((rgb >> 16) & 255)) < 8);
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
    public async Task SubtitleColors_UseRequestedPixelColorsInOverlayAndSnapshot(string name, uint rgb) => await WithWindow(_ =>
    {
        var cue = PlainSubtitleTrack.Parse("color.vtt", SubtitleStyleTests.FileText(".vtt", $"<c.{name}><b>Colored glyphs</b></c>"))!.CueAt(1000)!;
        var presenter = new SubtitlePresenter();
        presenter.SetCue(cue);
        var overlay = RenderCaption(presenter);
        var snapshot = SubtitlePresenter.Composite(CaptionBackground(), cue);
        Assert.True(ColorCaptionPixels(overlay, rgb) > 40, $"The overlay must contain opaque {name} glyph pixels.");
        Assert.True(ColorCaptionPixels(snapshot, rgb) > 40, $"The snapshot must contain opaque {name} glyph pixels.");
        var plain = SubtitlePresenter.Composite(CaptionBackground(), new SubtitleCue(0, 4000, cue.Text));
        if (name != "white")
        {
            if (name != "black") Assert.Equal(0, ColorCaptionPixels(plain, rgb));
            Assert.Equal(0, WhiteCaptionPixels(snapshot).Count(value => value));
        }
        var overlayPixels = CaptionPixels(overlay);
        var snapshotPixels = CaptionPixels(snapshot);
        // Opaque glyph/outline pixels must agree exactly on both backgrounds.
        var opaque = Enumerable.Range(0, 640 * 480).Where(index => overlayPixels[index * 4 + 3] == 255).ToArray();
        Assert.True(opaque.Length > 100);
        foreach (var index in opaque)
            Assert.Equal(overlayPixels.AsSpan(index * 4, 4).ToArray(), snapshotPixels.AsSpan(index * 4, 4).ToArray());
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("b")]
    [InlineData("i")]
    [InlineData("u")]
    public async Task SubtitleColors_KeepInlineStylesAndDefaultWhiteOutsideColoredRuns(string style) => await WithWindow(_ =>
    {
        var text = $"Plain <font color='#FF0000'><{style}>Color</{style}></font> plain";
        var cue = PlainSubtitleTrack.Parse("color.srt", SubtitleStyleTests.FileText(".srt", text))!.CueAt(1000)!;
        var snapshot = SubtitlePresenter.Composite(CaptionBackground(), cue);
        Assert.True(ColorCaptionPixels(snapshot, 0xFF0000) > 20);
        Assert.True(WhiteCaptionPixels(snapshot).Count(value => value) > 40);
        var unstyled = PlainSubtitleTrack.Parse("color.srt", SubtitleStyleTests.FileText(".srt", "Plain <font color='#FF0000'>Color</font> plain"))!.CueAt(1000)!;
        var unstyledImage = SubtitlePresenter.Composite(CaptionBackground(), unstyled);
        Assert.NotEqual(CaptionPixels(unstyledImage), CaptionPixels(snapshot));
        if (style == "u")
        {
            int LongestRedLine(BitmapSource image)
            {
                var pixels = CaptionPixels(image);
                var longest = 0;
                for (var row = 0; row < image.PixelHeight; row++)
                {
                    var run = 0;
                    for (var column = 0; column < image.PixelWidth; column++)
                    {
                        var index = (row * image.PixelWidth + column) * 4;
                        // Thin font decorations are antialiased. Recognize red
                        // coverage without requiring a fully opaque one-pixel line.
                        run = pixels[index + 2] > 100 && pixels[index] < 100 && pixels[index + 1] < 80 ? run + 1 : 0;
                        longest = Math.Max(longest, run);
                    }
                }
                return longest;
            }
            var styledLine = LongestRedLine(snapshot);
            var unstyledLine = LongestRedLine(unstyledImage);
            _testOutput.WriteLine($"Longest red stroke: underlined {styledLine}px, ordinary {unstyledLine}px.");
            Assert.True(styledLine > unstyledLine + 20,
                "An underlined colored run must paint its horizontal stroke in the same color.");
        }
        return Task.CompletedTask;
    });

    [Fact]
    public async Task SubtitleColors_IdenticalTextCanChangeColorAndResetWithoutStaleBrushes() => await WithWindow(_ =>
    {
        var red = new SubtitleCue(0, 1000, "Same caption", [new(0, 12, SubtitleStyle.None, 0xFF0000)]);
        var blue = new SubtitleCue(1000, 2000, red.Text, [new(0, 12, SubtitleStyle.None, 0x0000FF)]);
        var presenter = new SubtitlePresenter();
        presenter.SetCue(red);
        var first = RenderCaption(presenter);
        Assert.True(ColorCaptionPixels(first, 0xFF0000) > 40);
        presenter.SetCue(blue);
        var second = RenderCaption(presenter);
        Assert.True(ColorCaptionPixels(second, 0x0000FF) > 40);
        Assert.Equal(0, ColorCaptionPixels(second, 0xFF0000));
        presenter.SetCue(new SubtitleCue(2000, 3000, red.Text));
        var plain = RenderCaption(presenter);
        Assert.True(WhiteCaptionPixels(plain).Count(value => value) > 40);
        Assert.Equal(0, ColorCaptionPixels(plain, 0x0000FF));
        presenter.SetCue(null);
        Assert.All(CaptionPixels(RenderCaption(presenter)), value => Assert.Equal((byte)0, value));
        return Task.CompletedTask;
    });
}
