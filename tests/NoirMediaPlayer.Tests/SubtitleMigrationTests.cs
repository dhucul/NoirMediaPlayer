using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;
using Xunit.Abstractions;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    private readonly ITestOutputHelper _testOutput;
    public LogicAuditRegressionTests(ITestOutputHelper testOutput) => _testOutput = testOutput;

    [Fact]
    public async Task Compatibility_PausedAutomaticCaptionsStopWithinDrainDeadline() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
        var item = CreateVideo(window, "compat-captions", audioTracks: 2);
        File.WriteAllText(Path.ChangeExtension(item.Source, ".srt"), "1\n00:00:00,000 --> 00:00:25,000\nCompatibility caption\n");
        await Call<bool>(window, "AddItem", item, true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && vlc.Spu >= 0);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 3000);
        Invoke(window, "SeekTo", 3000L);
        await Until(() => Math.Abs(vlc.Time - 3000) < 150);
        var clock = Stopwatch.StartNew();
        Invoke(window, "Stop_Click", window, new RoutedEventArgs());
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle, timeoutSeconds: 10);
        _testOutput.WriteLine($"Existing VLC paused-caption drain: {clock.ElapsedMilliseconds} ms.");
    });

    [Theory]
    [InlineData(".srt")]
    [InlineData(".vtt")]
    public async Task WindowsNative_ExactSidecarMatchesCompatibilitySelection(string extension) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateVideo(window, "auto-captions");
        var subtitle = Path.ChangeExtension(item.Source, extension);
        File.WriteAllText(subtitle, extension == ".srt"
            ? "1\n00:00:00,000 --> 00:00:20,000\nAutomatic caption\n"
            : "WEBVTT\n\n00:00.000 --> 00:20.000\nAutomatic caption\n");
        var settings = Field<PlayerSettings>(window, "_settings");
        settings.PreferWindowsPlayback = false;
        await Call<bool>(window, "AddItem", item, true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing &&
            vlc.SpuDescription.Count(track => track.Id >= 0) == 1);
        Assert.True(vlc.Spu >= 0, "VLC should select the exact-name automatic subtitle.");
        settings.PreferWindowsPlayback = true;
        await Call<bool>(window, "PlayItem", item, false, 1000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text == "Automatic caption");
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => selector.Items.Count == 2);
        Assert.Equal(Path.GetFileName(subtitle), ((ComboBoxItem)selector.SelectedItem).Content);
    });

    [Theory]
    [InlineData(".srt", true)]
    [InlineData(".srt", false)]
    [InlineData(".vtt", true)]
    [InlineData(".vtt", false)]
    public async Task WindowsNative_AutomaticSidecarSurvivesRestartAndHandoffWithoutDuplicates(string extension, bool selected) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateVideo(window, "automatic-handoff", audioTracks: 2);
        var subtitle = Path.ChangeExtension(item.Source, extension);
        File.WriteAllText(subtitle, extension == ".srt"
            ? "1\n00:00:00,000 --> 00:00:25,000\nRetained automatic caption\n"
            : "WEBVTT\n\n00:00.000 --> 00:25.000\nRetained automatic caption\n");
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => selector.Items.Count == 2);
        if (!selected) selector.SelectedIndex = 0;
        await Call<bool>(window, "PlayItem", item, false, 3000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing &&
            !Field<bool>(window, "_hasPendingPosition"));
        var native = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.Equal(selected, selector.SelectedIndex == 1);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var audioSelector = Field<ComboBox>(window, "AudioTrackComboBox");
        await Until(() => audioSelector.Items.OfType<ComboBoxItem>().Any(option => option.Tag is WindowsAudioTrack { Ordinal: 1 }));
        audioSelector.SelectedItem = audioSelector.Items.OfType<ComboBoxItem>().Single(option => option.Tag is WindowsAudioTrack { Ordinal: 1 });
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        Assert.Equal(1, Assert.Single(native.AudioTracks, track => track.Selected).Ordinal);
        var position = native.Time;
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        try
        {
            await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null &&
                Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused &&
                Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle &&
                !Field<bool>(window, "_hasPendingPosition") && Field<object?>(window, "_pendingCompatibilitySubtitles") is null &&
                Field<int?>(window, "_pendingCompatibilityAudioOrdinal") is null &&
                vlc.AspectRatio == "16:9" &&
                vlc.SpuDescription.Count(track => track.Id >= 0) == 1);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException($"Handoff state {Field<PlaybackState>(window, "_playbackState")}, captions: " +
                string.Join(", ", vlc.SpuDescription.Select(track => $"{track.Id}: {track.Name}")));
        }
        Assert.Equal(selected, vlc.Spu >= 0);
        Assert.Equal(vlc.AudioTrackDescription.Where(track => track.Id >= 0).ElementAt(1).Id, vlc.AudioTrack);
        Assert.InRange(vlc.Time, position - 300, position + 500);
        if (!selected)
        {
            await Until(() =>
            {
                Invoke(window, "RefreshTrackSelectors");
                return selector.Items.OfType<ComboBoxItem>().Any(option => option.Tag is int id && id >= 0);
            });
            selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().Single(option => option.Tag is int id && id >= 0);
            await Until(() => vlc.Spu >= 0);
        }
    });

    [Theory]
    [InlineData("exact-font-attributes")]
    [InlineData("ambiguous-languages")]
    [InlineData("ambiguous-exact")]
    [InlineData("unsupported-secondary")]
    [InlineData("fuzzy")]
    public async Task WindowsNative_UnmigratedSidecarDiscoveryKeepsCompatibilityTracks(string kind) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateVideo(window, "sidecar");
        var root = TestRoot(window);
        var name = kind switch
        {
            "ambiguous-languages" => "sidecar.en.srt",
            "ambiguous-exact" => Path.Combine("subs", "sidecar.srt"),
            "fuzzy" => "alternate sidecar.en.srt",
            _ => "sidecar.srt"
        };
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "1\n00:00:00,000 --> 00:00:25,000\n" +
            (kind == "exact-font-attributes" ? "<font color=\"#FF0000\" face=\"Arial\">Font attributes</font>" : "Compatibility caption") + "\n");
        var multiple = kind is "ambiguous-languages" or "ambiguous-exact" or "unsupported-secondary";
        if (multiple) File.WriteAllText(Path.Combine(root, kind == "ambiguous-exact" ? "sidecar.srt" : "sidecar.fr.srt"),
            "1\n00:00:00,000 --> 00:00:25,000\n" + (kind == "unsupported-secondary" ? "{\\an8\\pos(10,20)}French" : "French") + "\n");
        await Call<bool>(window, "AddItem", item, true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing &&
            vlc.SpuDescription.Count(track => track.Id >= 0) == (multiple ? 2 : 1));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData("automatic")]
    [InlineData("manual")]
    [InlineData("off")]
    public async Task WindowsNative_MixedAutomaticAndManualCaptionsKeepTheSelectedTrack(string selected) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateVideo(window, "mixed-captions");
        var automatic = Path.ChangeExtension(item.Source, ".srt");
        var manual = Path.Combine(TestRoot(window), "other-captions.srt");
        File.WriteAllText(automatic, "1\n00:00:00,000 --> 00:00:25,000\nAutomatic caption\n");
        File.WriteAllText(manual, "1\n00:00:00,000 --> 00:00:25,000\nManual caption\n");
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", manual));
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        selector.SelectedIndex = selected switch { "automatic" => 1, "manual" => 2, _ => 0 };
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        // Ratio changes stay native; a later compatibility feature still waits
        // for the selected captions while this handoff is in progress.
        var handoff = Call<bool>(window, "EnsureVlcForFeatureAsync");
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null &&
            Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused &&
            Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle &&
            vlc.SpuDescription.Count(track => track.Id >= 0) == 2);
        // A feature invoked during the handoff must wait for the explicit caption
        // selection, rather than acting on whichever VLC chose automatically.
        await Call(window, "NavigateChapterAsync", true);
        Assert.True(await handoff);
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        var expected = selected switch { "automatic" => tracks[0].Id, "manual" => tracks[1].Id, _ => -1 };
        await Until(() => vlc.Spu == expected);
    });

    [Fact]
    public async Task WindowsNative_WebVttCustomClassFallsBackAndRestoresPreviouslyLoadedTrack() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var plain = Path.Combine(TestRoot(window), "plain.vtt");
        var styled = Path.Combine(TestRoot(window), "styled.vtt");
        File.WriteAllText(plain, "WEBVTT\n\n00:00.000 --> 00:25.000\nPlain &amp; Unicode café\n");
        File.WriteAllText(styled, "WEBVTT\n\n00:00.000 --> 00:25.000\n<c.red.bg_blue.custom>Custom class</c>\n");
        await Call<bool>(window, "AddItem", CreateVideo(window, "webvtt-styled"), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", plain));
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text == "Plain & Unicode café");
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", styled));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => vlc.SpuDescription.Count(track => track.Id >= 0) == 2);
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        await Until(() => vlc.Spu == vlc.SpuDescription.Last(track => track.Id >= 0).Id);
    });

    [Fact]
    public async Task WindowsNative_AudioSidecarRendersWithoutCompatibilityPlayback() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateWave(window, "audio-captions", 20);
        File.WriteAllText(Path.ChangeExtension(item.Source, ".srt"), "1\n00:00:00,000 --> 00:00:20,000\nAudio caption\n");
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing &&
            Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text == "Audio caption");
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
        Assert.Equal(0, Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").PictureAspectRatio);
    });
}
