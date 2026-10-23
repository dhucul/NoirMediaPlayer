using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(90u)]
    [InlineData(180u)]
    [InlineData(270u)]
    public async Task OrientedSnapshots_KeepNativeOrientationAndKnownSourceCorners(uint rotation) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "oriented.mp4");
        Mp4Fixture.WriteOriented(path, rotation);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 3000);
        Invoke(window, "SeekTo", 3000L);
        await Until(() => Math.Abs(player.Time - 3000) < 150);
        var nativePath = Path.Combine(TestRoot(window), "native.png");
        await Task.Run(() => player.SaveSnapshot(nativePath));
        var native = ReadCaptionBitmap(nativePath);
        Assert.Equal(rotation is 0 or 180 ? 320 : 240, native.PixelWidth);
        Assert.Equal(rotation is 0 or 180 ? 240 : 320, native.PixelHeight);
        Assert.InRange((double)native.PixelWidth / native.PixelHeight, player.DisplayAspectRatio - 0.001, player.DisplayAspectRatio + 0.001);
        AssertOrientedCorners(native, rotation);
        var commandSnapshot = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
        Assert.Equal(CaptionPixels(native), CaptionPixels(commandSnapshot));
        await Call(window, "NavigateChapterAsync", true); // Validated chapterless movie: native no-op.
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<Media?>(window, "_currentMedia"));
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(player.Time, 2850, 3150);
    });

    [Theory]
    [InlineData(90u, false)]
    [InlineData(90u, true)]
    [InlineData(180u, false)]
    [InlineData(180u, true)]
    [InlineData(270u, false)]
    [InlineData(270u, true)]
    public async Task OrientedSnapshots_PixelAspectAndCaptionsStayUprightLocallyAndOverHttp(uint rotation, bool network) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "portrait.mp4");
        Mp4Fixture.WriteOriented(source, rotation, aspectNumerator: 2);
        var original = File.ReadAllBytes(source);
        await using var server = network ? new LoopbackMediaServer(original, ".mp4", "video/mp4") : null;
        var styled = Path.ChangeExtension(source, ".vtt");
        var plain = Path.Combine(TestRoot(window), "subs", "portrait.fr.srt");
        Directory.CreateDirectory(Path.GetDirectoryName(plain)!);
        File.WriteAllText(styled, BackgroundSubtitleFile());
        File.WriteAllText(plain, "1\n00:00:00,000 --> 00:00:25,000\nPlain café 日本語\n");
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "Oriented HTTP", IsNetwork = true }
            : MediaSourceService.CreateFileItem(source);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        if (network)
        {
            Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", styled));
            Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", plain));
        }
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10000);
        player.SelectAudioTrack(-1);
        Invoke(window, "SeekTo", 7000L);
        await Until(() => Math.Abs(player.Time - 7000) < 150);
        var sourceSnapshot = Path.Combine(TestRoot(window), "source.png");
        await Task.Run(() => player.SaveSnapshot(sourceSnapshot));
        var image = ReadCaptionBitmap(sourceSnapshot);
        Assert.Equal(rotation == 180 ? 640 : 240, image.PixelWidth);
        Assert.Equal(rotation == 180 ? 240 : 640, image.PixelHeight);
        AssertOrientedCorners(image, rotation);
        Assert.InRange((double)image.PixelWidth / image.PixelHeight, player.DisplayAspectRatio - 0.001, player.DisplayAspectRatio + 0.001);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        Assert.Equal(3, selector.Items.Count);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        foreach (var choice in new[] { 1, 2, 0 })
        {
            selector.SelectedIndex = choice;
            var path = (selector.SelectedItem as ComboBoxItem)?.Tag is PlainSubtitleTrack selected ? selected.Path : null;
            var cue = path is null ? null : PlainSubtitleTrack.TryLoad(path)!.CueAt(7000);
            var captured = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
            Assert.Equal(CaptionPixels(SubtitlePresenter.Composite(image, cue)), CaptionPixels(captured));
            AssertOrientedCorners(captured, rotation);
            if (path == styled)
            {
                Assert.True(ColorCaptionPixels(captured, 0x00FF00) > 300);
                Assert.InRange(presenter.PictureAspectRatio, player.DisplayAspectRatio - 0.001, player.DisplayAspectRatio + 0.001);
            }
            Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
            Assert.InRange(player.Time, 6850, 7150);
        }
        Invoke(window, "SeekTo", 10000L);
        await Until(() => Math.Abs(player.Time - 10000) < 150);
        var later = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
        AssertOrientedCorners(later, rotation, later: true);
        window.Width = 1050;
        window.Height = 680;
        Invoke(window, "ToggleCompact");
        Invoke(window, "ToggleFullscreen");
        var resized = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
        Assert.Equal(CaptionPixels(later), CaptionPixels(resized));
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(styled));
        player.SelectAudioTrack(player.AudioTracks[0].Id);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData(90u, false)]
    [InlineData(90u, true)]
    [InlineData(270u, false)]
    [InlineData(270u, true)]
    public async Task OrientedSnapshots_NativeRotationAndLaterHandoffKeepCaptionsAudioOffPauseAndPosition(uint rotation, bool captionsOff) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "handoff.mp4");
        Mp4Fixture.WriteOriented(source, rotation);
        var styled = Path.ChangeExtension(source, ".vtt");
        var other = Path.Combine(TestRoot(window), "subs", "handoff.fr.srt");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(styled, BackgroundSubtitleFile());
        File.WriteAllText(other, "1\n00:00:00,000 --> 00:00:25,000\nRetained caption\n");
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(source), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => selector.Items.Count == 3);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10000);
        player.SelectAudioTrack(-1);
        selector.SelectedIndex = 0;
        Invoke(window, "SeekTo", 10000L);
        await Until(() => Math.Abs(player.Time - 10000) < 150);
        AssertOrientedCorners(ReadCaptionBitmap(await CapturePlayerSnapshot(window)), rotation, later: true);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        selector.SelectedIndex = captionsOff ? 0 : 2;
        Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<Media?>(window, "_currentMedia"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        AssertOrientedCorners(ReadCaptionBitmap(await CapturePlayerSnapshot(window)), (rotation + 90) % 360, later: true);
        // A later feature that still needs VLC retains the selected native transform.
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.Equal(captionsOff ? -1 : tracks[1].Id, vlc.Spu);
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(90, Field<int>(window, "_rotation"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(vlc.Time, 9700, 10500);
        Assert.Null(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue);
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(styled));
    });

    private static void AssertOrientedCorners(BitmapSource bitmap, uint rotation, bool later = false)
    {
        // RGB32 input rows are bottom-up. These fixed corner orders describe the
        // corrected picture, independently of the renderer's transform implementation.
        const uint red = 0xC82020, green = 0x20C820, blue = 0x2020C8, yellow = 0xC8C820;
        uint[] expected = rotation switch
        {
            0 => [blue, yellow, red, green], 90 => [red, blue, green, yellow],
            180 => [green, red, yellow, blue], 270 => [yellow, green, blue, red], _ => throw new ArgumentOutOfRangeException(nameof(rotation))
        };
        if (later) expected = expected.Select(color => color switch { red => blue, green => yellow, blue => red, yellow => green, _ => color }).ToArray();
        var positions = new[] { (0.2, 0.2), (0.8, 0.2), (0.2, 0.8), (0.8, 0.8) };
        for (var index = 0; index < positions.Length; index++)
        {
            var (x, y) = positions[index];
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)(bitmap.PixelWidth * x), (int)(bitmap.PixelHeight * y), 1, 1), pixel, 4, 0);
            for (var channel = 0; channel < 3; channel++)
                Assert.InRange(Math.Abs(pixel[channel] - (int)((expected[index] >> (channel * 8)) & 255)), 0, 20);
        }
    }
}
