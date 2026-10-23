using System.Diagnostics;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.LogicTests;

[CollectionDefinition("Snapshot processes", DisableParallelization = true)]
public sealed class SnapshotProcessCollection;

[Collection("Snapshot processes")]
public sealed class SnapshotWorkerTests
{
    [Fact]
    public async Task SuccessfulCapture_PublishesCapturedStateAfterChildExit()
    {
        using var files = new SnapshotFiles();
        var host = new SnapshotTestProcess(files);
        var worker = new SnapshotWorker(startInfo: host.StartInfo);
        var cue = new SubtitleCue(0, 4000, "日本語\ncaption",
            [new(0, 3, SubtitleStyle.Bold, 0xFF00FF00, 0xFF000000, "ja-JP")], SubtitleAlignment.TopRight);
        var request = new SourceSnapshotRequest("https://example.test/片.mp4?token=private", 1234,
            "video/mp4", Rotation: 270, EmbeddedCue: cue);
        Assert.True(await worker.TryCaptureAsync(request, files.Output).WaitAsync(TimeSpan.FromSeconds(5)));
        using var image = File.OpenRead(files.Output);
        var result = SnapshotProtocol.Read(image);
        Assert.Equal(request.Source, result.Source);
        Assert.Equal(1234, result.Time);
        Assert.Equal("video/mp4", result.ContentType);
        Assert.Equal(270, result.Rotation);
        Assert.Equal(cue.Text, result.Caption!.Text);
        Assert.Equal(cue.Styles, result.Caption.Styles);
        Assert.Equal(cue.Alignment, result.Caption.Alignment);
        Assert.NotEqual(files.Output, result.Destination);
        Assert.Single(Directory.GetFiles(files.Folder));
        Assert.False(worker.IsBusy);
        host.AssertExited();
    }

    [Fact]
    public async Task HungChild_TimeoutKillsAndReaps_AllowsRetryAndTransportCommands()
    {
        using var files = new SnapshotFiles();
        var host = new SnapshotTestProcess(files) { Mode = "hang" };
        var clock = new AuditClock();
        var worker = new SnapshotWorker(TimeSpan.FromSeconds(10), clock, host.StartInfo);
        var capture = worker.TryCaptureAsync(new("fake", 0), files.Output);
        await host.WaitForStart();
        Assert.True(worker.IsBusy);
        Assert.False(await worker.TryCaptureAsync(new("fake", 0), files.Output));
        Assert.Equal(1, host.Starts);
        var transport = new PlaybackOperationQueue();
        var commands = new List<string>();
        await transport.Enqueue(() => commands.Add("pause")).WaitAsync(TimeSpan.FromSeconds(2));
        await transport.Enqueue(() => commands.Add("stop")).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "pause", "stop" }, commands);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<TimeoutException>(() => capture.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.True(capture.IsCompleted); // Not the test's own wait timeout.
        Assert.False(worker.IsBusy);
        host.AssertExited();
        Assert.Empty(Directory.GetFiles(files.Folder));
        host.Mode = "success";
        Assert.True(await worker.TryCaptureAsync(new("next", 42), files.Output).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, host.Starts);
        host.AssertExited();
    }

    [Fact]
    public async Task ChildThatNeverReadsRequest_IsKilledEvenWithBlockedPipeWrite()
    {
        using var files = new SnapshotFiles();
        var host = new SnapshotTestProcess(files) { Mode = "no-read" };
        var clock = new AuditClock();
        var worker = new SnapshotWorker(TimeSpan.FromSeconds(10), clock, host.StartInfo);
        // JSON escaping exceeds the pipe buffer, but stays below the protocol cap.
        var capture = worker.TryCaptureAsync(new(new string('中', 30000), 0), files.Output);
        await host.WaitForStart();
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<TimeoutException>(() => capture.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.True(capture.IsCompleted);
        Assert.False(worker.IsBusy);
        host.AssertExited();
        Assert.Empty(Directory.GetFiles(files.Folder));
    }

    [Fact]
    public async Task Cancellation_KillsChildAndDiscardsPartialOutput()
    {
        using var files = new SnapshotFiles();
        using var cancellation = new CancellationTokenSource();
        var host = new SnapshotTestProcess(files) { Mode = "hang" };
        var worker = new SnapshotWorker(startInfo: host.StartInfo);
        var capture = worker.TryCaptureAsync(new("fake", 0), files.Output, cancellation.Token);
        await host.WaitForStart();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.False(worker.IsBusy);
        host.AssertExited();
        Assert.Empty(Directory.GetFiles(files.Folder));
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("missing")]
    [InlineData("success")]
    public async Task FailureOrCollision_NeverOverwritesDestinationOrLeaksStaging(string mode)
    {
        using var files = new SnapshotFiles();
        File.WriteAllText(files.Output, "existing");
        var host = new SnapshotTestProcess(files) { Mode = mode };
        var worker = new SnapshotWorker(startInfo: host.StartInfo);
        await Assert.ThrowsAnyAsync<IOException>(() => worker.TryCaptureAsync(new("fake", 0), files.Output)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("existing", File.ReadAllText(files.Output));
        Assert.Single(Directory.GetFiles(files.Folder));
        Assert.False(worker.IsBusy);
        host.AssertExited();
    }

    [Fact]
    public async Task PreCanceledRequest_DoesNotLaunchAChild()
    {
        using var files = new SnapshotFiles();
        var host = new SnapshotTestProcess(files);
        var worker = new SnapshotWorker(startInfo: host.StartInfo);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.TryCaptureAsync(new("fake", 0),
            files.Output, new CancellationToken(true)));
        Assert.Equal(0, host.Starts);
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public async Task LaunchFailure_ReleasesSlotForRetry()
    {
        using var files = new SnapshotFiles();
        var host = new SnapshotTestProcess(files);
        var unavailable = true;
        var worker = new SnapshotWorker(startInfo: () => unavailable
            ? new ProcessStartInfo(Path.Combine(files.Folder, "missing.exe")) : host.StartInfo());
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => worker.TryCaptureAsync(new("fake", 0), files.Output));
        Assert.False(worker.IsBusy);
        unavailable = false;
        Assert.True(await worker.TryCaptureAsync(new("fake", 0), files.Output).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ParentPipeClosed_ChildWatchdogTerminatesBlockedDecoder()
    {
        using var files = new SnapshotFiles();
        var host = new SnapshotTestProcess(files) { Mode = "hang" };
        var start = host.StartInfo();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        start.RedirectStandardInput = true;
        using var child = Process.Start(start)!;
        try
        {
            await child.StandardInput.BaseStream.WriteAsync(SnapshotProtocol.Encode(new("fake", 0), files.Output));
            await child.StandardInput.BaseStream.FlushAsync();
            await host.WaitForStart();
            child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(3, child.ExitCode);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        }
    }
}

internal sealed class SnapshotTestProcess(SnapshotFiles files)
{
    internal string Mode = "success";
    internal int Starts;
    private string Marker => Path.Combine(files.Control, "started");
    internal ProcessStartInfo StartInfo()
    {
        // Tests are sequential and paced; each child only writes a tiny text file.
        Thread.Sleep(50);
        File.Delete(Marker);
        Starts++;
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SnapshotTestHost", "NoirMediaPlayer.SnapshotTestHost.exe"));
        start.ArgumentList.Add(Mode);
        start.ArgumentList.Add(Marker);
        return start;
    }
    internal async Task WaitForStart()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (true)
        {
            if (File.Exists(Marker) && int.TryParse(File.ReadAllText(Marker), out _)) return;
            await Task.Delay(20, bound.Token);
        }
    }
    internal void AssertExited()
    {
        var pid = int.Parse(File.ReadAllText(Marker));
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (process) Assert.True(process.HasExited, $"Child {pid} is still running.");
    }
}

internal sealed class SnapshotFiles : IDisposable
{
    internal string Folder { get; } = Path.Combine(Path.GetTempPath(), "NoirSnapshotLogic-" + Guid.NewGuid().ToString("N"));
    internal string Control => Path.Combine(Folder, "control");
    internal string Output => Path.Combine(Folder, "capture.png");
    internal SnapshotFiles() => Directory.CreateDirectory(Control);
    public void Dispose() => Directory.Delete(Folder, recursive: true);
}
