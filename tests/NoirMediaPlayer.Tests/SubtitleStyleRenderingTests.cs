using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    private static BitmapSource CaptionBackground(int width = 640, int height = 480)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 150; pixels[index + 1] = 40; pixels[index + 2] = 20; pixels[index + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static byte[] CaptionPixels(BitmapSource image)
    {
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static bool[] WhiteCaptionPixels(BitmapSource image)
    {
        var pixels = CaptionPixels(image);
        return Enumerable.Range(0, image.PixelWidth * image.PixelHeight).Select(index =>
            pixels[index * 4 + 3] > 180 && pixels[index * 4] > 180 && pixels[index * 4 + 1] > 180 && pixels[index * 4 + 2] > 180).ToArray();
    }

    private static BitmapSource RenderCaption(SubtitlePresenter presenter, int width = 640, int height = 480)
    {
        presenter.Measure(new Size(width, height));
        presenter.Arrange(new Rect(0, 0, width, height));
        presenter.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(presenter);
        bitmap.Freeze();
        return bitmap;
    }

    [Theory]
    [InlineData("b")]
    [InlineData("i")]
    [InlineData("u")]
    public async Task SubtitleStyles_ChangeVisibleGlyphsAndMatchSnapshotRendering(string tag) => await WithWindow(_ =>
    {
        var track = PlainSubtitleTrack.Parse("captions.srt", SubtitleStyleTests.FileText(".srt", $"<{tag}>Visible caption</{tag}>"));
        Assert.NotNull(track);
        var styled = track.CueAt(1000)!;
        var plain = new SubtitleCue(styled.Start, styled.End, styled.Text);
        var background = CaptionBackground();
        var plainPixels = WhiteCaptionPixels(SubtitlePresenter.Composite(background, plain));
        var styledSnapshot = SubtitlePresenter.Composite(background, styled);
        var styledPixels = WhiteCaptionPixels(styledSnapshot);
        Assert.True(plainPixels.Zip(styledPixels).Count(pair => pair.First != pair.Second) > 30,
            $"The {tag} style must change the rendered glyphs, not merely remove its markup.");
        var presenter = new SubtitlePresenter();
        presenter.SetCue(styled);
        Assert.Equal(styledPixels, WhiteCaptionPixels(RenderCaption(presenter)));
        if (tag == "u")
        {
            int LongestLine(bool[] mask)
            {
                var longest = 0;
                for (var row = 0; row < 480; row++)
                {
                    var run = 0;
                    for (var column = 0; column < 640; column++)
                    {
                        run = mask[row * 640 + column] ? run + 1 : 0;
                        longest = Math.Max(longest, run);
                    }
                }
                return longest;
            }
            Assert.True(LongestLine(styledPixels) > LongestLine(plainPixels) + 30,
                "Underline must add a visible horizontal stroke in the PNG and overlay.");
        }
        return Task.CompletedTask;
    });

    [Fact]
    public async Task SubtitleStyles_SameTextChangesStyleAndClearingCannotLeaveStaleFormatting() => await WithWindow(_ =>
    {
        var presenter = new SubtitlePresenter();
        var normal = new SubtitleCue(0, 1000, "Same caption");
        var styled = PlainSubtitleTrack.Parse("captions.srt", SubtitleStyleTests.FileText(".srt", "<i>Same caption</i>"))!.CueAt(1000)!;
        presenter.SetCue(normal);
        var plainPixels = WhiteCaptionPixels(RenderCaption(presenter));
        presenter.SetCue(styled);
        var italicPixels = WhiteCaptionPixels(RenderCaption(presenter));
        Assert.True(plainPixels.Zip(italicPixels).Count(pair => pair.First != pair.Second) > 30);
        presenter.SetCue(null);
        Assert.Empty(presenter.Text);
        Assert.DoesNotContain(true, WhiteCaptionPixels(RenderCaption(presenter)));
        presenter.SetCue(styled);
        Assert.Equal(italicPixels, WhiteCaptionPixels(RenderCaption(presenter)));
        presenter.SetCue(normal);
        Assert.Equal(plainPixels, WhiteCaptionPixels(RenderCaption(presenter)));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(960, 540, 4d / 3)]
    [InlineData(800, 800, 2.35)]
    public async Task SubtitleStyles_RemainInsideThePictureWhenAspectAndWindowSizeChange(int width, int height, double aspect) => await WithWindow(_ =>
    {
        var presenter = new SubtitlePresenter { PictureAspectRatio = aspect };
        presenter.SetCue(PlainSubtitleTrack.Parse("captions.srt", SubtitleStyleTests.FileText(".srt",
            "<font color='red'><b>Bold</b></font> <font color='blue'><i>Italic</i></font> <u>Underline</u>"))!.CueAt(1000));
        var pixels = CaptionPixels(RenderCaption(presenter, width, height));
        var mask = Enumerable.Range(0, width * height).Select(index => pixels[index * 4 + 3] > 180).ToArray();
        var pictureWidth = Math.Min(width, height * aspect);
        var pictureHeight = pictureWidth / aspect;
        var picture = new Rect((width - pictureWidth) / 2, (height - pictureHeight) / 2, pictureWidth, pictureHeight);
        var count = 0;
        for (var index = 0; index < mask.Length; index++)
        {
            if (!mask[index]) continue;
            Assert.True(picture.Contains(index % width, index / width));
            count++;
        }
        Assert.True(count > 50);
        return Task.CompletedTask;
    });
}
