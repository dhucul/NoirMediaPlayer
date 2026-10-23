using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

// Playback integration coverage. Compile only during the GPU-restricted audit;
// execute only when playback testing is explicitly safe/authorized again.
public sealed partial class LogicAuditRegressionTests
{
    [Fact]
    public async Task AuditFix_CompatibilityRotationKeepsPauseAndDisabledAudio() => await WithWindow(async window =>
    {
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
        Field<DispatcherTimer>(window, "_uiTimer").Start();
        var item = CreateWave(window, "rotate-retained-state", 30);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var subtitlePath = Path.Combine(TestRoot(window), "manual-rotation.srt");
        File.WriteAllText(subtitlePath, "1\n00:00:00,000 --> 00:00:25,000\nKeep this manual track\n");
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", subtitlePath));
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var player = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 20_000);
        Invoke(window, "SeekTo", 12_000L);
        await Until(() => Math.Abs(player.Time - 12_000) < 150);
        player.SetAudioTrack(-1);
        await Until(() => player.AudioTrack == -1);
        var generation = Field<long>(window, "_playbackGeneration");
        Field<DispatcherTimer>(window, "_uiTimer").Start();
        Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        await Until(() => Field<long>(window, "_playbackGeneration") != generation &&
            Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused &&
            Field<int?>(window, "_pendingCompatibilityAudioOrdinal") is null && !Field<bool>(window, "_hasPendingPosition"));
        Assert.Equal(-1, player.AudioTrack);
        Assert.True(player.Spu >= 0);
        Assert.Equal(subtitlePath, Field<ConcurrentDictionary<int, string>>(window, "_compatibilityExternalSubtitlePaths")[player.Spu]);
        Assert.InRange(player.Time, 11_800, 12_200);
        Assert.Equal(90, Field<int>(window, "_rotation"));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Fact]
    public async Task AuditFix_NaturalEndReplayRetainsSubtitlesOff() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateWave(window, "natural-end-choice", 3);
        File.WriteAllText(Path.ChangeExtension(item.Source, ".srt"), "1\n00:00:00,000 --> 00:00:25,000\nRetain this choice\n");
        await Call<bool>(window, "AddItem", item, true);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && selector.Items.Count == 2);
        selector.SelectedIndex = 0;
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Ended);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && selector.Items.Count == 2);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(0, selector.SelectedIndex);
        Assert.Null(Field<PlainSubtitleTrack?>(window, "_selectedPlainSubtitle"));
    });

    [Fact]
    public async Task AuditFix_MissingSidecarFailsHandoffInsteadOfReportingReady() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateWave(window, "handoff-missing-sidecar", 30);
        var path = Path.Combine(TestRoot(window), "manual-unrelated.srt");
        File.WriteAllText(path, "1\n00:00:00,000 --> 00:00:25,000\nRetain this caption\n");
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", path));
        File.Delete(path);
        Assert.False(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        Assert.Equal(PlaybackState.Error, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal("RESTORE FAILED", Field<TextBlock>(window, "EngineStatusText").Text);
        Assert.False(Field<bool>(window, "_hasPendingPosition"));
    });

    [Fact]
    public async Task AuditFix_MissingAudioOrdinalReachesATerminalError() => await WithWindow(async window =>
    {
        var item = CreateWave(window, "missing-audio-ordinal", 30);
        await Call<bool>(window, "AddItem", item, false);
        Field<DispatcherTimer>(window, "_uiTimer").Start();
        var clock = Stopwatch.StartNew();
        await Call<bool>(window, "PlayItemCore", item, false, 3000L, true, true, 1, null!);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Error, timeoutSeconds: 8);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8));
        Assert.Null(Field<int?>(window, "_pendingCompatibilityAudioOrdinal"));
        Assert.False(Field<bool>(window, "_hasPendingPosition"));
        Assert.Equal("RESTORE FAILED", Field<TextBlock>(window, "EngineStatusText").Text);
    });
}
