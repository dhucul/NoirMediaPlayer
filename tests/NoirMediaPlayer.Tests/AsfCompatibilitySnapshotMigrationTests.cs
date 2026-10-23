using System.Diagnostics;
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
    [Theory]
    [InlineData("local", false, true)]
    [InlineData("http", false, true)]
    [InlineData("endpoint", false, true)]
    [InlineData("local", true, false)]
    [InlineData("http", true, false)]
    [InlineData("endpoint", true, false)]
    public async Task AsfCompatibilityCapture_PausedChapterPicturesKeepCaptionsAndTransport(string location, bool hardware, bool preferNative) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = preferNative;
        var path = Path.Combine(TestRoot(window), "capture.wmv");
        AsfFixture.Write(path, true, 0, 4000, 8000);
        await using var server = location == "local" ? null : new LoopbackMediaServer(File.ReadAllBytes(path), location == "http" ? ".wmv" : ".php", "application/octet-stream");
        var item = server is null ? MediaSourceService.CreateFileItem(path) : new PlaylistItem
        {
            Source = server.Url + "?signature=KeepCase&file=AbC", Title = "Compatibility source capture", IsNetwork = true
        };
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        // Software video is now native. Explicitly hand off these cases so they
        // continue testing independent ASF capture during compatibility playback.
        if (preferNative) Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing &&
            Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null && vlc.VoutCount > 0 && vlc.Time > 250);
        var media = Field<Media>(window, "_currentMedia");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var plain = Path.Combine(TestRoot(window), "plain.srt");
        var styled = Path.Combine(TestRoot(window), "styled.webvtt");
        File.WriteAllText(plain, ColorSubtitleFile(".srt"));
        File.WriteAllText(styled, BackgroundSubtitleFile());
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", plain));
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", styled));
        Assert.True(vlc.SetAudioTrack(-1));
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        Assert.Equal(3, selector.Items.Count);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        Assert.Equal(8000, Field<long>(window, "_compatibilityChapterTime"));
        selector.SelectedIndex = 0;
        await Until(() => vlc.Spu == -1 && Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        var sourcePicture = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
        AssertMp4SnapshotColor(Directory.EnumerateFiles(TestRoot(window), "*.png").Single(), blue: false);
        foreach (var choice in new[] { 1, 2, 0 })
        {
            selector.SelectedIndex = choice;
            var selected = (int)((ComboBoxItem)selector.SelectedItem).Tag;
            await Until(() => vlc.Spu == selected && Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
            var track = choice == 0 ? null : PlainSubtitleTrack.TryLoad(choice == 1 ? plain : styled);
            var captured = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
            Assert.Equal(CaptionPixels(SubtitlePresenter.Composite(sourcePicture, track?.CueAt(8000))), CaptionPixels(captured));
            Assert.Equal(8000, Field<long>(window, "_compatibilityChapterTime"));
            Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
            Assert.Equal(-1, vlc.AudioTrack);
            Assert.Same(media, Field<Media>(window, "_currentMedia"));
        }
        await Call(window, "NavigateChapterAsync", false);
        Assert.Equal(4000, Field<long>(window, "_compatibilityChapterTime"));
        AssertMp4SnapshotColor(await CapturePlayerSnapshot(window), blue: true);
        Assert.Equal(item.Source, Field<PlaylistItem>(window, "_currentItem").Source);
        Assert.Equal(1.5f, vlc.Rate);
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue);
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(styled));
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && vlc.Time >= 4000 && vlc.Time < 5500);
        AssertMp4SnapshotColor(await CapturePlayerSnapshot(window), blue: true);
        Assert.Equal(PlaybackState.Playing, Field<PlaybackState>(window, "_playbackState"));
        Assert.Same(media, Field<Media>(window, "_currentMedia"));
    }, hardwareDecoding: hardware);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AsfCompatibilityCapture_RestoredNativeCaptionIdsRemainSelectable(bool automatic, bool off) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "handoff.wmv");
        AsfFixture.Write(path, true, 0, 4000, 8000);
        var caption = automatic ? Path.ChangeExtension(path, ".webvtt") : Path.Combine(TestRoot(window), "manual.webvtt");
        File.WriteAllText(caption, BackgroundSubtitleFile());
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var native = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        if (!automatic) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        selector.SelectedIndex = off ? 0 : 1;
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        native.SelectAudioTrack(-1);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        var expectedPath = Path.Combine(TestRoot(window), "source.png");
        await Task.Run(() => WindowsMediaEngine.SaveSourceSnapshot(path, expectedPath, 8000));
        var source = ReadCaptionBitmap(expectedPath);
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        var media = Field<Media>(window, "_currentMedia");
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
        var expected = SubtitlePresenter.Composite(source, off ? null : PlainSubtitleTrack.TryLoad(caption)!.CueAt(8000));
        Assert.Equal(CaptionPixels(expected), CaptionPixels(ReadCaptionBitmap(await CapturePlayerSnapshot(window))));
        selector.SelectedIndex = off ? 1 : 0;
        await Until(() => (vlc.Spu < 0) == !off && Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        expected = SubtitlePresenter.Composite(source, off ? PlainSubtitleTrack.TryLoad(caption)!.CueAt(8000) : null);
        Assert.Equal(CaptionPixels(expected), CaptionPixels(ReadCaptionBitmap(await CapturePlayerSnapshot(window))));
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(8000, Field<long>(window, "_compatibilityChapterTime"));
        Assert.Equal(1.5f, vlc.Rate);
        Assert.Same(media, Field<Media>(window, "_currentMedia"));
    });

    [Theory]
    [InlineData(LoopbackResponse.IgnoreRanges)]
    [InlineData(LoopbackResponse.WrongRange)]
    [InlineData(LoopbackResponse.StallHeaders)]
    public async Task AsfCompatibilityCapture_FailedWindowsReadRetriesExistingRenderer(LoopbackResponse response) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
        var path = Path.Combine(TestRoot(window), "fallback.wmv");
        AsfFixture.Write(path, true, 0, 4000, 8000);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), ".bin", "application/octet-stream");
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "Capture fallback", IsNetwork = true }, true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && vlc.VoutCount > 0 && vlc.Time > 250);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var media = Field<Media>(window, "_currentMedia");
        var before = server.RangeRequests;
        server.Response = response;
        server.ResetResponseAfterRequest = true;
        var clock = Stopwatch.StartNew();
        var captured = await CapturePlayerSnapshot(window, 12);
        AssertMp4SnapshotColor(captured, blue: true);
        Assert.True(server.RangeRequests > before);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 12);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Same(media, Field<Media>(window, "_currentMedia"));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData("rotation")]
    [InlineData("aspect")]
    [InlineData("complex")]
    [InlineData("changed")]
    [InlineData("unknown")]
    public async Task AsfCompatibilityCapture_UnreadyPresentationAndCaptionTracksKeepTheirRenderer(string kind) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
        var path = Path.Combine(TestRoot(window), "legacy-capture.wmv");
        AsfFixture.Write(path, true, 0, 4000, 8000);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), ".wmv", "video/x-ms-wmv");
        var item = new PlaylistItem { Source = server.Url, Title = "Legacy capture", IsNetwork = true };
        await Call<bool>(window, "AddItem", item, true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && vlc.VoutCount > 0 && vlc.Time > 250);
        if (kind == "rotation")
        {
            var previous = Field<Media>(window, "_currentMedia");
            Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
            await Until(() => !ReferenceEquals(previous, Field<Media?>(window, "_currentMedia")) &&
                Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !Field<bool>(window, "_hasPendingPosition") && vlc.VoutCount > 0);
        }
        if (kind == "aspect")
        {
            Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
            await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
            Assert.False(string.IsNullOrWhiteSpace(vlc.AspectRatio));
        }
        if (kind == "complex")
        {
            var caption = Path.Combine(TestRoot(window), "complex.webvtt");
            File.WriteAllText(caption, "WEBVTT\n\nSTYLE\n::cue { color: red; }\n\n00:00.000 --> 00:25.000\nComplex CSS caption\n");
            Assert.Null(PlainSubtitleTrack.TryLoad(caption));
            Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        }
        if (kind == "unknown")
        {
            var caption = Path.Combine(TestRoot(window), "unknown.srt");
            File.WriteAllText(caption, ColorSubtitleFile(".srt"));
            // Model a caption input discovered by the compatibility backend,
            // without a confirmed file-to-ID association in the application.
            Assert.True(vlc.AddSlave(MediaSlaveType.Subtitle, new Uri(caption).AbsoluteUri, false));
            await Until(() => vlc.SpuDescription.Any(track => track.Id >= 0));
            Assert.True(vlc.SetSpu(vlc.SpuDescription.Single(track => track.Id >= 0).Id));
        }
        if (kind == "changed")
        {
            var caption = Path.Combine(TestRoot(window), "changed.srt");
            File.WriteAllText(caption, ColorSubtitleFile(".srt"));
            Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
            File.WriteAllText(caption, "1\n00:00:00,000 --> 00:00:25,000\nA different caption file after the compatibility renderer loaded its own input\n");
        }
        if (kind is "unknown" or "complex" or "changed") await Until(() => vlc.Spu >= 0);
        // A reopened input can expose a video output before it presents a frame.
        // Pause only after the current media has supplied a real capture target.
        await Until(() => vlc.Time > 250 && Field<Media>(window, "_currentMedia").Statistics is { DisplayedPictures: > 0 });
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        Assert.NotNull(Field<NativeChapterMap?>(window, "_compatibilityChapters"));
        var selected = vlc.Spu;
        var skipped = Path.Combine(TestRoot(window), "must-not-be-written.png");
        var saved = true;
        await Field<PlaybackOperationQueue>(window, "_playbackOperations").Enqueue(() =>
            saved = Invoke(window, "TryCreateWindowsAsfSnapshot", item.Source, kind is not ("rotation" or "aspect")) is not null);
        Assert.False(saved);
        Assert.False(File.Exists(skipped));
        var ranges = server?.RangeRequests;
        var captured = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
        Assert.Equal(ranges, server?.RangeRequests);
        var reference = Path.Combine(TestRoot(window), "renderer-reference.png");
        Assert.True(await Task.Run(() => vlc.TakeSnapshot(0, reference, 0, 0)));
        await Until(() => File.Exists(reference));
        Assert.Equal(CaptionPixels(ReadCaptionBitmap(reference)), CaptionPixels(captured));
        if (kind == "rotation") Assert.Equal(90, Field<int>(window, "_rotation"));
        Assert.Equal(selected, vlc.Spu);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Fact]
    public async Task AsfCompatibilityCapture_SourceReplacementCannotReusePreviousCaptionIds() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
        var first = Path.Combine(TestRoot(window), "first.wmv");
        AsfFixture.Write(first, true, 0, 4000, 8000);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(first), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var caption = Path.Combine(TestRoot(window), "registered.webvtt");
        File.WriteAllText(caption, BackgroundSubtitleFile());
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        var second = Path.Combine(TestRoot(window), "second.wmv");
        AsfFixture.Write(second, true, 0, 4000, 8000);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(second), true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && vlc.VoutCount > 0 && vlc.Time > 250);
        var folder = Path.Combine(TestRoot(window), "unregistered");
        Directory.CreateDirectory(folder);
        var unknown = Path.Combine(folder, "second.srt");
        File.WriteAllText(unknown, ColorSubtitleFile(".srt"));
        Assert.True(vlc.AddSlave(MediaSlaveType.Subtitle, new Uri(unknown).AbsoluteUri, false));
        await Until(() => vlc.SpuDescription.Any(track => track.Id >= 0));
        Assert.True(vlc.SetSpu(vlc.SpuDescription.Single(track => track.Id >= 0).Id));
        await Until(() => vlc.Spu >= 0);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var saved = true;
        var skipped = Path.Combine(TestRoot(window), "wrong-caption.png");
        await Field<PlaybackOperationQueue>(window, "_playbackOperations").Enqueue(() =>
            saved = Invoke(window, "TryCreateWindowsAsfSnapshot", second, true) is not null);
        Assert.False(saved);
        Assert.False(File.Exists(skipped));
        Assert.Equal(320, ReadCaptionBitmap(await CapturePlayerSnapshot(window)).PixelWidth);
        Assert.True(vlc.Spu >= 0);
    });
}
