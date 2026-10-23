using System.Windows;
using System.Windows.Media.Imaging;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    private static Rect AlignmentInk(BitmapSource image)
    {
        var pixels = CaptionPixels(image);
        var left = image.PixelWidth; var top = image.PixelHeight; var right = -1; var bottom = -1;
        for (var y = 0; y < image.PixelHeight; y++)
            for (var x = 0; x < image.PixelWidth; x++)
            {
                var offset = (y * image.PixelWidth + x) * 4;
                if (pixels[offset] < 180 || pixels[offset + 1] < 180 || pixels[offset + 2] < 180 || pixels[offset + 3] < 180) continue;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        Assert.True(right >= left && bottom >= top, "The positioned caption must contain visible glyphs.");
        return new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    private static void AssertAlignmentRegion(BitmapSource image, int number, Rect picture)
    {
        var ink = AlignmentInk(image);
        Assert.True(picture.Contains(ink), $"Caption {ink} must stay inside picture {picture}.");
        var column = (number - 1) % 3;
        var row = (number - 1) / 3;
        var centerX = ink.Left + ink.Width / 2;
        var centerY = ink.Top + ink.Height / 2;
        // Test the aligned edge: a long caption can occupy most of a narrow
        // portrait picture while still being correctly left/right aligned.
        if (column == 0) Assert.InRange(ink.Left, picture.Left + picture.Width * 0.03, picture.Left + picture.Width * 0.1 + 3);
        else if (column == 2) Assert.InRange(ink.Right, picture.Right - picture.Width * 0.1 - 3, picture.Right - picture.Width * 0.03);
        else Assert.InRange(centerX, picture.Left + picture.Width * 0.48, picture.Left + picture.Width * 0.52);
        if (row == 0) Assert.True(centerY > picture.Top + picture.Height * 2 / 3);
        else if (row == 2) Assert.True(centerY < picture.Top + picture.Height / 3);
        else Assert.InRange(centerY, picture.Top + picture.Height * 0.47, picture.Top + picture.Height * 0.53);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public async Task NativeAlignment_AllNineAnchorsUseThePictureInOverlaysAndSnapshots(int number) => await WithWindow(_ =>
    {
        var track = PlainSubtitleTrack.Parse("positioned.srt", SubtitleAlignmentTests.FileText($"{{\\an{number}}}<b>Place</b>\n日本語 😀"))!;
        var cue = track.CueAt(1500)!;
        foreach (var size in new[] { (640, 480), (800, 240), (240, 640) })
        {
            var presenter = new SubtitlePresenter { PictureAspectRatio = 4d / 3 };
            presenter.SetCue(cue);
            var pictureWidth = Math.Min(size.Item1, size.Item2 * 4d / 3);
            var pictureHeight = pictureWidth * 3 / 4;
            var picture = new Rect((size.Item1 - pictureWidth) / 2, (size.Item2 - pictureHeight) / 2, pictureWidth, pictureHeight);
            AssertAlignmentRegion(RenderCaption(presenter, size.Item1, size.Item2), number, picture);
            var snapshot = SubtitlePresenter.Composite(CaptionBackground(size.Item1, size.Item2), cue);
            AssertAlignmentRegion(snapshot, number, new Rect(0, 0, size.Item1, size.Item2));
        }
        return Task.CompletedTask;
    });

    [Fact]
    public async Task NativeAlignment_IdenticalTextMovesWithoutStalePixelsAndOffClearsIt() => await WithWindow(_ =>
    {
        const string text = "Same caption";
        var presenter = new SubtitlePresenter();
        var defaultCue = new SubtitleCue(0, 2000, text);
        presenter.SetCue(defaultCue);
        var original = CaptionPixels(RenderCaption(presenter));
        foreach (var number in new[] { 8, 5, 1, 9, 2 })
        {
            var cue = PlainSubtitleTrack.Parse("positioned.srt", SubtitleAlignmentTests.FileText($"{{\\an{number}}}{text}"))!.CueAt(1500)!;
            presenter.SetCue(cue);
            var image = RenderCaption(presenter);
            AssertAlignmentRegion(image, number, new Rect(0, 0, 640, 480));
            if (number == 2) Assert.Equal(original, CaptionPixels(image));
            else Assert.NotEqual(original, CaptionPixels(image));
        }
        presenter.SetCue(null);
        Assert.All(CaptionPixels(RenderCaption(presenter)), value => Assert.Equal(0, value));
        return Task.CompletedTask;
    });
}
