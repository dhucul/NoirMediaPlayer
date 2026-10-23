using System.IO;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    [Fact]
    public async Task NativeAspect_FormatNotificationsPreserveThePausedPictureAndSelection() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "format-notification.mp4");
        Mp4Fixture.WriteOriented(source, 90, aspectNumerator: 2);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(source), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        await AssertRotationPicture(window, 90, 16d / 9);
        var time = player.Time;
        var generation = Field<long>(window, "_playbackGeneration");
        for (var repeat = 0; repeat < 3; repeat++)
        {
            Invoke(window, "DispatchWindowsPlaybackEvent", generation, 1000u);
            await Task.Delay(25);
            await AssertRotationPicture(window, 90, 16d / 9);
        }
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(time, player.Time);
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
    });

    [Fact]
    public async Task NativeAspect_CompatibilityClockBaseline() => _ = await ReadCompatibilityAspectClock(network: false, off: false);

    private static async Task<long> ReadCompatibilityAspectClock(bool network, bool off)
    {
        long reference = 0;
        await WithWindow(async window =>
        {
            ShowPlaybackWindow(window);
            Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
            typeof(MainWindow).GetField("_rotation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, 90);
            Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
            var source = Path.Combine(TestRoot(window), "baseline.mp4");
            Mp4Fixture.Write(source, Mp4Fixture.Nero(0, 4000, 8000));
            File.WriteAllText(Path.ChangeExtension(source, ".webvtt"), BackgroundSubtitleFile());
            await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(source), ".mp4", "video/mp4") : null;
            var item = network ? new PlaylistItem { Source = server!.Url, Title = "Compatibility baseline", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
            await Call<bool>(window, "AddItem", item, true);
            var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
            await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && vlc.Time > 250);
            if (network) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", Path.ChangeExtension(source, ".webvtt")));
            await Until(() => vlc.Spu >= 0);
            if (off) vlc.SetSpu(-1);
            await Call(window, "TogglePlayPause");
            await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
            await Field<PlaybackOperationQueue>(window, "_playbackOperations").Enqueue(() =>
            {
                vlc.SetAudioTrack(-1);
                Invoke(window, "RestoreCompatibilityPosition", 8000L);
            });
            var before = vlc.Time;
            var complex = Path.Combine(TestRoot(window), "complex.vtt");
            File.WriteAllText(complex, "WEBVTT\n\nSTYLE\n::cue { color: red; }\n\n00:00.000 --> 00:25.000\nComplex CSS\n");
            Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", complex));
            Assert.Equal(before, vlc.Time);
            Assert.Equal(8000, Field<long>(window, "_compatibilityChapterTime"));
            reference = vlc.Time;
        });
        return reference;
    }

    [Theory]
    [InlineData(0u, 0, false)]
    [InlineData(0u, 90, false)]
    [InlineData(0u, 180, false)]
    [InlineData(0u, 270, false)]
    [InlineData(90u, 0, false)]
    [InlineData(90u, 90, false)]
    [InlineData(90u, 180, false)]
    [InlineData(90u, 270, false)]
    [InlineData(0u, 0, true)]
    [InlineData(0u, 90, true)]
    [InlineData(0u, 180, true)]
    [InlineData(0u, 270, true)]
    [InlineData(90u, 0, true)]
    [InlineData(90u, 90, true)]
    [InlineData(90u, 180, true)]
    [InlineData(90u, 270, true)]
    public async Task NativeAspect_AllRatiosKeepTheWholePictureAndSourceCaptures(uint sourceRotation, int rotation, bool network) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "aspect.mp4");
        Mp4Fixture.WriteOriented(source, sourceRotation, aspectNumerator: 2);
        var original = File.ReadAllBytes(source);
        await using var server = network ? new LoopbackMediaServer(original, ".mp4", "video/mp4") : null;
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "Aspect HTTP", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10000);
        Invoke(window, "SeekTo", 3000L);
        await Until(() => Math.Abs(player.Time - 3000) < 100);
        player.SelectAudioTrack(-1);
        for (var turn = 0; turn < rotation; turn += 90) Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        var total = (sourceRotation + (uint)rotation) % 360;
        var sourceAspect = total is 0 or 180 ? 640d / 240 : 240d / 640;
        var raw = Path.Combine(TestRoot(window), "source.png");
        await Task.Run(() => player.SaveSnapshot(raw));
        var reference = ReadCaptionBitmap(raw);
        foreach (var (index, aspect) in new[] { (1, 16d / 9), (2, 1.6), (3, 4d / 3), (4, 1d), (5, 21d / 9), (0, sourceAspect) })
        {
            Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = index;
            await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
            Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
            Assert.InRange(player.PresentationAspectRatio, aspect - 0.001, aspect + 0.001);
            await AssertRotationPicture(window, total, aspect);
            var captured = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
            Assert.Equal(reference.PixelWidth, captured.PixelWidth);
            Assert.Equal(reference.PixelHeight, captured.PixelHeight);
            Assert.Equal(CaptionPixels(reference), CaptionPixels(captured));
            Assert.Null(Field<Media?>(window, "_currentMedia"));
            Assert.Null(Field<string?>(window, "_windowsFailureReason"));
            Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
            Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
            Assert.InRange(player.Time, 2900, 3100);
        }
        Assert.Equal(original, File.ReadAllBytes(source));
    });

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public async Task NativeAspect_ResizeAndDisplayModesKeepTheSelectedRatio(int selection, bool rotated) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "resize.mp4");
        Mp4Fixture.WriteOriented(source, 0);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(source), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        if (rotated) Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = selection;
        var rotation = rotated ? 90u : 0u;
        var aspect = selection == 4 ? 1 : 21d / 9;
        foreach (var (width, height) in new[] { (1050, 680), (1400, 960) })
        {
            window.Width = width; window.Height = height;
            await AssertRotationPicture(window, rotation, aspect);
        }
        Invoke(window, "ToggleCompact");
        await AssertRotationPicture(window, rotation, aspect);
        Invoke(window, "ToggleCompact");
        Invoke(window, "ToggleFullscreen");
        await AssertRotationPicture(window, rotation, aspect);
        Invoke(window, "ToggleFullscreen");
        window.WindowState = WindowState.Minimized;
        await Task.Delay(50);
        window.WindowState = WindowState.Normal;
        await AssertRotationPicture(window, rotation, aspect);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 0;
        await AssertRotationPicture(window, rotation, rotated ? 0.75 : 4d / 3);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
    });

    [Theory]
    [InlineData(".mp4", false)]
    [InlineData(".mp4", true)]
    [InlineData("", false)]
    [InlineData(".php", true)]
    public async Task NativeAspect_StartupRestartAndPlayingChangesKeepNativePresentation(string endpoint, bool changeDuringStartup) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "startup.mp4");
        Mp4Fixture.WriteOriented(source, 0);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(source), endpoint, "video/mp4");
        var item = new PlaylistItem { Source = server.Url + "?signature=KeepCase", Title = "Startup ratio", IsNetwork = true };
        var selector = Field<ComboBox>(window, "AspectRatioComboBox");
        selector.SelectedIndex = changeDuringStartup ? 1 : 4;
        var opening = Call<bool>(window, "AddItem", item, true);
        if (changeDuringStartup) selector.SelectedIndex = 4;
        Assert.True(await opening);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await AssertRotationPicture(window, 0, 1);
        selector.SelectedIndex = 5;
        await AssertRotationPicture(window, 0, 21d / 9);
        await Until(() => player.Time > 250);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.True(await Call<bool>(window, "PlayItem", item, false, 1000L));
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !Field<bool>(window, "_hasPendingPosition"));
        var restarted = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.NotSame(player, restarted);
        await AssertRotationPicture(window, 0, 21d / 9);
        Assert.Equal(item.Source, Field<PlaylistItem>(window, "_currentItem").Source);
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeAspect_CaptionsChaptersAndActualUnsupportedCaptionFallbackKeepState(bool network, bool off)
    {
        // VLC's paused H.264 input can report a buffered timestamp beyond the
        // requested seek. Compare with the unchanged compatibility-only path.
        var compatibilityClock = await ReadCompatibilityAspectClock(network, off);
        await WithWindow(async window =>
        {
            ShowPlaybackWindow(window);
            var source = Path.Combine(TestRoot(window), "captions.mp4");
            Mp4Fixture.Write(source, Mp4Fixture.Nero(0, 4000, 8000));
            var caption = Path.ChangeExtension(source, ".webvtt");
            File.WriteAllText(caption, BackgroundSubtitleFile());
            await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(source), ".mp4", "video/mp4") : null;
            var item = network ? new PlaylistItem { Source = server!.Url, Title = "Ratio captions HTTP", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
            await Call<bool>(window, "AddItem", item, true);
            await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
            if (network) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
            var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
            await Call(window, "TogglePlayPause");
            await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
            player.SelectAudioTrack(-1);
            await Call(window, "NavigateChapterAsync", true);
            Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
            Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
            await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
            var plain = Path.Combine(TestRoot(window), "plain.png");
            await Task.Run(() => player.SaveSnapshot(plain));
            var sourceImage = ReadCaptionBitmap(plain);
            var subtitles = Field<ComboBox>(window, "SubtitleTrackComboBox");
            foreach (var choice in new[] { 0, 1 })
            {
                subtitles.SelectedIndex = choice;
                var expected = SubtitlePresenter.Composite(sourceImage, choice == 0 ? null : PlainSubtitleTrack.TryLoad(caption)!.CueAt(4000));
                Assert.Equal(CaptionPixels(expected), CaptionPixels(ReadCaptionBitmap(await CapturePlayerSnapshot(window))));
                if (choice != 0) Assert.InRange(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").PictureAspectRatio, 1.777, 1.779);
            }
            subtitles.SelectedIndex = off ? 0 : 1;
            await Call(window, "NavigateChapterAsync", true);
            Assert.InRange(player.Time, 7900, 8100);
            Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
            var complex = Path.Combine(TestRoot(window), "complex.vtt");
            File.WriteAllText(complex, "WEBVTT\n\nSTYLE\n::cue { color: red; }\n\n00:00.000 --> 00:25.000\nComplex CSS\n");
            Assert.Null(PlainSubtitleTrack.TryLoad(complex));
            Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", complex));
            Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
            var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
            Assert.Equal("16:9", vlc.AspectRatio);
            Assert.Equal(90, Field<int>(window, "_rotation"));
            Assert.Equal(-1, vlc.AudioTrack);
            Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
            Assert.Equal(8000, Field<long>(window, "_compatibilityChapterTime"));
            Assert.InRange(vlc.Time, compatibilityClock - 100, compatibilityClock + 100);
            Assert.Equal(1.5f, vlc.Rate);
            Assert.Equal(2, vlc.SpuDescription.Count(track => track.Id >= 0));
            Assert.True(vlc.Spu >= 0); // The explicitly loaded compatibility caption is selected.
            Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(caption));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAspect_AudioRatiosKeepNativeCaptionsAndTheWholePane(bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateWave(window, "audio-aspect", 20);
        File.WriteAllText(Path.ChangeExtension(item.Source, ".srt"), ColorSubtitleFile(".srt"));
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var position = player.Time;
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 4;
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(0, Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").PictureAspectRatio);
        Assert.InRange(player.Time, position - 50, position + 50);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
    }, hardwareDecoding: hardware);

    [Fact]
    public async Task NativeAspect_CompatibilityPresentationContract() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "legacy-aspect.mp4");
        Mp4Fixture.WriteOriented(path, 0);
        using var library = new LibVLC("--quiet", "--aout=dummy", "--vout=direct3d11", "--avcodec-hw=none");
        using var renderer = new LibVLCSharp.Shared.MediaPlayer(library) { Hwnd = Field<PlaybackView>(window, "VideoView").WindowHandle };
        using var media = new Media(library, path, FromType.FromPath);
        try
        {
            Assert.True(renderer.Play(media));
            renderer.AspectRatio = "16:9";
            await Until(() => renderer.VoutCount > 0 && renderer.Time > 250);
            renderer.SetPause(true);
            await Until(() => renderer.State == VLCState.Paused);
            await AssertRotationPicture(window, 0, 16d / 9);
            var snapshot = Path.Combine(TestRoot(window), "legacy.png");
            Assert.True(renderer.TakeSnapshot(0, snapshot, 0, 0));
            await Until(() => File.Exists(snapshot));
            var image = ReadCaptionBitmap(snapshot);
            Assert.Equal(320, image.PixelWidth);
            Assert.Equal(240, image.PixelHeight);
        }
        finally { await Task.Run(renderer.Stop); renderer.Hwnd = 0; }
    });
}
