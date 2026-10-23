using System.Diagnostics;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.LogicTests;

public sealed class SubtitleRestorationTests
{
    [Theory]
    [InlineData(".srt")]
    [InlineData(".ass")]
    public async Task SuccessfulManualLoad_RetainsFileAndSelectionAcrossSessionCapture(string extension)
    {
        using var file = new SubtitleFile(extension);
        var window = new MainWindow();
        window.Backend.OnAdd = () => window.Backend.SpuDescription = [new(2)];
        Assert.True(await window.LoadSubtitleForLogicAsync(file.Path));
        Assert.False(window.SubtitleLoadPending);
        Assert.False(window.Backend.AttachmentRequestedSelection);
        Assert.Equal(2, window.Backend.Spu);
        var choice = window.CaptureSubtitleForLogic();
        Assert.Equal(file.Path, choice.Path);
        Assert.Equal(new[] { file.Path }, choice.Files);

        var reopened = new MainWindow();
        reopened.Backend.OnAdd = () => reopened.Backend.SpuDescription = [new(7)];
        await reopened.RestoreCapturedSubtitlesForLogicAsync(window);
        Assert.False(reopened.SubtitleRestorePending);
        Assert.True(reopened.MetadataRefreshRequested);
        Assert.Equal(7, reopened.Backend.Spu);
        Assert.Equal(file.Path, reopened.CaptureSubtitleForLogic().Path);
        Assert.Null(reopened.Failure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SimultaneousAutomaticAndManualDiscovery_RejectsAmbiguityAndRetiresUncertainSession(int selectedId)
    {
        using var file = new SubtitleFile();
        var window = new MainWindow();
        window.Backend.OnAdd = () =>
        {
            window.Backend.SpuDescription = [new(1), new(2)];
            window.Backend.Spu = selectedId;
        };
        Assert.False(await window.LoadSubtitleForLogicAsync(file.Path));
        Assert.IsType<InvalidOperationException>(window.Failure);
        Assert.Equal("subtitle loading", window.FailureFeature);
        Assert.False(window.SubtitleLoadPending);
        Assert.True(window.TeardownCompleted);
        Assert.Equal(0, window.Backend.SelectionCalls);
        Assert.Empty(window.CaptureSubtitleForLogic().Files);
    }

    [Fact]
    public async Task RejectedManualSelection_DoesNotReportACompletedLoad()
    {
        using var file = new SubtitleFile();
        var window = new MainWindow();
        window.Backend.OnAdd = () => window.Backend.SpuDescription = [new(2)];
        window.Backend.AcceptSelection = false;
        Assert.False(await window.LoadSubtitleForLogicAsync(file.Path));
        Assert.IsType<InvalidOperationException>(window.Failure);
        Assert.False(window.SubtitleLoadPending);
        Assert.True(window.TeardownCompleted);
    }

    [Fact]
    public async Task PendingLoad_BlocksCaptureAndConcurrentLoads_WithoutClearingANewerRequest()
    {
        using var file = new SubtitleFile();
        var window = new MainWindow();
        window.Queue.BlockNext = true;
        var load = window.LoadSubtitleForLogicAsync(file.Path);
        await WaitForBlockedAsync(window);
        Assert.True(window.SubtitleLoadPending);
        Assert.Throws<InvalidOperationException>(() => window.CaptureSubtitleForLogic());
        Assert.False(await window.LoadSubtitleForLogicAsync(file.Path));
        window.SupersedeForLogic(pendingLoad: true);
        window.Queue.CompleteQueued();
        Assert.False(await load);
        Assert.True(window.SubtitleLoadPending);
        Assert.Equal(0, window.Backend.AddCalls);
        Assert.Null(window.Failure);
    }

    [Theory]
    [InlineData("attachment")]
    [InlineData("selection")]
    [InlineData("manual-load")]
    public async Task StalledSubtitleCommand_FailsWithinDeadline_AndKeepsTeardownQueued(string stage)
    {
        using var file = new SubtitleFile();
        var window = new MainWindow();
        window.Backend.Spu = 0;
        window.Queue.BlockNext = true;
        var clock = Stopwatch.StartNew();
        var operation = stage == "manual-load" ? window.LoadSubtitleForLogicAsync(file.Path) :
            window.RestoreSubtitlesForLogicAsync(stage == "attachment" ? [file.Path] : []);
        await WaitForBlockedAsync(window);
        var blocked = window.Queue.FirstBlocked;
        try
        {
            if (stage == "manual-load") Assert.False(await ((Task<bool>)operation).WaitAsync(TimeSpan.FromSeconds(8)));
            else await Assert.ThrowsAsync<TimeoutException>(() => operation.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8));
            Assert.IsType<TimeoutException>(window.Failure);
            Assert.False(window.SubtitleRestorePending);
            Assert.False(window.SubtitleLoadPending);
            Assert.False(window.TeardownCompleted);
            Assert.True(window.Queue.WaitingCount >= 2);
            Assert.Contains(blocked!, window.ObservedOperations);
            Assert.Equal(0, window.Backend.AddCalls);
            Assert.Equal(0, window.Backend.SelectionCalls);
        }
        finally
        {
            // Complete the old queued task with a late fault. Its command must be
            // suppressed after retirement, and queued teardown must still run.
            window.Queue.CompleteQueued(new InvalidOperationException("Late command failure"));
            try { await operation; } catch (TimeoutException) { }
            await Task.WhenAll(window.ObserverTasks);
        }
        Assert.True(window.TeardownCompleted);
        Assert.Equal(0, window.Backend.AddCalls);
        Assert.Equal(0, window.Backend.SelectionCalls);
    }

    [Fact]
    public async Task SupersededRestore_SkipsOldCommandsAndPreservesNewPendingSession()
    {
        var window = new MainWindow();
        window.Queue.BlockNext = true;
        var restoration = window.RestoreSubtitlesForLogicAsync();
        Assert.NotNull(window.Queue.FirstBlocked);
        window.SupersedeForLogic(pendingRestore: true);
        window.Queue.CompleteQueued();
        await restoration;
        Assert.True(window.SubtitleRestorePending);
        Assert.Equal(0, window.Backend.SelectionCalls);
        Assert.Null(window.Failure);
    }

    [Fact]
    public async Task DeadlineExpiresWhileQueued_PreventsLateExecutionEvenBeforeRetirement()
    {
        var window = new MainWindow();
        var time = new ManualTime();
        var deadline = new PlaybackRestorationDeadline(TimeSpan.FromSeconds(5), time);
        window.Queue.BlockNext = true;
        var executions = 0;
        var operation = window.QueueSubtitleCommandForLogicAsync(deadline, () => executions++);
        time.Advance(TimeSpan.FromSeconds(5));
        window.Queue.CompleteQueued();
        await Assert.ThrowsAsync<TimeoutException>(() => operation);
        await Task.WhenAll(window.ObserverTasks);
        Assert.Equal(0, executions);
    }

    private static async Task WaitForBlockedAsync(MainWindow window)
    {
        var clock = Stopwatch.StartNew();
        while (window.Queue.FirstBlocked is null && clock.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(5);
        Assert.NotNull(window.Queue.FirstBlocked);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private sealed class SubtitleFile : IDisposable
    {
        internal string Path { get; }
        internal SubtitleFile(string extension = ".srt")
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NoirLogicCaption-" + Guid.NewGuid().ToString("N") + extension);
            File.WriteAllText(Path, extension == ".srt" ? "1\n00:00:00,000 --> 00:00:25,000\nRetain this caption\n" : "[Script Info]\nTitle: Compatibility-only caption\n");
        }
        public void Dispose() => File.Delete(Path);
    }
}
