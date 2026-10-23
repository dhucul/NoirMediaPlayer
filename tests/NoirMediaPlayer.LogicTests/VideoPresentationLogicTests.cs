using Xunit;

namespace NoirMediaPlayer.LogicTests;

// Command/lifecycle regressions only. The production partial uses a managed fake
// player and a counter in place of the video view; no rendering occurs.
public sealed class VideoPresentationLogicTests
{
    [Theory]
    [InlineData(null, "1:1")]
    [InlineData("", "1:1")]
    [InlineData("1:1", "4:3")]
    [InlineData("16:9", "1:1")]
    public async Task DvdPreparation_PrimesADifferentAspectThenRestoresTheSelection(string? ratio, string primed)
    {
        var window = new MainWindow();
        window.PrepareAspectForLogic(ratio, dvd: true);
        Assert.Equal(primed, window.Backend.AspectRatio);
        await window.NotifyVideoOutputForLogicAsync(true, ratio);
        Assert.Equal(string.IsNullOrWhiteSpace(ratio) ? null : ratio, window.Backend.AspectRatio);
        Assert.Equal(0, window.Backend.Scale);
        Assert.Equal(1, window.VideoGeometryRefreshes);
    }

    [Fact]
    public void OtherSources_StartWithTheSelectedAspect()
    {
        var window = new MainWindow();
        window.PrepareAspectForLogic("16:9", dvd: false);
        Assert.Equal("16:9", window.Backend.AspectRatio);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task FirstOutput_RefreshesFitEvenWhenOriginalWasAlreadySelected(string? original)
    {
        var window = new MainWindow();
        window.ApplyAspectForLogic(original); // Startup ran before any output existed.
        Assert.Equal(0, window.Backend.Scale);
        Assert.Null(window.Backend.AspectRatio);
        Assert.Equal(0, window.VideoGeometryRefreshes);

        await window.NotifyVideoOutputForLogicAsync(true, original);

        Assert.Equal(0, window.Backend.Scale);
        Assert.Null(window.Backend.AspectRatio);
        Assert.Equal(2, window.Backend.PresentationApplyCount);
        Assert.Equal(1, window.VideoGeometryRefreshes);
    }

    [Theory]
    [InlineData("1:1")]
    [InlineData("16:9")]
    [InlineData("4:3")]
    [InlineData("21:9")]
    public async Task OutputCreation_PreservesTheSelectedOverrideAndRestoresAutoFit(string ratio)
    {
        var window = new MainWindow();
        window.Backend.Scale = 1.5f;
        await window.NotifyVideoOutputForLogicAsync(true, ratio);
        Assert.Equal(0, window.Backend.Scale);
        Assert.Equal(ratio, window.Backend.AspectRatio);
        Assert.Equal(1, window.VideoGeometryRefreshes);
    }

    [Fact]
    public async Task RecreatedDvdOutput_ReappliesGeometryEvenWhenTheOutputCountIsStillOne()
    {
        var window = new MainWindow();
        await window.NotifyVideoOutputForLogicAsync(true, "");
        window.Backend.Scale = 1.5f;
        await window.NotifyVideoOutputForLogicAsync(true, "");
        Assert.Equal(0, window.Backend.Scale);
        Assert.Null(window.Backend.AspectRatio);
        Assert.Equal(2, window.Backend.PresentationApplyCount);
        Assert.Equal(2, window.VideoGeometryRefreshes);
    }

    [Fact]
    public async Task OutputRemoval_DoesNotQueueCommandsOrRequestGeometry()
    {
        var window = new MainWindow();
        await window.NotifyVideoOutputForLogicAsync(false, "");
        Assert.Equal(0, window.Queue.EnqueuedCount);
        Assert.Equal(0, window.Backend.PresentationApplyCount);
        Assert.Equal(0, window.VideoGeometryRefreshes);
    }

    [Fact]
    public async Task NativeBackend_DoesNotReceiveCompatibilityPresentationCommands()
    {
        var window = new MainWindow();
        window.UseNativeBackendForLogic();
        await window.NotifyVideoOutputForLogicAsync(true, "");
        Assert.Equal(0, window.Queue.EnqueuedCount);
        Assert.Equal(0, window.Backend.PresentationApplyCount);
        Assert.Equal(0, window.VideoGeometryRefreshes);
    }

    [Fact]
    public async Task OldOutputNotification_DoesNotChangeTheNewSourcesPresentation()
    {
        var window = new MainWindow();
        window.SupersedeForLogic();
        var alreadyQueued = window.Queue.EnqueuedCount;
        await window.NotifyVideoOutputForLogicAsync(true, "1:1", generation: 0);
        Assert.Equal(alreadyQueued, window.Queue.EnqueuedCount);
        Assert.Equal(0, window.Backend.PresentationApplyCount);
        Assert.Equal(0, window.VideoGeometryRefreshes);
    }

    [Fact]
    public async Task QueuedPresentationChange_SkipsExecutionAndGeometryAfterSourceChange()
    {
        var window = new MainWindow();
        window.Queue.BlockNext = true;
        var update = window.NotifyVideoOutputForLogicAsync(true, "1:1");
        Assert.NotNull(window.Queue.FirstBlocked);
        window.SupersedeForLogic();
        window.Queue.CompleteQueued();
        await update;
        await Task.WhenAll(window.ObserverTasks);
        Assert.Equal(0, window.Backend.PresentationApplyCount);
        Assert.Equal(0, window.VideoGeometryRefreshes);
    }

    [Fact]
    public void ReturningToOriginal_ClearsTheOverrideAndKeepsAutoFit()
    {
        var window = new MainWindow();
        window.ApplyAspectForLogic("1:1");
        window.ApplyAspectForLogic("");
        Assert.Null(window.Backend.AspectRatio);
        Assert.Equal(0, window.Backend.Scale);
    }
}
