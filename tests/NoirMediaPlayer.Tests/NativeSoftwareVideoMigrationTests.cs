using System.IO;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    [Theory]
    [InlineData(0u, false)]
    [InlineData(90u, false)]
    [InlineData(180u, false)]
    [InlineData(270u, false)]
    [InlineData(0u, true)]
    [InlineData(90u, true)]
    [InlineData(180u, true)]
    [InlineData(270u, true)]
    public async Task NativeSoftware_RenderedFramesRetainOrientationRatiosAndRotation(uint sourceRotation, bool network) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "software-orientation.mp4");
        Mp4Fixture.WriteOriented(source, sourceRotation, aspectNumerator: 2);
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(source), ".mp4", "video/mp4") : null;
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "Software HTTP", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.True(player.UsesSoftwareVideo);
        Assert.Equal((nint)0, typeof(WindowsMediaEngine).GetField("_videoDeviceManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(player));
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused);
        await Task.Run(() => player.SeekChapter(3000));
        for (var rotation = 0; rotation < 360; rotation += 90)
        {
            var total = (sourceRotation + (uint)rotation) % 360;
            var aspect = total is 0 or 180 ? 640d / 240 : 240d / 640;
            await AssertRotationPicture(window, total, aspect);
            foreach (var (selection, ratio) in new[] { (1, 16d / 9), (2, 1.6), (3, 4d / 3), (4, 1d), (5, 21d / 9) })
            {
                Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = selection;
                await AssertRotationPicture(window, total, ratio);
            }
            Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 0;
            var capture = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
            AssertOrientedCorners(capture, total);
            Assert.Equal(total is 90 or 270 ? 240 : 640, capture.PixelWidth);
            Assert.Equal(total is 90 or 270 ? 640 : 240, capture.PixelHeight);
            if (rotation < 270) Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        }
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<Media?>(window, "_currentMedia"));
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
        Assert.InRange(player.Time, 2999, 3001);
        var finalRotation = (sourceRotation + 270) % 360;
        var finalAspect = finalRotation is 0 or 180 ? 640d / 240 : 240d / 640;
        await Task.Run(() => player.SeekChapter(9000));
        await AssertRotationPicture(window, finalRotation, finalAspect, later: true);
        await Task.Run(() => player.SeekChapter(7000));
        await AssertRotationPicture(window, finalRotation, finalAspect);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        // The encoded palette changes at eight seconds: verify live playback
        // supplies a new frame, independently of source snapshots and seeks.
        await AssertRotationPicture(window, finalRotation, finalAspect, later: true);
        Assert.InRange(player.Time, 8000, 12000);
    }, hardwareDecoding: false);

    [Theory]
    [InlineData(".mp4", false)]
    [InlineData(".mp4", true)]
    [InlineData(".wmv", false)]
    [InlineData(".wmv", true)]
    public async Task NativeSoftware_ChaptersCaptionsControlsAndRestartKeepTheSession(string extension, bool network) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "software-chapters" + extension);
        if (extension == ".mp4") Mp4Fixture.Write(source, Mp4Fixture.Nero(0, 4000, 8000));
        else AsfFixture.Write(source, true, 0, 4000, 8000);
        var caption = Path.ChangeExtension(source, ".webvtt");
        File.WriteAllText(caption, BackgroundSubtitleFile());
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(source), extension, "application/octet-stream") : null;
        var item = network ? new PlaylistItem { Source = server!.Url + "?signature=KeepCase", Title = "Software chapters", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        if (network) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        Assert.True(Field<WindowsMediaEngine?>(window, "_windowsPlayer") is not null, Field<string?>(window, "_windowsFailureReason"));
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => selector.Items.Count == 2);
        await Call<bool>(window, "PlayItem", item, false, 1000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !Field<bool>(window, "_hasPendingPosition"));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.True(player is not null, Field<string?>(window, "_windowsFailureReason"));
        Assert.NotNull(player);
        Assert.True(player.UsesSoftwareVideo);
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused);
        player.SelectAudioTrack(-1);
        Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Invoke(window, "SelectSpeed", 0.75f);
        foreach (var expected in new[] { 4000L, 8000L })
        {
            await Call(window, "NavigateChapterAsync", true);
            Assert.InRange(player.Time, expected - 1, expected + 1);
            var raw = Path.Combine(TestRoot(window), "expected-" + expected + ".png");
            await Task.Run(() => WindowsMediaEngine.SaveSourceSnapshot(item.Source, raw, expected, manualRotation: 90));
            var reference = ReadCaptionBitmap(raw);
            var captured = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
            var track = PlainSubtitleTrack.TryLoad(caption)!;
            Assert.Equal(CaptionPixels(Controls.SubtitlePresenter.Composite(reference, track.CueAt(expected))), CaptionPixels(captured));
        }
        selector.SelectedIndex = 0;
        await Until(() => Field<Controls.SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue is null);
        selector.SelectedIndex = 1;
        await Until(() => Field<Controls.SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue is not null);
        await Call(window, "NavigateChapterAsync", false);
        Assert.InRange(player.Time, 3999, 4001);
        Assert.Equal(0.75f, Field<PlayerSettings>(window, "_settings").PlaybackRate);
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        Assert.True(player.IsPaused);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(item.Source, Field<PlaylistItem>(window, "_currentItem").Source);
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
    }, hardwareDecoding: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSoftware_ResizeModesAndSourceReplacementReleaseThePresenter(bool rotated) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "software-resize.mp4");
        Mp4Fixture.WriteOriented(source, 0);
        var originalItem = MediaSourceService.CreateFileItem(source);
        await Call<bool>(window, "AddItem", originalItem, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused);
        if (rotated) Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        var rotation = rotated ? 90u : 0u;
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 5;
        foreach (var (width, height) in new[] { (1050, 680), (1400, 960) })
        {
            window.Width = width; window.Height = height;
            await AssertRotationPicture(window, rotation, 21d / 9);
        }
        Invoke(window, "ToggleCompact");
        await AssertRotationPicture(window, rotation, 21d / 9);
        Invoke(window, "ToggleCompact");
        Invoke(window, "ToggleFullscreen");
        await AssertRotationPicture(window, rotation, 21d / 9);
        Invoke(window, "ToggleFullscreen");
        window.WindowState = WindowState.Minimized;
        await Task.Delay(50);
        window.WindowState = WindowState.Normal;
        await AssertRotationPicture(window, rotation, 21d / 9);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 0;
        await AssertRotationPicture(window, rotation, rotated ? 0.75 : 4d / 3);
        var handle = Field<Controls.PlaybackView>(window, "VideoView").WindowHandle;
        Assert.True(RotationGetWindowRect(handle, out var originalBounds));
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 5;
        Assert.True(SoftwareSetWindowPos(handle, 0, 0, 0, 1, 1, 0x16));
        _ = CaptureRotationWindow(RotationGetAncestor(handle, 2));
        await Task.Delay(100);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.True(SoftwareSetWindowPos(handle, 0, 0, 0, originalBounds.Right - originalBounds.Left, originalBounds.Bottom - originalBounds.Top, 0x16));
        await AssertRotationPicture(window, rotation, 21d / 9);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 0;
        Assert.True(await Call<bool>(window, "AddItem", CreateWave(window, "next-audio", 20), true));
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        AssertNativeAudioCaptionPane(window);
        Assert.False(WindowsSoftwareVideoSurface.TryPaint(handle, 0x0014, 0));
        Assert.True(await Call<bool>(window, "PlayItem", originalItem, false, 0L));
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(Field<WindowsMediaEngine>(window, "_windowsPlayer").UsesSoftwareVideo);
        await AssertRotationPicture(window, rotation, rotated ? 0.75 : 4d / 3);
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
    }, hardwareDecoding: false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeSoftware_PresentationFailureAndComplexCaptionsRestoreCompatibilityState(bool failedPresenter, bool network) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "software-fallback.wmv");
        AsfFixture.Write(source, true, 0, 4000, 8000);
        var caption = Path.ChangeExtension(source, ".webvtt");
        File.WriteAllText(caption, BackgroundSubtitleFile());
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(source), ".wmv", "video/x-ms-wmv") : null;
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "Software fallback", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        if (network) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.True(player.UsesSoftwareVideo);
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused);
        player.SelectAudioTrack(-1);
        Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 4;
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        Assert.InRange(player.Time, 7999, 8001);
        if (failedPresenter)
        {
            // Exercise the real render-pump error path while paused: it must
            // notice a GDI failure even without another decoded video tick.
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var surface = typeof(WindowsMediaEngine).GetField("_softwareSurface", flags)!.GetValue(player)!;
            typeof(WindowsSoftwareVideoSurface).GetField("_error", flags)!.SetValue(surface, unchecked((int)0x80004005));
            await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null);
            Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
            Assert.NotNull(Field<string?>(window, "_windowsFailureReason"));
        }
        else
        {
            var complex = Path.Combine(TestRoot(window), "complex.vtt");
            File.WriteAllText(complex, "WEBVTT\n\nSTYLE\n::cue { color: red; }\n\n00:00.000 --> 00:25.000\nComplex CSS\n");
            Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", complex));
        }
        var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(1.5f, vlc.Rate);
        Assert.Equal(8000, Field<long>(window, "_compatibilityChapterTime"));
        Assert.Equal("1:1", vlc.AspectRatio);
        Assert.Equal(90, Field<int>(window, "_rotation"));
        var captions = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(failedPresenter ? 1 : 2, captions.Length);
        Assert.Equal(captions[^1].Id, vlc.Spu);
        Assert.Equal(item.Source, Field<PlaylistItem>(window, "_currentItem").Source);
        var handle = Field<Controls.PlaybackView>(window, "VideoView").WindowHandle;
        Assert.False(WindowsSoftwareVideoSurface.TryPaint(handle, 0x0014, 0));
    }, hardwareDecoding: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSoftware_EndOfStreamReleasesTheFrameAndCanAdvance(bool advance) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "software-end.mp4");
        Mp4Fixture.WriteOriented(source, 0);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(source), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await AssertRotationPicture(window, 0, 4d / 3);
        var next = CreateWave(window, "end-next", 20);
        await Call<bool>(window, "AddItem", next, false);
        Field<PlayerSettings>(window, "_settings").AutoPlayNext = advance;
        await Until(() => player.Length > 10000);
        player.Time = player.Length - 1000;
        await Until(() => advance ? ReferenceEquals(next, Field<PlaylistItem>(window, "_currentItem")) &&
            Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing :
            Field<PlaybackState>(window, "_playbackState") == PlaybackState.Ended);
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        var handle = Field<Controls.PlaybackView>(window, "VideoView").WindowHandle;
        Assert.False(WindowsSoftwareVideoSurface.TryPaint(handle, 0x0014, 0));
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
        if (advance) AssertNativeAudioCaptionPane(window);
        else Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    }, hardwareDecoding: false);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool SoftwareSetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
