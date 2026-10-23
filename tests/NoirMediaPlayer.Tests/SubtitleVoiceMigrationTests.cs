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
    private static string VoiceCaptionFile(bool annotated = true)
    {
        var cues = annotated ? new[]
        {
            "<v Alice>Same</v>",
            "<v Bob><i.red>Same</i></v>",
            "<v.yellow.bg_blue Alice><b>Same</b></v>",
            "<v 李 😀><u.cyan>Same</u></v>",
            "<v Alice><b.yellow.bg_blue>日本語 café 😀</b></v>\n<v Bob><u>Second speaker</u></v>"
        } : new[]
        {
            "Same",
            "<c.red><i>Same</i></c>",
            "<c.yellow.bg_blue><b>Same</b></c>",
            "<c.cyan><u>Same</u></c>",
            "<c.yellow.bg_blue><b>日本語 café 😀</b></c>\n<u>Second speaker</u>"
        };
        return "WEBVTT\n\n" + string.Join("\n\n", cues.Select((cue, index) =>
            $"00:{index * 2:00}.000 --> 00:{(index == 4 ? 25 : index * 2 + 2):00}.000\n{cue}")) + "\n";
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
    public async Task NativeVoice_PlaybackKeepsTimedGlyphsCapturesAndControls(bool audio, bool network, bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "voice" + (audio ? ".m4a" : ".mp4"));
        if (audio) Mp4Fixture.WriteAudio(source, Mp4Fixture.Nero(0, 4000, 8000));
        else Mp4Fixture.Write(source, Mp4Fixture.Nero(0, 4000, 8000));
        var captions = Path.ChangeExtension(source, audio ? ".webvtt" : ".vtt");
        File.WriteAllText(captions, VoiceCaptionFile());
        var reference = PlainSubtitleTrack.Parse("reference.vtt", VoiceCaptionFile(annotated: false))!;
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(source), audio ? ".m4a" : ".mp4", audio ? "audio/mp4" : "video/mp4") : null;
        var item = network ? new PlaylistItem { Source = server!.Url + "?signature=KeepCase", Title = "Voice captions HTTP", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.NotNull(player);
        if (network) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", captions));
        await Until(() => Field<ComboBox>(window, "SubtitleTrackComboBox").Items.Count == 2);
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 15000);
        player.SelectAudioTrack(-1);
        if (!audio) Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        foreach (var time in new[] { 1000L, 3000L, 5000L, 7000L, 9000L, 3000L })
        {
            Invoke(window, "SeekTo", time);
            await Until(() => Math.Abs(player.Time - time) < 150 && presenter.Cue?.Start == reference.CueAt(time)!.Start);
            var expected = reference.CueAt(time)!;
            Assert.Equal(expected.Text, presenter.Text);
            Assert.Equal(expected.Styles, presenter.Cue!.Styles);
            if (!audio)
            {
                var raw = Path.Combine(TestRoot(window), "reference-" + time + ".png");
                await Task.Run(() => player.SaveSnapshot(raw));
                var frame = ReadCaptionBitmap(raw);
                var snapshot = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
                Assert.Equal(CaptionPixels(SubtitlePresenter.Composite(frame, expected)), CaptionPixels(snapshot));
            }
            else AssertNativeAudioCaptionPane(window);
        }
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        selector.SelectedIndex = 0;
        Assert.Null(presenter.Cue);
        selector.SelectedIndex = 1;
        await Until(() => presenter.Cue is not null);
        Invoke(window, "SeekTo", 1000L);
        await Until(() => Math.Abs(player.Time - 1000) < 150);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => presenter.Cue?.Start == 8000);
        Assert.InRange(player.Time, 7999, 8001);
        Invoke(window, "SelectSpeed", 0.75f);
        await Call(window, "TogglePlayPause");
        await Until(() => !player.IsPaused);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        Assert.Equal(0.75f, Field<PlayerSettings>(window, "_settings").PlaybackRate);
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
        Assert.Equal(VoiceCaptionFile(), File.ReadAllText(captions));
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
    public async Task NativeVoice_RestartAndCompatibilityKeepFilesSelectionAndTransport(bool off, bool network, bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "voice-handoff.wmv");
        AsfFixture.Write(source, true, 0, 4000, 8000);
        var first = Path.ChangeExtension(source, ".webvtt");
        var second = Path.Combine(TestRoot(window), "manual.vtt");
        File.WriteAllText(first, VoiceCaptionFile());
        const string alternate = "WEBVTT\n\n00:00.000 --> 00:25.000\n<v.red.bg_blue Narrator><u>Retained voice</u>\n";
        File.WriteAllText(second, alternate);
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(source), ".wmv", "video/x-ms-wmv") : null;
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "Voice handoff HTTP", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
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
        Assert.Equal(VoiceCaptionFile(), File.ReadAllText(first));
        Assert.Equal(alternate, File.ReadAllText(second));
    }, hardwareDecoding: hardware);

    [Theory]
    [InlineData("custom")]
    [InlineData("language")]
    [InlineData("css")]
    public async Task NativeVoice_UnsupportedMeaningStillLoadsItsOriginalCompatibilityTrack(string kind) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "voice-unready.wmv");
        AsfFixture.Write(source, true, 0, 4000, 8000);
        var first = Path.ChangeExtension(source, ".vtt");
        File.WriteAllText(first, VoiceCaptionFile());
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(source), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused);
        player.SelectAudioTrack(-1);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        var unsupported = Path.Combine(TestRoot(window), "unready.webvtt");
        var content = kind switch
        {
            "custom" => "WEBVTT\n\n00:00.000 --> 00:25.000\n<v.custom Alice>Custom class\n",
            "language" => "WEBVTT\n\n00:00.000 --> 00:25.000\n<lang x-private><v Alice>Language</v></lang>\n",
            _ => "WEBVTT\n\nSTYLE\n::cue(v[voice=\"Alice\"]) { color: red; }\n\n00:00.000 --> 00:25.000\n<v Alice>CSS voice\n"
        };
        File.WriteAllText(unsupported, content);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", unsupported));
        var vlc = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.Equal(tracks[^1].Id, vlc.Spu);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(8000, Field<long>(window, "_compatibilityChapterTime"));
        Assert.Equal(content, File.ReadAllText(unsupported));
        Assert.Equal(VoiceCaptionFile(), File.ReadAllText(first));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });
}
