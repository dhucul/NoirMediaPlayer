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
    private const string LanguageGlyphs = "骨直令葛辻";

    private static string LanguageCaptionFile() => "WEBVTT\n\n" + string.Join("\n\n", new[]
    {
        "00:00.000 --> 00:02.000\n<lang ja-JP>" + LanguageGlyphs + "</lang>",
        "00:02.000 --> 00:04.000\n<lang zh-CN>" + LanguageGlyphs + "</lang>",
        "00:04.000 --> 00:06.000\n" + LanguageGlyphs,
        "00:06.000 --> 00:08.000\n<lang.yellow.bg_blue ja-JP><b>" + LanguageGlyphs + "</b></lang>",
        "00:08.000 --> 00:25.000\n<v Narrator><lang ja-JP>日本語 café 😀</lang>\n<lang ar-SA><u>العربية</u></lang>"
    }) + "\n";

    // Build the expected ranges independently of the subtitle parser.
    private static SubtitleCue LanguageReference(long time)
    {
        if (time < 2000) return new(0, 2000, LanguageGlyphs,
            [new(0, LanguageGlyphs.Length, SubtitleStyle.None, Language: "ja-JP")]);
        if (time < 4000) return new(2000, 4000, LanguageGlyphs,
            [new(0, LanguageGlyphs.Length, SubtitleStyle.None, Language: "zh-Hans-CN")]);
        if (time < 6000) return new(4000, 6000, LanguageGlyphs);
        if (time < 8000) return new(6000, 8000, LanguageGlyphs,
            [new(0, LanguageGlyphs.Length, SubtitleStyle.Bold, 0xFFFF00, 0x0000FF, "ja-JP")]);
        const string first = "日本語 café 😀";
        const string second = "العربية";
        return new(8000, 25000, first + "\n" + second,
            [new(0, first.Length, SubtitleStyle.None, Language: "ja-JP"),
             new(first.Length + 1, second.Length, SubtitleStyle.Underline, Language: "ar-SA")]);
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
    public async Task NativeLanguage_TimedLanguagesKeepVideoAudioSnapshotsAndTransport(bool audio, bool network, bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "language" + (audio ? ".m4a" : ".mp4"));
        if (audio) Mp4Fixture.WriteAudio(source, Mp4Fixture.Nero(0, 4000, 8000));
        else Mp4Fixture.Write(source, Mp4Fixture.Nero(0, 4000, 8000));
        var captions = Path.ChangeExtension(source, audio ? ".webvtt" : ".vtt");
        File.WriteAllText(captions, LanguageCaptionFile());
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(source), audio ? ".m4a" : ".mp4", audio ? "audio/mp4" : "video/mp4") : null;
        var item = network ? new PlaylistItem { Source = server!.Url + "?signature=KeepCase", Title = "Language HTTP", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.NotNull(player);
        if (network) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", captions));
        await Until(() => Field<ComboBox>(window, "SubtitleTrackComboBox").Items.Count == 2);
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused && Field<long>(window, "_lastPlaybackLength") > 15000);
        player.SelectAudioTrack(-1);
        if (!audio) Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        foreach (var time in new[] { 1000L, 3000L, 5000L, 7000L, 9000L, 3000L })
        {
            var expected = LanguageReference(time);
            Invoke(window, "SeekTo", time);
            await Until(() => Math.Abs(player.Time - time) < 150 && presenter.Cue?.Start == expected.Start);
            Assert.Equal(expected.Text, presenter.Text);
            Assert.Equal(expected.Styles, presenter.Cue!.Styles);
            if (!audio)
            {
                var raw = Path.Combine(TestRoot(window), "language-reference-" + time + ".png");
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
        Assert.Equal(LanguageCaptionFile(), File.ReadAllText(captions));
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
    public async Task NativeLanguage_RestartAndHandoffRetainLanguageFilesSelectionAndTransport(bool off, bool network, bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "language-handoff.wmv");
        AsfFixture.Write(source, true, 0, 4000, 8000);
        var first = Path.ChangeExtension(source, ".webvtt");
        var second = Path.Combine(TestRoot(window), "manual.vtt");
        File.WriteAllText(first, LanguageCaptionFile());
        const string alternate = "WEBVTT\n\n00:00.000 --> 00:25.000\n<lang.red.bg_blue ja-JP><u>日本語</u></lang>\n";
        File.WriteAllText(second, alternate);
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(source), ".wmv", "video/x-ms-wmv") : null;
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "Language handoff HTTP", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
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
        Assert.Equal(LanguageCaptionFile(), File.ReadAllText(first));
        Assert.Equal(alternate, File.ReadAllText(second));
    }, hardwareDecoding: hardware);

    [Theory]
    [InlineData("unknown")]
    [InlineData("private")]
    [InlineData("custom")]
    [InlineData("css")]
    public async Task NativeLanguage_UnsupportedLanguagesAndStylingLoadOriginalCompatibilityTrack(string kind) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "language-unready.wmv");
        AsfFixture.Write(source, true, 0, 4000, 8000);
        var first = Path.ChangeExtension(source, ".vtt");
        File.WriteAllText(first, LanguageCaptionFile());
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(source), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.NotNull(player);
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused);
        player.SelectAudioTrack(-1);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        var unsupported = Path.Combine(TestRoot(window), "unready.webvtt");
        var content = "WEBVTT\n\n" + (kind == "css" ? "STYLE\n::cue(:lang(ja)) { color: red; }\n\n" : "") +
            "00:00.000 --> 00:25.000\n" + (kind switch
            {
                "unknown" => "<lang qzz>Unknown language</lang>\n",
                "private" => "<lang x-private>Private language</lang>\n",
                "custom" => "<lang.custom ja>日本語</lang>\n",
                _ => "<lang ja>日本語</lang>\n"
            });
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
        Assert.Equal(LanguageCaptionFile(), File.ReadAllText(first));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeLanguage_SourceAndManualRotationKeepSnapshotLanguagesUpright(bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "language-oriented.mp4");
        Mp4Fixture.WriteOriented(source, 90, 2, 1);
        File.WriteAllText(Path.ChangeExtension(source, ".vtt"), LanguageCaptionFile());
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(source), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.NotNull(player);
        await Call(window, "TogglePlayPause");
        await Until(() => player.IsPaused && Field<long>(window, "_lastPlaybackLength") > 15000);
        Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 4;
        Invoke(window, "SeekTo", 7000L);
        await Until(() => Math.Abs(player.Time - 7000) < 150 && Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue?.Start == 6000);
        var raw = Path.Combine(TestRoot(window), "language-oriented-reference.png");
        await Task.Run(() => player.SaveSnapshot(raw));
        var frame = ReadCaptionBitmap(raw);
        var snapshot = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
        Assert.Equal(CaptionPixels(SubtitlePresenter.Composite(frame, LanguageReference(7000))), CaptionPixels(snapshot));
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
    }, hardwareDecoding: hardware);
}
