using System.Text;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.LogicTests;

public sealed class AuditFixTests
{
    private sealed record Captions(string? SelectedPath, int EmbeddedOrdinal);

    [Fact]
    public void RotationCapture_RetainsPausePositionDisabledAudioAndExternalSelection()
    {
        var captions = new Captions("manual.ass", -1);
        var choices = PlaybackSessionChoices<Captions>.Capture(31_234, true, [5, 12], -1, captions);
        Assert.Equal(31_234, choices.Position);
        Assert.True(choices.Paused);
        Assert.Equal(-1, choices.AudioOrdinal);
        Assert.Same(captions, choices.Subtitles);
    }

    [Fact]
    public void RotationCapture_UsesOrdinalRatherThanBackendSpecificTrackId()
    {
        var choices = PlaybackSessionChoices<Captions>.Capture(10_000, false, [5, 12], 12, null);
        Assert.Equal(1, choices.AudioOrdinal);
        Assert.False(choices.Paused);
    }

    [Fact]
    public void VideoWithoutAudio_DoesNotCreateAnAudioRestorationRequest()
    {
        var choices = PlaybackSessionChoices<Captions>.Capture(0, true, [], -1, null);
        Assert.Null(choices.AudioOrdinal);
    }

    [Fact]
    public void UnknownSelectedAudio_IsRejectedInsteadOfSilentlyDisablingIt() =>
        Assert.Throws<InvalidOperationException>(() => PlaybackSessionChoices<Captions>.Capture(0, true, [5], 12, null));

    [Fact]
    public void ReplayChoice_SurvivesResourceTeardownAndRemainsBoundToTheSameItem()
    {
        var item = new object();
        var other = new object();
        var memory = new PlaybackReplayMemory<Captions>();
        var off = new Captions(null, -1);
        memory.Remember(item, off);
        memory.ClearIfDifferent(item); // Reopening the retained item must not discard off.
        Assert.Same(off, memory.For(item));
        Assert.Null(memory.For(other));
        memory.ClearIfDifferent(other);
        Assert.Null(memory.For(item));
    }

    [Fact]
    public void ReplayChoice_CanBeReplacedOrExplicitlyCleared()
    {
        var item = new object();
        var memory = new PlaybackReplayMemory<Captions>();
        memory.Remember(item, new Captions("first.srt", -1));
        var second = new Captions("second.srt", -1);
        memory.Remember(item, second);
        Assert.Same(second, memory.For(item));
        memory.Clear();
        Assert.Null(memory.For(item));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FaultedRestoration_CannotReportReadyAfterPendingFlagsClear(bool subtitleFailure)
    {
        var failed = Task.FromException(new IOException("Original track disappeared"));
        await Assert.ThrowsAsync<IOException>(() => failed);
        var audio = subtitleFailure ? Task.CompletedTask : failed;
        var subtitles = subtitleFailure ? failed : Task.CompletedTask;
        Assert.True(PlaybackRestorationPolicy.Failed(audio, subtitles));
        Assert.False(PlaybackRestorationPolicy.IsReady(PlaybackState.Paused, false, false, false, false, audio, subtitles));
    }

    [Fact]
    public void CanceledOrIncompleteRestoration_CannotReportReady()
    {
        var canceled = Task.FromCanceled(new CancellationToken(true));
        Assert.True(PlaybackRestorationPolicy.Failed(null, canceled));
        Assert.False(PlaybackRestorationPolicy.IsReady(PlaybackState.Playing, false, false, false, false, null, canceled));
        var pending = new TaskCompletionSource();
        Assert.False(PlaybackRestorationPolicy.IsReady(PlaybackState.Playing, false, false, false, false, pending.Task, null));
        pending.SetResult();
        Assert.True(PlaybackRestorationPolicy.IsReady(PlaybackState.Playing, false, false, false, false, pending.Task, null));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void PendingWork_BlocksReadiness(bool pause, bool position, bool audio, bool subtitles) =>
        Assert.False(PlaybackRestorationPolicy.IsReady(PlaybackState.Playing, pause, position, audio, subtitles, null, null));

    [Theory]
    [InlineData((int)PlaybackState.Stopped)]
    [InlineData((int)PlaybackState.Opening)]
    [InlineData((int)PlaybackState.Error)]
    [InlineData((int)PlaybackState.Ended)]
    public void TerminalOrOpeningSession_IsNotReady(int state) =>
        Assert.False(PlaybackRestorationPolicy.IsReady((PlaybackState)state, false, false, false, false, null, null));

    [Fact]
    public void MissingAudioInventory_ExpiresAtTheSharedDeadlineWithoutSleeping()
    {
        var time = new ManualTime();
        var deadline = new PlaybackRestorationDeadline(TimeSpan.FromSeconds(5), time);
        time.Advance(TimeSpan.FromMilliseconds(4999));
        deadline.ThrowIfExpired("Missing audio inventory");
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Throws<TimeoutException>(() => deadline.ThrowIfExpired("Missing audio inventory"));
    }

    [Fact]
    public void AudioDiscoveryAndSelection_ConsumeOneDeadline()
    {
        var time = new ManualTime();
        var deadline = new PlaybackRestorationDeadline(TimeSpan.FromSeconds(5), time);
        time.Advance(TimeSpan.FromSeconds(3)); // Discovery consumed three seconds.
        deadline.ThrowIfExpired("Discovery");
        Assert.Equal(TimeSpan.FromSeconds(2), deadline.Remaining);
        time.Advance(TimeSpan.FromSeconds(2)); // Selection does not get a fresh budget.
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
        Assert.Throws<TimeoutException>(() => deadline.ThrowIfExpired("Selection"));
    }

    [Theory]
    [InlineData(-1L, "0:00")]
    [InlineData(0L, "0:00")]
    [InlineData(59_999L, "0:59")]
    [InlineData(3_600_000L, "1:00:00")]
    [InlineData(86_399_000L, "23:59:59")]
    [InlineData(86_400_000L, "24:00:00")]
    [InlineData(90_000_000L, "25:00:00")]
    [InlineData(long.MaxValue, "2562047788015:12:55")]
    public void DurationFormatting_UsesTotalHoursAndAcceptsExtremeValues(long milliseconds, string expected)
    {
        Assert.Equal(expected, PlaybackTimeFormatter.Format(milliseconds));
        var item = new PlaylistItem { Source = "test.mp4", Title = "Duration", DurationMilliseconds = milliseconds };
        Assert.Equal(milliseconds > 0 ? expected : "—", item.DurationText);
    }

    [Fact]
    public void PlaylistReader_HandlesMixedNewlinesAndFinalUnterminatedLine()
    {
        using var file = new PlaylistFile("a\r\nb\rc\n\nlast");
        Assert.Equal(new[] { "a", "b", "c", "", "last" }, PlaylistTextReader.ReadLines(file.Path, default));
    }

    [Fact]
    public void PlaylistReader_HandlesCrLfAcrossChunks()
    {
        var first = new string('a', 4095);
        using var file = new PlaylistFile(first + "\r\nlast");
        Assert.Equal(new[] { first, "last" }, PlaylistTextReader.ReadLines(file.Path, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlaylistReader_PreservesUtf8AndBomMarkedUtf16(bool utf16)
    {
        using var file = new PlaylistFile("日本語\r\nhttps://example.com/media", utf16 ? Encoding.Unicode : new UTF8Encoding(true));
        Assert.Equal(new[] { "日本語", "https://example.com/media" }, PlaylistTextReader.ReadLines(file.Path, default));
    }

    [Fact]
    public void PlaylistReader_AcceptsTheLineLimitButRejectsOneCharacterBeyondIt()
    {
        using var exact = new PlaylistFile(new string('x', PlaylistTextReader.MaximumLineCharacters));
        Assert.Equal(PlaylistTextReader.MaximumLineCharacters, Assert.Single(PlaylistTextReader.ReadLines(exact.Path, default)).Length);
        using var excess = new PlaylistFile(new string('x', PlaylistTextReader.MaximumLineCharacters + 1));
        Assert.Throws<InvalidDataException>(() => PlaylistTextReader.ReadLines(excess.Path, default).ToArray());
    }

    [Fact]
    public void OversizedComment_IsRejectedWithoutAllocatingItsWholeLine()
    {
        using var file = new PlaylistFile("");
        using (var stream = File.OpenWrite(file.Path)) stream.SetLength(16 * 1024 * 1024);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => PlaylistTextReader.ReadLines(file.Path, default).ToArray());
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 1024 * 1024);
    }

    [Fact]
    public void ManySmallComments_StillConsumeTheInspectedLineBudget()
    {
        using var file = new PlaylistFile(string.Concat(Enumerable.Repeat("#\n", PlaylistTextReader.MaximumLines + 1)));
        Assert.Throws<InvalidDataException>(() => PlaylistTextReader.ReadLines(file.Path, default).Count());
    }

    [Fact]
    public void PlaylistImport_ReportsAnOversizedLineAndRetainsEarlierValidEntries()
    {
        using var file = new PlaylistFile("https://example.com/first\n#" + new string('x', PlaylistTextReader.MaximumLineCharacters));
        var diagnostics = new MediaScanDiagnostics();
        Assert.Equal(new[] { "https://example.com/first" }, MediaSourceService.ExpandFiles([file.Path], diagnostics: diagnostics));
        Assert.True(diagnostics.Faulted);
    }

    [Fact]
    public void PlaylistImport_PropagatesCancellationBetweenBufferedLines()
    {
        using var file = new PlaylistFile("https://example.com/first\nhttps://example.com/second\n");
        using var cancellation = new CancellationTokenSource();
        using var iterator = MediaSourceService.ExpandFiles([file.Path], cancellation.Token).GetEnumerator();
        Assert.True(iterator.MoveNext());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => iterator.MoveNext());
    }

    [Fact]
    public void TestAssembly_DoesNotReferenceAPlayerOrGraphicsFramework()
    {
        var references = typeof(AuditFixTests).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name).ToArray();
        Assert.DoesNotContain(references, name => name is "NoirMediaPlayer" or "LibVLCSharp" or "PresentationCore" or "PresentationFramework");
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private sealed class PlaylistFile : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NoirLogic-" + Guid.NewGuid().ToString("N") + ".m3u8");
        internal PlaylistFile(string contents, Encoding? encoding = null) => File.WriteAllText(Path, contents, encoding ?? new UTF8Encoding(false));
        public void Dispose() => File.Delete(Path);
    }
}
