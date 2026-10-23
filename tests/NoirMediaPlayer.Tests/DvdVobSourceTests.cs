using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdVobSourceTests
{
    [Theory]
    [InlineData("missing")] [InlineData("gap")] [InlineData("unaligned")]
    [InlineData("empty")] [InlineData("oversized")] [InlineData("cell-outside")]
    public void InvalidSourcesKeepCompatibilityAndReleaseAllOpenedParts(string fault) => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        var first = Path.Combine(folder, "VTS_01_1.VOB");
        if (fault != "missing") DvdStreamFixture.Vob(first, 400);
        if (fault == "gap") DvdStreamFixture.Vob(Path.Combine(folder, "VTS_01_3.VOB"), 10);
        if (fault == "unaligned") File.WriteAllBytes(first, [1, 2, 3]);
        if (fault == "empty") File.WriteAllBytes(first, []);
        if (fault == "oversized") { using var file = File.OpenWrite(first); file.SetLength(1024L * 1024 * 1024 + 2048); }
        if (fault == "cell-outside") DvdStreamFixture.Vob(first, 150, (100, DvdStreamFixture.Nav(100, 9, 0x8000000a)));
        var read = new DvdStreamTraversal(catalog).Read(state);
        Assert.Equal(DvdStreamStatus.CompatibilityRequired, read.Status); Assert.Same(state, read.State);
        Assert.Null(read.Unit); Assert.Null(read.Next); Assert.NotNull(read.Reason);
        if (File.Exists(first)) using (File.Open(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    });

    [Fact]
    public void SourceReadsAreBoundedReadOnlyAndDisposeDeterministically() => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        var path = Path.Combine(folder, "VTS_01_1.VOB");
        DvdStreamFixture.Vob(path, 400, (100, DvdStreamFixture.Nav(100, 99)));
        var source = new DvdVobSource(folder, state.Position.Chain);
        Assert.Equal(400, source.SectorCount);
        var bytes = new byte[2048]; source.Read(100, bytes);
        Assert.Equal(100u, DvdNavigationPacket.Read(bytes).Sector);
        Assert.Throws<IOException>(() => { using var blocked = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None); });
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Read(0, new byte[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Read(0, new byte[2048 * 1025]));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Read(0, Array.Empty<byte>()));
        Assert.Throws<InvalidDataException>(() => source.Read(400, bytes));
        Assert.Throws<InvalidDataException>(() => source.Read(uint.MaxValue, bytes));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => source.Read(0, bytes, cancelled.Token));
        source.Dispose(); source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.Read(0, bytes));
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    });

    [Theory]
    [InlineData("relative")] [InlineData("network")] [InlineData("set")] [InlineData("domain")]
    public void ExplicitLocalDirectoryAndValidDomainAreRequired(string fault) => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        var key = state.Position.Chain;
        if (fault is "relative" or "network")
            Assert.Throws<ArgumentException>(() => new DvdVobSource(fault == "relative" ? "VIDEO_TS" : @"\\server\DVD", key));
        else
            Assert.Throws<InvalidDataException>(() => new DvdVobSource(folder,
                fault == "set" ? key with { TitleSet = 100 } : key with { Domain = (DvdPlaybackDomain)99 }));
    });

    [Fact]
    public void DirectoryNamedAsAVobCannotBecomeAStreamFile() => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        var path = Path.Combine(folder, "VTS_01_1.VOB");
        // A directory is not a VOB, even if it has the expected file name.
        Directory.CreateDirectory(path);
        var read = new DvdStreamTraversal(catalog).Read(state);
        Assert.Equal(DvdStreamStatus.CompatibilityRequired, read.Status); Assert.Null(read.Unit);
    });
}
