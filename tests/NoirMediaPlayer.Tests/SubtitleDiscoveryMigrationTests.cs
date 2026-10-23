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
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubtitleDiscovery_AutomaticInventoryIsReadyWithoutAMetadataTimerTick(bool hardwareDecoding) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<System.Windows.Threading.DispatcherTimer>(window, "_uiTimer").Stop();
        var item = CreateWave(window, "initial-selector", 20);
        var first = Path.ChangeExtension(item.Source, ".vtt");
        var second = Path.ChangeExtension(item.Source, ".en_US.forced.srt");
        File.WriteAllText(first, BackgroundSubtitleFile());
        File.WriteAllText(second, "1\n00:00:00,000 --> 00:00:25,000\nAlternate caption\n");
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        Assert.Equal(3, selector.Items.Count);
        Assert.Equal(first, Assert.IsType<PlainSubtitleTrack>(((ComboBoxItem)selector.SelectedItem).Tag).Path);
        selector.SelectedIndex = 0;
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        Assert.Null(presenter.Cue);
        selector.SelectedIndex = 2;
        await Until(() => presenter.Text == "Alternate caption");
        Assert.Equal(second, Assert.IsType<PlainSubtitleTrack>(((ComboBoxItem)selector.SelectedItem).Tag).Path);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    }, hardwareDecoding);

    private static string WriteDiscoveredSubtitle(MainWindow window, string name, string caption)
    {
        var path = Path.GetFullPath(Path.Combine(TestRoot(window), name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Path.GetExtension(path).ToLowerInvariant() is ".vtt" or ".webvtt"
            ? SubtitleDiscoveryTests.WebCaption.Replace("Other caption", caption)
            : SubtitleDiscoveryTests.Caption.Replace("Selected caption", caption));
        return path;
    }

    [Theory]
    [InlineData("film.en.srt", false)]
    [InlineData("film.pt-BR.vtt", false)]
    [InlineData("subs/film.srt", false)]
    [InlineData("Subtitles/film.en.vtt", false)]
    [InlineData("film.srt", true)]
    [InlineData("subs/film.vtt", true)]
    [InlineData("film.en.forced.srt", false)]
    [InlineData("film.en_US.vtt", false)]
    [InlineData("film.English.srt", false)]
    [InlineData("film alternate.srt", false)]
    [InlineData("film_en.srt", false)]
    [InlineData("film - French.srt", false)]
    [InlineData("film (English SDH).vtt", false)]
    [InlineData("film[eng].srt", false)]
    [InlineData("subtitles/film.en.forced.vtt", false)]
    [InlineData("film.webvtt", true)]
    [InlineData("subs/film.webvtt", true)]
    [InlineData("subs/film.en.webvtt", false)]
    public async Task SubtitleDiscovery_NativeDefaultAndInventoryMatchVlc(string preferred, bool multiple) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateVideo(window, "film", audioTracks: 2);
        var path = WriteDiscoveredSubtitle(window, preferred, "Default caption");
        var descriptiveNames = preferred.EndsWith(".webvtt", StringComparison.OrdinalIgnoreCase);
        var english = descriptiveNames ? "film.en.forced.srt" : "film.en.srt";
        var french = descriptiveNames ? "subtitles/film.fr_FR.webvtt" : "subtitles/film.fr.vtt";
        if (multiple)
        {
            WriteDiscoveredSubtitle(window, english, "English caption");
            WriteDiscoveredSubtitle(window, french, "French caption");
        }
        var settings = Field<PlayerSettings>(window, "_settings");
        settings.PreferWindowsPlayback = false;
        await Call<bool>(window, "AddItem", item, true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        var count = multiple ? 3 : 1;
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing &&
            vlc.SpuDescription.Count(track => track.Id >= 0) == count);
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        var slaves = Field<Media>(window, "_currentMedia").Slaves.Where(slave => slave.Type == MediaSlaveType.Subtitle).ToArray();
        Assert.Equal(count, slaves.Length);
        Assert.Equal(path, new Uri(slaves[0].Uri).LocalPath, ignoreCase: true);
        _testOutput.WriteLine($"{preferred}: VLC tracks {string.Join(", ", tracks.Select(track => $"{track.Id}: {track.Name}"))}; selected {vlc.Spu}");
        Assert.Equal(tracks[0].Id, vlc.Spu); // VLC loads the unique highest-priority file first.
        settings.PreferWindowsPlayback = true;
        await Call<bool>(window, "PlayItem", item, false, 1000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing &&
            Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text == "Default caption");
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => selector.Items.Count == count + 1);
        Assert.Equal(path, Assert.IsType<PlainSubtitleTrack>(((ComboBoxItem)selector.SelectedItem).Tag).Path, ignoreCase: true);
        Assert.True(slaves.Select(slave => new Uri(slave.Uri).LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(selector.Items.OfType<ComboBoxItem>().Select(option => option.Tag).OfType<PlainSubtitleTrack>().Select(track => track.Path)));
        if (multiple)
        {
            selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().Single(option => option.Tag is PlainSubtitleTrack track && track.Path.EndsWith(Path.GetFileName(french)));
            await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text == "French caption");
            Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        }
    });

    [Theory]
    [InlineData("exact", false)]
    [InlineData("language", false)]
    [InlineData("off", false)]
    [InlineData("exact", true)]
    [InlineData("language", true)]
    [InlineData("off", true)]
    public async Task SubtitleDiscovery_RestartSnapshotAndHandoffKeepMultipleAutomaticTracks(string selected, bool descriptiveNames) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateVideo(window, "film", audioTracks: 2);
        var exact = descriptiveNames ? "subs/film.webvtt" : "subs/film.srt";
        var english = descriptiveNames ? "film.en.forced.vtt" : "film.en.vtt";
        var french = descriptiveNames ? "subtitles/film.fr_FR.srt" : "subtitles/film.fr.srt";
        WriteDiscoveredSubtitle(window, exact, "Default caption");
        WriteDiscoveredSubtitle(window, english, "English caption");
        WriteDiscoveredSubtitle(window, french, "French caption");
        await Call<bool>(window, "AddItem", item, true);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && selector.Items.Count == 4);
        var selectedPath = selected switch
        {
            "exact" => Path.GetFullPath(Path.Combine(TestRoot(window), exact)),
            "language" => Path.GetFullPath(Path.Combine(TestRoot(window), french)),
            _ => null
        };
        selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().Single(option => selectedPath is null
            ? option.Tag is int id && id == -1
            : option.Tag is PlainSubtitleTrack track && track.Path == selectedPath);
        await Call<bool>(window, "PlayItem", item, false, 3000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !Field<bool>(window, "_hasPendingPosition"));
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var selectedTrack = ((ComboBoxItem)selector.SelectedItem).Tag as PlainSubtitleTrack;
        Assert.Equal(selectedPath, selectedTrack?.Path);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        await Until(() => selectedTrack is null ? presenter.Cue is null : presenter.Text == selectedTrack.TextAt(player.Time));
        Assert.Equal(4, selector.Items.Count);
        var position = player.Time;
        Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
        await Until(() => Directory.EnumerateFiles(TestRoot(window), "*.png").Any());
        var snapshot = Directory.EnumerateFiles(TestRoot(window), "*.png").Single();
        var sourcePath = Path.Combine(TestRoot(window), "source.png");
        player.SaveSnapshot(sourcePath);
        var expected = SubtitlePresenter.Composite(ReadCaptionBitmap(sourcePath), selectedTrack?.CueAt(position));
        Assert.Equal(CaptionPixels(expected), CaptionPixels(ReadCaptionBitmap(snapshot)));
        player.SelectAudioTrack(-1);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => vlc.AspectRatio == "16:9" && vlc.AudioTrack == -1);
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(3, tracks.Length);
        // Restore uses the native inventory's order: exact, root English, folder French.
        Assert.Equal(selected switch { "exact" => tracks[0].Id, "language" => tracks[2].Id, _ => -1 }, vlc.Spu);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(vlc.Time, position - 300, position + 500);
        Assert.Null(presenter.Cue);
    });
}
