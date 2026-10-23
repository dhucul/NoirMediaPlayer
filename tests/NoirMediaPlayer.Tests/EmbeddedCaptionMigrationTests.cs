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
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddedCaptions_MatroskaTextUsesWindowsAndSupportsPausedSelectionAndSnapshot(bool video) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "embedded-native.mkv");
        MatroskaFixture.Write(path, subtitles: true, video: video, chapters: MatroskaFixture.SimpleChapters());
        var item = new PlaylistItem { Source = path, Title = "Embedded captions" };
        await Call<bool>(window, "AddItem", item, true);
        try { await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing); }
        catch (TaskCanceledException)
        {
            Assert.Fail($"Embedded caption startup: state={Field<PlaybackState>(window, "_playbackState")}; pending={Field<bool>(window, "_windowsStartupPending")}; reason={Field<string?>(window, "_windowsFailureReason")}");
        }
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.True(player.HasSupportedStreams);
        Assert.Single(player.CaptionTracks);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => selector.Items.Count == 2);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        await Until(() => presenter.Text == "Embedded captions");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        player.Time = 1000;
        await Until(() => Math.Abs(player.Time - 1000) < 100 && presenter.Text == "Embedded captions");
        selector.SelectedIndex = 0;
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle && presenter.Cue is null);
        Assert.DoesNotContain(player.CaptionTracks, track => track.Selected);
        selector.SelectedIndex = 1;
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle && presenter.Text == "Embedded captions");
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        if (video)
        {
            var snapshot = Path.Combine(TestRoot(window), "embedded-snapshot.png");
            player.SaveSnapshot(snapshot);
            using var snapshotStream = File.OpenRead(snapshot);
            var image = BitmapFrame.Create(snapshotStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(320, image.PixelWidth);
            Assert.Equal(240, image.PixelHeight);
            var pixels = CaptionPixels(image);
            Assert.Contains(Enumerable.Range(0, pixels.Length / 4), index => pixels[index * 4] > 220 && pixels[index * 4 + 1] > 220 && pixels[index * 4 + 2] > 220);
        }
        player.Time = 5000;
        await Until(() => Math.Abs(player.Time - 5000) < 100 && presenter.Cue is null);
        player.Time = 1000;
        await Until(() => Math.Abs(player.Time - 1000) < 100 && presenter.Text == "Embedded captions");
        Assert.Null(Field<Media?>(window, "_currentMedia"));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmbeddedCaptions_HandoffPreservesSelectionPauseAudioAndPosition(bool selected) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "embedded-handoff.mkv");
        MatroskaFixture.Write(path, subtitles: true);
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = path, Title = "Embedded handoff" }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => selector.Items.Count == 2);
        if (!selected) selector.SelectedIndex = 0;
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        player.SelectAudioTrack(-1);
        player.Time = 4000;
        await Until(() => Math.Abs(player.Time - 4000) < 100);
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle && vlc.AudioTrack == -1);
        Assert.Equal(selected, vlc.Spu >= 0);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(vlc.Time, 3700, 4500);
        Assert.Null(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddedCaptions_ExternalAndMultipleEmbeddedChoicesRetainTheirSelectionAcrossHandoff(bool externalSelected) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "embedded-choices.mkv");
        MatroskaFixture.Write(path, true, subtitleTracks: 2);
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = path, Title = "Embedded choices" }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        await Until(() => selector.Items.Count == 3);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        player.Time = 1000;
        selector.SelectedIndex = 2;
        await Until(() => presenter.Text == "Alternate embedded captions");
        Assert.Contains("French", player.CaptionTracks[1].Name);
        var external = Path.Combine(TestRoot(window), "manual.srt");
        File.WriteAllText(external, "1\n00:00:00,000 --> 00:00:03,000\nExternal choice\n");
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", external));
        await Until(() => presenter.Text == "External choice");
        Assert.DoesNotContain(player.CaptionTracks, track => track.Selected);
        selector.SelectedIndex = 2;
        await Until(() => presenter.Text == "Alternate embedded captions");
        if (externalSelected)
        {
            selector.SelectedIndex = 3;
            await Until(() => presenter.Text == "External choice");
        }
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle && vlc.SpuDescription.Count(track => track.Id >= 0) == 3);
        Assert.Equal(vlc.SpuDescription.Where(track => track.Id >= 0).ElementAt(externalSelected ? 2 : 1).Id, vlc.Spu);
    });

    [Theory]
    [InlineData("<b>Styled embedded captions</b>")]
    [InlineData("{\\an7}Positioned embedded captions")]
    public async Task EmbeddedCaptions_UnsupportedFormattingFallsBackWithTheEmbeddedTrackSelected(string text) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "embedded-styled.mkv");
        MatroskaFixture.Write(path, true, subtitleText: text);
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = path, Title = "Styled embedded" }, true);
        try { await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null); }
        catch (TaskCanceledException)
        {
            Assert.Fail($"Caption fallback did not finish: state={Field<PlaybackState>(window, "_playbackState")}; native={Field<WindowsMediaEngine?>(window, "_windowsPlayer") is not null}; reason={Field<string?>(window, "_windowsFailureReason")}; text={Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text}");
        }
        var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        await Until(() => vlc.Spu >= 0);
        Assert.NotNull(Field<string?>(window, "_windowsFailureReason"));
        Assert.Null(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task EmbeddedCaptions_CustomDefaultSelectionKeepsTheCompatibilityTrackChoice(bool isDefault, bool forced) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "caption-defaults.mkv");
        MatroskaFixture.Write(path, true, subtitleTracks: 2, subtitleDefault: isDefault, subtitleForced: forced);
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = path, Title = "Caption defaults" }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null);
        var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        await Until(() => vlc.SpuDescription.Count(track => track.Id >= 0) == 2 && vlc.Spu >= 0);
        Assert.Equal(vlc.SpuDescription.Where(track => track.Id >= 0).ElementAt(1).Id, vlc.Spu);
    });

    [Fact]
    public async Task EmbeddedCaptions_OrdinaryDefaultSelectionMatchesCompatibilityPlayback() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "ordinary-caption-defaults.mkv");
        MatroskaFixture.Write(path, true, subtitleTracks: 2);
        var item = new PlaylistItem { Source = path, Title = "Ordinary caption defaults" };
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        var nativeOrdinal = Field<int?>(window, "_selectedEmbeddedSubtitleOrdinal")!.Value;
        // Open a fresh compatibility session without restoring the native choice.
        await Call(window, "ReleaseCurrentMedia");
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
        await Call<bool>(window, "PlayItem", item, false, 0L);
        var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && vlc.SpuDescription.Count(track => track.Id >= 0) == 2 && vlc.Spu >= 0);
        Assert.Equal(vlc.SpuDescription.Where(track => track.Id >= 0).ElementAt(nativeOrdinal).Id, vlc.Spu);
    });

    [Fact]
    public async Task EmbeddedCaptions_OverlappingTextKeepsCompatibilityRendering() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "caption-overlap.mkv");
        MatroskaFixture.Write(path, true, laterSubtitleText: "Overlapping caption", laterSubtitleSecond: 2);
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = path, Title = "Overlapping captions" }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null);
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        Assert.True(Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer").Spu >= 0);
    });

    [Fact]
    public async Task EmbeddedCaptions_SeekingIntoLaterStyledTextRetainsCompatibilityRendering() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "later-styled.mkv");
        MatroskaFixture.Write(path, true, laterSubtitleText: "<b>Later styled caption</b>");
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = path, Title = "Later styled" }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        player.Time = 6000;
        await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null);
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        Assert.True(vlc.Spu >= 0);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(vlc.Time, 5700, 6500);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task EmbeddedCaptions_ReplayRestoresOffOrAlternateTrackBeforeStartup(int selection) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "replay-captions.mkv");
        MatroskaFixture.Write(path, true, subtitleTracks: 2);
        var item = new PlaylistItem { Source = path, Title = "Replay captions" };
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => selector.Items.Count == 3);
        selector.SelectedIndex = selection;
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        await Call<bool>(window, "PlayItem", item, false, 1000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !Field<bool>(window, "_hasPendingPosition"));
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        await Until(() => selector.SelectedIndex == selection);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        if (selection == 0) Assert.Null(presenter.Cue);
        else await Until(() => presenter.Text == "Alternate embedded captions");
    });
}
