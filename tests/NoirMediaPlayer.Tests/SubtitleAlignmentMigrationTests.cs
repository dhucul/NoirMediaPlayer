using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    private static string AlignmentCaptionFile() => string.Join("\n", Enumerable.Range(1, 9).Select(number =>
        $"{number}\n00:00:{(number - 1) * 2:00},000 --> 00:00:{(number == 9 ? 25 : number * 2):00},000\n{{\\an{number}}}<b>Place</b>\n<font color=\"white\"><u>日本語 😀</u></font>\n"));

    private static SubtitleCue AlignmentReference(int number) => new((number - 1) * 2000, number == 9 ? 25000 : number * 2000,
        "Place\n日本語 😀", [new(0, 5, SubtitleStyle.Bold), new(6, 6, SubtitleStyle.Underline, 0xFFFFFF)], (SubtitleAlignment)number);

    private static void AssertLiveAlignment(MainWindow window, int number)
    {
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        presenter.UpdateLayout();
        var width = presenter.ActualWidth; var height = presenter.ActualHeight;
        Assert.True(width > 0 && height > 0);
        var picture = new Rect(0, 0, width, height);
        if (presenter.PictureAspectRatio > 0)
        {
            var fittedWidth = Math.Min(width, height * presenter.PictureAspectRatio);
            var fittedHeight = fittedWidth / presenter.PictureAspectRatio;
            picture = new Rect((width - fittedWidth) / 2, (height - fittedHeight) / 2, fittedWidth, fittedHeight);
        }
        var image = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        image.Render(presenter);
        AssertAlignmentRegion(image, number, picture);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    public async Task NativeAlignment_AllNineTimedPositionsKeepPlaybackSnapshotsAndControls(bool audio, bool network, bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "alignment" + (audio ? ".m4a" : ".mp4"));
        if (audio) Mp4Fixture.WriteAudio(source, Mp4Fixture.Nero(0, 4000, 8000));
        else Mp4Fixture.Write(source, Mp4Fixture.Nero(0, 4000, 8000));
        var captions = Path.ChangeExtension(source, ".srt");
        File.WriteAllText(captions, AlignmentCaptionFile());
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(source), audio ? ".m4a" : ".mp4", audio ? "audio/mp4" : "video/mp4") : null;
        var item = network ? new PlaylistItem { Source = server!.Url + "?signature=KeepCase", Title = "Positioned captions HTTP", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.NotNull(player);
        if (network) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", captions));
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => selector.Items.Count == 2);
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused && Field<long>(window, "_lastPlaybackLength") > 15000);
        player.SelectAudioTrack(-1);
        if (!audio) Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        foreach (var number in Enumerable.Range(1, 9).Append(2))
        {
            var time = (number - 1) * 2000L + 1000;
            var expected = AlignmentReference(number);
            Invoke(window, "SeekTo", time);
            await Until(() => Math.Abs(player.Time - time) < 150 && presenter.Cue?.Start == expected.Start);
            Assert.Equal(expected.Text, presenter.Text);
            Assert.Equal(expected.Alignment, presenter.Cue!.Alignment);
            Assert.Equal(expected.Styles, presenter.Cue.Styles);
            AssertLiveAlignment(window, number);
            if (audio) AssertNativeAudioCaptionPane(window);
            else
            {
                var raw = Path.Combine(TestRoot(window), "alignment-reference-" + time + ".png");
                await Task.Run(() => player.SaveSnapshot(raw));
                var snapshot = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
                Assert.Equal(CaptionPixels(SubtitlePresenter.Composite(ReadCaptionBitmap(raw), expected)), CaptionPixels(snapshot));
            }
        }
        selector.SelectedIndex = 0;
        Assert.Null(presenter.Cue);
        selector.SelectedIndex = 1;
        await Until(() => presenter.Cue is not null);
        Invoke(window, "SeekTo", 1000L);
        await Until(() => Math.Abs(player.Time - 1000) < 150);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => presenter.Cue?.Start == 8000);
        Assert.Equal(SubtitleAlignment.MiddleCenter, presenter.Cue!.Alignment);
        Assert.InRange(player.Time, 7999, 8001);
        Invoke(window, "SelectSpeed", 0.75f);
        await Call(window, "TogglePlayPause");
        await Until(() => !player.IsPaused);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        Assert.Equal(0.75f, Field<PlayerSettings>(window, "_settings").PlaybackRate);
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
        Assert.Equal(AlignmentCaptionFile(), File.ReadAllText(captions));
    }, hardwareDecoding: hardware);

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    public async Task NativeAlignment_RestartAndHandoffRetainPositionedFilesTracksAndTransport(bool off, bool network, bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "alignment-handoff.wmv");
        AsfFixture.Write(source, true, 0, 4000, 8000);
        var first = Path.ChangeExtension(source, ".srt");
        var second = Path.Combine(TestRoot(window), "manual.srt");
        File.WriteAllText(first, AlignmentCaptionFile());
        const string alternate = "1\n00:00:00,000 --> 00:00:25,000\n{\\an8}<font color=\"red\"><u>Retained position</u></font>\n";
        File.WriteAllText(second, alternate);
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(source), ".wmv", "video/x-ms-wmv") : null;
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "Positioned handoff HTTP", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        if (network) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", first));
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", second));
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => selector.Items.Count == 3);
        selector.SelectedIndex = off ? 0 : 2;
        await Call<bool>(window, "PlayItem", item, false, 1000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !Field<bool>(window, "_hasPendingPosition"));
        Assert.Equal(off ? 0 : 2, selector.SelectedIndex);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.NotNull(player);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        if (off) Assert.Null(presenter.Cue);
        else await Until(() => presenter.Cue?.Alignment == SubtitleAlignment.TopCenter);
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused);
        player.SelectAudioTrack(-1);
        Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.Equal(off ? -1 : tracks[1].Id, vlc.Spu);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(1.5f, vlc.Rate);
        Assert.Equal(8000, Field<long>(window, "_compatibilityChapterTime"));
        Assert.Equal("16:9", vlc.AspectRatio);
        Assert.Equal(90, Field<int>(window, "_rotation"));
        Assert.Equal(AlignmentCaptionFile(), File.ReadAllText(first));
        Assert.Equal(alternate, File.ReadAllText(second));
    }, hardwareDecoding: hardware);

    [Theory]
    [InlineData(0u, true)]
    [InlineData(0u, false)]
    [InlineData(90u, true)]
    [InlineData(90u, false)]
    public async Task NativeAlignment_ResizeModesRatiosAndRotationKeepPositionsInsideThePicture(uint sourceRotation, bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "alignment-oriented.mp4");
        Mp4Fixture.WriteOriented(source, sourceRotation, 2, 1);
        File.WriteAllText(Path.ChangeExtension(source, ".srt"), AlignmentCaptionFile());
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(source), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.NotNull(player);
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused && Field<long>(window, "_lastPlaybackLength") > 15000);
        Invoke(window, "SeekTo", 13000L);
        await Until(() => Math.Abs(player.Time - 13000) < 150 && Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue?.Alignment == SubtitleAlignment.TopLeft);
        async Task Check()
        {
            await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
            await Until(() => Math.Abs(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").PictureAspectRatio - player.PresentationAspectRatio) < 0.001);
            window.UpdateLayout();
            AssertLiveAlignment(window, 7);
            Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
            Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
            Assert.InRange(player.Time, 12999, 13001);
        }
        foreach (var ratio in new[] { 1, 2, 3, 4, 5, 0 })
        {
            Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = ratio;
            await Check();
        }
        window.Width = 1050; window.Height = 680;
        await Check();
        window.Width = 1400; window.Height = 960;
        await Check();
        Invoke(window, "ToggleCompact");
        await Check();
        Invoke(window, "ToggleCompact");
        Invoke(window, "ToggleFullscreen");
        await Check();
        Invoke(window, "ToggleFullscreen");
        window.WindowState = WindowState.Minimized;
        await Task.Delay(50);
        window.WindowState = WindowState.Normal;
        await Check();
        for (var turn = 0; turn < 4; turn++)
        {
            Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
            await Check();
            var raw = Path.Combine(TestRoot(window), "oriented-alignment-reference-" + turn + ".png");
            await Task.Run(() => player.SaveSnapshot(raw));
            var snapshot = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
            Assert.Equal(CaptionPixels(SubtitlePresenter.Composite(ReadCaptionBitmap(raw), AlignmentReference(7))), CaptionPixels(snapshot));
        }
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
    }, hardwareDecoding: hardware);

    [Theory]
    [InlineData("position")]
    [InlineData("animation")]
    [InlineData("karaoke")]
    public async Task NativeAlignment_UnreadyOverridesLoadOriginalCompatibilityTracks(string kind) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "alignment-unready.wmv");
        AsfFixture.Write(source, true, 0, 4000, 8000);
        var first = Path.ChangeExtension(source, ".srt");
        File.WriteAllText(first, AlignmentCaptionFile());
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(source), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.NotNull(player);
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused);
        player.SelectAudioTrack(-1);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        var unsupported = Path.Combine(TestRoot(window), "unready.srt");
        var tag = kind switch { "position" => "{\\pos(10,20)}", "animation" => "{\\move(0,0,10,10)}", _ => "{\\k20}" };
        var content = "1\n00:00:00,000 --> 00:00:25,000\n{\\an8}" + tag + "Unready layout\n";
        File.WriteAllText(unsupported, content);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", unsupported));
        var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.Equal(tracks[^1].Id, vlc.Spu);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(1.5f, vlc.Rate);
        Assert.Equal(8000, Field<long>(window, "_compatibilityChapterTime"));
        Assert.Equal(content, File.ReadAllText(unsupported));
        Assert.Equal(AlignmentCaptionFile(), File.ReadAllText(first));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });
}
