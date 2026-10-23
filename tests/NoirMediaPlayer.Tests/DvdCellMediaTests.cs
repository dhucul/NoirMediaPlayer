using System.IO;
using System.Runtime.InteropServices;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdCellMediaTests
{
    [Fact]
    public void PreparationAndComReadsPreserveBytesWithoutAdvancingNavigation() => DvdCompletionFixture.With(c =>
    {
        var media = DvdCellMedia.Prepare(c.Catalog, c.State);
        Assert.Single(media.Units); Assert.Equal(c.Read.Unit!.ByteCount, media.ByteCount); Assert.Equal(0, media.AudioStreams);
        Assert.Same(c.State, c.Playback.State); Assert.Equal(7, c.State.Registers.General[0]);
        using var stream = media.OpenStream(); stream.Stat(out var stat, 1); Assert.Equal(media.ByteCount, stat.cbSize);
        var bytes = new byte[(int)media.ByteCount]; stream.Read(bytes, bytes.Length, 0); Assert.Equal(c.Read.Unit.Bytes.ToArray(), bytes);
        stream.Seek(-16, 2, 0); var tail = new byte[16]; stream.Read(tail, 16, 0); Assert.Equal(bytes[^16..], tail);
        stream.Seek(10_000_000, 0, 0); var count = Marshal.AllocHGlobal(8);
        try { stream.Read(tail, 16, count); Assert.Equal(0, Marshal.ReadInt32(count)); }
        finally { Marshal.FreeHGlobal(count); }
        Assert.Throws<IOException>(() => stream.Seek(-1, 0, 0)); Assert.Throws<NotSupportedException>(() => stream.Write([1], 1, 0));
        stream.Dispose(); Assert.Throws<ObjectDisposedException>(() => stream.Read(tail, 1, 0));
    }, commands: [0x7300000000010000]);

    [Theory]
    [InlineData("bytes")] [InlineData("units")] [InlineData("midcell")] [InlineData("private")]
    [InlineData("video")] [InlineData("audio")] [InlineData("scrambled")]
    public void UnreadyAndOversizedCellsCannotReachTheRenderer(string fault) => DvdCompletionFixture.With(c =>
    {
        var state = c.State;
        if (fault == "midcell") state = state with { Position = state.Position with { Sector = 101 } };
        if (fault == "units")
            DvdStreamFixture.Vob(Path.Combine(c.Folder, "VTS_01_1.VOB"), 400,
                (100, DvdStreamFixture.Nav(100, 0, 0x80000001)), (101, DvdStreamFixture.Nav(101, 98)));
        if (fault is "private" or "video" or "audio" or "scrambled")
        {
            var bytes = File.ReadAllBytes(Path.Combine(c.Folder, "VTS_01_1.VOB"));
            bytes[101 * 2048 + 17] = fault == "private" ? (byte)0xbd : fault == "video" ? (byte)0xe1 : (byte)0xc0;
            if (fault == "audio") bytes[102 * 2048 + 17] = 0xc1;
            if (fault == "scrambled") bytes[101 * 2048 + 20] |= 0x30;
            File.WriteAllBytes(Path.Combine(c.Folder, "VTS_01_1.VOB"), bytes);
        }
        Assert.Throws<NotSupportedException>(() => DvdCellMedia.Prepare(c.Catalog, state,
            byteLimit: fault == "bytes" ? 2048 : 32 * 1024 * 1024, unitLimit: fault == "units" ? 1 : 1024));
        Assert.Equal(7, c.State.Registers.General[0]);
    });

    [Fact]
    public void ReadOnlyComStreamCrossesSegmentsAndBoundsEachRead()
    {
        using var stream = new DvdCellComStream(new ReadOnlyMemory<byte>[] { new byte[700_000], Enumerable.Repeat((byte)7, 700_000).ToArray() });
        var bytes = new byte[1_400_000]; var count = Marshal.AllocHGlobal(8);
        try
        {
            stream.Read(bytes, bytes.Length, count); Assert.Equal(1024 * 1024, Marshal.ReadInt32(count)); Assert.Equal(7, bytes[700_000]);
            stream.Seek(699_998, 0, count); Assert.Equal(699_998, Marshal.ReadInt64(count)); stream.Read(bytes, 4, 0);
            Assert.Equal(new byte[] { 0, 0, 7, 7 }, bytes[..4]);
            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(bytes, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(0, 99, 0));
        }
        finally { Marshal.FreeHGlobal(count); }
    }

    [Fact]
    public void CancellationCannotOpenNativeMediaOrRunACommand() => DvdCompletionFixture.With(c =>
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => DvdCellMedia.Prepare(c.Catalog, c.State, cancellationToken: cancelled.Token));
        Assert.Equal(7, c.State.Registers.General[0]);
    }, commands: [0x7300000000010000]);
}
