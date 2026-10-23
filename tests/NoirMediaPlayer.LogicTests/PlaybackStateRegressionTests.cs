using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.LogicTests;

public sealed class PlaybackStateRegressionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(60_000, false)]
    [InlineData(0, true)]
    public void FallbackAtZero_BecomesReadyWithoutAnImpossibleSeek(long length, bool seekable)
    {
        var window = new MainWindow();
        window.PendingPositionForLogic(0, new AuditClock());
        window.TickPositionForLogic(length, seekable);
        Assert.False(window.PositionPendingForLogic);
        Assert.True(window.PositionDeadlineClearedForLogic);
        Assert.Empty(window.RestoredPositions);
        Assert.True(PlaybackRestorationPolicy.IsReady(PlaybackState.Playing, false,
            window.PositionPendingForLogic, false, false, null, null));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(60_000, false)]
    [InlineData(0, true)]
    public void UnavailablePosition_ExpiresWithoutRepeatedlyRestartingDiscovery(long length, bool seekable)
    {
        var clock = new AuditClock();
        var window = new MainWindow();
        window.PendingPositionForLogic(12_000, clock);
        window.TickPositionForLogic(length, seekable);
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        window.TickPositionForLogic(length, seekable);
        Assert.True(window.PositionPendingForLogic);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        window.TickPositionForLogic(length, seekable);
        Assert.False(window.PositionPendingForLogic);
        Assert.Empty(window.RestoredPositions);
        Assert.Contains("cannot restore", window.Notice);
    }

    [Fact]
    public void MetadataArrivingBeforeDeadline_RestoresExactlyOnce()
    {
        var clock = new AuditClock();
        var window = new MainWindow();
        window.PendingPositionForLogic(12_000, clock);
        window.TickPositionForLogic(0, false);
        clock.Advance(TimeSpan.FromSeconds(1));
        window.TickPositionForLogic(60_000, true);
        window.TickPositionForLogic(60_000, true);
        Assert.Equal(new long[] { 12_000 }, window.RestoredPositions);
        Assert.False(window.PositionPendingForLogic);
    }

    [Fact]
    public void ExplicitPosition_PreservesEndClampAndWaitsForAudio()
    {
        var window = new MainWindow();
        window.PendingPositionForLogic(60_000, new AuditClock());
        window.SetPendingAudioForLogic(true);
        window.TickPositionForLogic(60_000, true);
        Assert.True(window.PositionPendingForLogic);
        window.SetPendingAudioForLogic(false);
        window.SetPendingPauseForLogic(true);
        window.TickPositionForLogic(60_000, true);
        Assert.True(window.PositionPendingForLogic);
        window.SetPendingPauseForLogic(false);
        window.TickPositionForLogic(60_000, true);
        Assert.Equal(new long[] { 59_999 }, window.RestoredPositions);
    }

    [Fact]
    public void StaleGeneration_DoesNotClearNewPositionAndReleaseClearsDeadline()
    {
        var clock = new AuditClock();
        var window = new MainWindow();
        window.PendingPositionForLogic(12_000, clock);
        clock.Advance(TimeSpan.FromSeconds(3));
        window.TickPositionForLogic(0, false, generation: -1);
        Assert.True(window.PositionPendingForLogic);
        window.ClearPositionForLogic();
        Assert.False(window.PositionPendingForLogic);
        Assert.True(window.PositionDeadlineClearedForLogic);
        window.PendingPositionForLogic(24_000, clock);
        window.TickPositionForLogic(0, false);
        Assert.True(window.PositionPendingForLogic);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(0)]
    public async Task NativeStartup_UsesLatestRotationEvenWhileCreationIsQueued(int rotation)
    {
        var window = new MainWindow();
        window.Queue.BlockNext = true;
        var startup = window.StartNativeForLogic();
        Assert.Null(window.NativeForLogic);
        window.SetRotationForLogic(rotation);
        window.Queue.CompleteQueued();
        await startup;
        Assert.Equal(rotation, window.NativeForLogic!.RotationAtOpen);
        Assert.True(window.StartupFallbackClearedForLogic);
    }

    [Fact]
    public async Task SupersededNativeStartup_DoesNotCreateAnEngine()
    {
        var window = new MainWindow();
        window.Queue.BlockNext = true;
        var startup = window.StartNativeForLogic();
        window.SupersedeForLogic();
        window.Queue.CompleteQueued();
        await startup;
        Assert.Null(window.NativeForLogic);
    }
}
