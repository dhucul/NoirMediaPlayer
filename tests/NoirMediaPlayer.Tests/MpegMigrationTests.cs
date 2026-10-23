using System.IO;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    private static string MpegDiagnostics(MainWindow window)
    {
        var compatibility = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        return $"audio={compatibility.AudioTrack}; tracks={string.Join(',', compatibility.AudioTrackDescription.Select(track => $"{track.Id}:{track.Name}"))}; pendingAudio={Field<int?>(window, "_pendingCompatibilityAudioOrdinal")}; audioTask={Field<Task?>(window, "_compatibilityAudioRestoreTask")?.Status}; audioError={Field<Task?>(window, "_compatibilityAudioRestoreTask")?.Exception}; stats={Field<Media?>(window, "_currentMedia")?.Statistics.DecodedAudio}";
    }
    [Theory]
    [InlineData(".mpg", true)]
    [InlineData(".mpeg", true)]
    [InlineData(".vob", true)]
    [InlineData(".mpg", false)]
    [InlineData(".mpeg", false)]
    [InlineData(".vob", false)]
    public async Task NativeMpeg_PlaybackRetainsAudioAndPresentation(string extension, bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "native-mpeg" + extension);
        MpegFixture.Write(path);
        var item = MediaSourceService.CreateFileItem(path);
        Assert.True(await Call<bool>(window, "AddItem", item, true));
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.True(player is not null, Field<string?>(window, "_windowsFailureReason"));
        Assert.NotNull(player);
        Assert.True(player.HasAudio && player.HasVideo && player.HasSupportedStreams);
        Assert.Equal(!hardware, player.UsesSoftwareVideo);
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused && Field<long>(window, "_lastPlaybackLength") > 15_000);
        player.SelectAudioTrack(-1);
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        player.SelectAudioTrack(player.AudioTracks[0].Id);
        Assert.True(player.AudioTracks[0].Selected);
        var before = player.Time;
        await Call(window, "NavigateChapterAsync", true);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.InRange(player.Time, before - 150, before + 150);
        window.Width += 130; window.Height += 70;
        await Task.Delay(100);
        Assert.InRange(player.PresentationAspectRatio, 1.332, 1.334);
        await Call(window, "TogglePlayPause");
        await Until(() => !player.IsPaused && player.Time > before + 150);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<Media?>(window, "_currentMedia"));
    }, hardwareDecoding: hardware);

    [Theory]
    [InlineData(".mpg", false)]
    [InlineData(".mpg", true)]
    [InlineData(".mpeg", false)]
    [InlineData(".vob", true)]
    public async Task NativeMpeg_SeekAndCaptureHandoffRetainPauseAudioAndRate(string extension, bool capture) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "mpeg-handoff" + extension);
        MpegFixture.Write(path);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused && Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused && Field<long>(window, "_lastPlaybackLength") > 15_000);
        player.SelectAudioTrack(-1);
        Invoke(window, "SelectSpeed", 1.25f);
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        if (capture)
        {
            string snapshot;
            try { snapshot = await CapturePlayerSnapshot(window, timeoutSeconds: 12); }
            catch (TaskCanceledException)
            {
                Assert.Fail($"MPEG capture: state={Field<PlaybackState>(window, "_playbackState")}; native={Field<WindowsMediaEngine?>(window, "_windowsPlayer") is not null}; idle={Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle}; pendingCaptions={Field<object?>(window, "_pendingCompatibilitySubtitles") is not null}; pendingPosition={Field<bool>(window, "_hasPendingPosition")}; captionRestore={Field<Task?>(window, "_compatibilitySubtitleRestoreTask")?.Exception}; {MpegDiagnostics(window)}");
                return;
            }
            var image = ReadCaptionBitmap(snapshot);
            Assert.Equal(320, image.PixelWidth);
            Assert.Equal(240, image.PixelHeight);
            var pixel = new byte[4];
            image.CopyPixels(new System.Windows.Int32Rect(160, 120, 1, 1), pixel, 4, 0);
            Assert.True(pixel[0] > 140 && pixel[2] < 100);
        }
        else Invoke(window, "SeekTo", 9000L);
        await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null);
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"), MpegDiagnostics(window));
        var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.InRange(vlc.Rate, 1.249f, 1.251f);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        if (!capture) await Until(() => Math.Abs(vlc.Time - 9000) < 500);
    }, realCompatibilityVideo: true);

    [Fact]
    public async Task NativeMpeg_LoadingCaptionsHandsOffBeforeNativePresentation() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "manual-caption.mpg");
        MpegFixture.Write(path);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        var subtitle = Path.ChangeExtension(path, ".srt");
        File.WriteAllText(subtitle, "1\n00:00:00,000 --> 00:00:25,000\nCompatibility MPEG captions\n");
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", subtitle));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        Assert.True(vlc.Spu >= 0);
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
    });

    [Fact]
    public async Task NativeMpeg_SavedResumeUsesCompatibilityForAccuratePosition() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "resume.mpg");
        MpegFixture.Write(path, longer: true);
        var settings = Field<PlayerSettings>(window, "_settings");
        settings.RememberPosition = true;
        settings.ResumePositions[path] = 12_000;
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null &&
            Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing &&
            Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer").Time >= 11_500);
        Assert.InRange(Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer").Time, 11_500, 14_500);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeMpeg_VideoOnlyRetainsItsCompleteInventory(bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "video-only.mpg");
        MpegFixture.Write(path, videoOnly: true);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.True(player.HasVideo && !player.HasAudio && player.HasSupportedStreams);
        Assert.Empty(player.AudioTracks);
        Assert.Equal(!hardware, player.UsesSoftwareVideo);
        await Until(() => player.Time > 300);
    }, hardwareDecoding: hardware);

    [Fact]
    public async Task MpegTransport_AdditionalAudioRetainsAllCompatibilityTracks() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "mpeg-multiple-audio.m2ts");
        MpegFixture.Write(path, audioTracks: 2);
        Assert.Null(MpegStreamInspection.TryRead(path));
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        await Until(() => vlc.AudioTrackDescription.Count(track => track.Id >= 0) == 2);
        var second = vlc.AudioTrackDescription.Where(track => track.Id >= 0).ElementAt(1).Id;
        Assert.True(vlc.SetAudioTrack(second));
        await Until(() => vlc.AudioTrack == second);
    });
}
