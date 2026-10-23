using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.LogicTests;

[Collection("Snapshot processes")]
public sealed class SnapshotOrchestrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrClose_TerminatesDecoderAndDiscardsPartialImage(bool closing)
    {
        using var files = new SnapshotFiles();
        var host = new SnapshotTestProcess(files) { Mode = "hang" };
        var worker = new SnapshotWorker(startInfo: host.StartInfo);
        var window = new MainWindow(worker);
        window.UseNativeBackendForLogic();
        var player = window.NativeForLogic!;
        var capture = window.SnapshotForLogic(files.Output);
        await host.WaitForStart();
        Assert.True(window.Queue.IsIdle);
        await window.StopSnapshotSessionForLogic(closing).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(player.Disposed);
        Assert.False(await capture.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.Equal(0, window.Fallbacks);
        Assert.False(worker.IsBusy);
        host.AssertExited();
        Assert.Empty(Directory.GetFiles(files.Folder));
    }

    [Fact]
    public async Task HungSnapshot_TimesOutWithoutFallback_ThenRetriesWithoutRestart()
    {
        using var files = new SnapshotFiles();
        var host = new SnapshotTestProcess(files) { Mode = "hang" };
        var clock = new AuditClock();
        var worker = new SnapshotWorker(TimeSpan.FromSeconds(10), clock, host.StartInfo);
        var window = new MainWindow(worker);
        window.UseNativeBackendForLogic();
        var player = window.NativeForLogic!;
        var capture = window.SnapshotForLogic(files.Output);
        await host.WaitForStart();
        Assert.False(await window.SnapshotForLogic(files.Output));
        Assert.Equal(1, host.Starts);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(await capture.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.Contains("timed out", window.Notice);
        Assert.False(worker.IsBusy);
        host.AssertExited();
        Assert.Empty(Directory.GetFiles(files.Folder));
        host.Mode = "success";
        Assert.True(await window.SnapshotForLogic(files.Output).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, host.Starts);
        Assert.Equal(0, window.Fallbacks);
        Assert.Same(player, window.NativeForLogic);
        Assert.True(window.Queue.IsIdle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedCaptureFailureOrUnsupportedRequest_RetainsCompatibilityFallback(bool unsupported)
    {
        using var files = new SnapshotFiles();
        var host = new SnapshotTestProcess(files) { Mode = "fail" };
        var window = new MainWindow(new SnapshotWorker(startInfo: host.StartInfo));
        window.UseNativeBackendForLogic();
        window.NativeForLogic!.UnsupportedSnapshot = unsupported;
        Assert.True(await window.SnapshotForLogic(files.Output).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, window.Fallbacks);
        Assert.Equal(1, window.Backend.SnapshotCalls);
        Assert.Equal("fake compatibility image", File.ReadAllText(files.Output));
        Assert.Single(Directory.GetFiles(files.Folder));
    }
}
