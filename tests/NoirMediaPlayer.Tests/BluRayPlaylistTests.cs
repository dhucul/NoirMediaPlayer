using System.Buffers.Binary;
using System.IO;
using NoirMediaPlayer.Services;
using Xunit;
using static NoirMediaPlayer.Tests.BluRayFixture;

namespace NoirMediaPlayer.Tests;

public sealed class BluRayPlaylistTests
{
    [Theory]
    [InlineData("0100")]
    [InlineData("0200")]
    [InlineData("0300")]
    public void LinearPlaylist_KeepsTrimPointsClipOrderAndChapterClock(string version)
    {
        // Repeat the same clip deliberately. Its second occurrence is a different
        // playlist interval and must not be deduplicated or sorted by clip name.
        var file = Playlist(
            [Item("00123", 90000, 270000), Item("00002", 450000, 720000), Item("00123", 270000, 360000)],
            [Mark(0, 90000), Mark(0, 180000), Mark(1, 450000), Mark(1, 585000), Mark(2, 270000)], version: version);
        var playlist = BluRayPlaylistReader.Parse(file);
        Assert.Equal(version, playlist.Version);
        Assert.Equal(new[] { "00123", "00002", "00123" }, playlist.Items.Select(item => item.Clips[0].Id));
        Assert.Equal(new long[] { 0, 180000, 450000 }, playlist.Items.Select(item => item.StartTime));
        Assert.Equal(540000, playlist.SequentialDurationTicks);
        Assert.Equal(12000, playlist.SequentialDurationMilliseconds);
        Assert.Equal(new long[] { 0, 2000, 4000, 7000, 10000 }, playlist.CreateLinearChapterMap()!.Starts);
        Assert.Equal(4000, playlist.CreateLinearChapterMap()!.Navigate(2000, true));
        Assert.Equal(7000, playlist.CreateLinearChapterMap()!.Navigate(11000, false));
        Assert.Equal(BluRayPlaylistFeatures.None, playlist.Features);
        Assert.Equal(0xc000, playlist.ApplicationFlags);
        Assert.Equal(0, playlist.PlaybackCount);
        Assert.All(playlist.Items, item => Assert.True(item.RandomAccess));
        Assert.All(playlist.Items, item => Assert.Equal(9, item.Clips[0].StcId));
    }

    [Fact]
    public void FractionalMilliseconds_AreAccumulatedBeforeConversion()
    {
        var playlist = BluRayPlaylistReader.Parse(Playlist(
            [Item(input: 900, output: 946), Item(input: 1000, output: 1046), Item(input: 2000, output: 2090)],
            [Mark(0, 900), Mark(2, 2000)]));
        Assert.Equal(182, playlist.SequentialDurationTicks);
        Assert.Equal(4, playlist.SequentialDurationMilliseconds);
        Assert.Equal(92, playlist.Marks[1].PlaylistTimeTicks);
        Assert.Equal(new long[] { 0, 2 }, playlist.CreateLinearChapterMap()!.Starts);
    }

    [Fact]
    public void Tracks_KeepPidsLanguagesAndTypesAcrossClips()
    {
        var table = Table(
            video: [Stream(0x1011, 0x1b, [0x64]), Stream(0x1012, 0x24, [0x64, 0x12, 0])],
            audio: [Stream(0x1100, 0x81, Join([0x31], Text("eng"))), Stream(0x1101, 0x83, Join([0x61], Text("fra")))],
            pg: [Stream(0x1200, 0x90, Text("eng"))], ig: [Stream(0x1400, 0x91, Text("jpn"))]);
        var playlist = BluRayPlaylistReader.Parse(Playlist([Item(table: table, operations: 0x0102030405060708)]));
        var item = Assert.Single(playlist.Items);
        Assert.Equal(0x0102030405060708UL, item.UserOperationMask);
        Assert.Equal(new ushort?[] { 0x1011, 0x1012, 0x1100, 0x1101, 0x1200, 0x1400 }, item.Streams.Select(stream => stream.PacketId));
        Assert.Equal(new[] { "eng", "fra", "eng", "jpn" }, item.Streams.Skip(2).Select(stream => stream.Language));
        Assert.Equal((byte)6, item.Streams[0].Format);
        Assert.Equal((byte)4, item.Streams[0].Rate);
        Assert.Equal(BluRayPlaylistFeatures.InteractiveGraphics, playlist.Features);
    }

    [Fact]
    public void SecondaryStreams_DoNotShiftTheFollowingEntries()
    {
        var table = Table(
            audio: [Stream(0x1100, 0x81, Join([0x31], Text("eng")))],
            pg: [Stream(0x1200, 0x90, Text("eng"))],
            pip: [Stream(0x1201, 0x90, Text("spa"), 2)],
            ig: [Stream(0x1400, 0x91, Text("fra"))],
            secondaryAudio: [Join(Stream(0x1a00, 0xa1, Join([0x31], Text("jpn")), 3), [1, 0, 0, 0])],
            secondaryVideo: [Join(Stream(0x1b00, 0x1b, [0x64], 4), [1, 0, 0, 0, 1, 0, 0, 0])],
            dv: [Stream(0x1015, 0x24, [0x64, 0x12, 0])]);
        var playlist = BluRayPlaylistReader.Parse(Playlist([Item(table: table)]));
        var streams = playlist.Items[0].Streams;
        Assert.Equal(new[] { "Audio", "PresentationGraphics", "PipGraphics", "InteractiveGraphics", "SecondaryAudio", "SecondaryVideo", "DolbyVision" }, streams.Select(stream => stream.Kind));
        Assert.Equal((ushort)0x1015, streams[^1].PacketId);
        Assert.Equal((byte)3, streams[2].SubPathId);
        Assert.Equal((byte)4, streams[2].SubClipId);
        Assert.Null(streams[4].SubClipId);
        Assert.True(playlist.Features.HasFlag(BluRayPlaylistFeatures.SecondaryStreams));
    }

    [Fact]
    public void UnknownStreamDescriptors_AreBoundedAndFlagged()
    {
        var playlist = BluRayPlaylistReader.Parse(Playlist([Item(table: Table(video:
            [Stream(1, 0xff, [], 0xff), Stream(0x1011, 0x1b, [0x64])]))]));
        Assert.Equal(2, playlist.Items[0].Streams.Count);
        Assert.Null(playlist.Items[0].Streams[0].PacketId);
        Assert.Equal((ushort)0x1011, playlist.Items[0].Streams[1].PacketId);
        Assert.True(playlist.Features.HasFlag(BluRayPlaylistFeatures.UnknownStreamTypes));
        Assert.True(playlist.Features.HasFlag(BluRayPlaylistFeatures.UnknownCodecTypes));
    }

    [Fact]
    public void MultiAnglePlaylist_RetainsAlternatesAndDoesNotProduceLinearNavigation()
    {
        var playlist = BluRayPlaylistReader.Parse(Playlist([Item(angles: ["00002", "00003"])]));
        Assert.Equal(new[] { "00001", "00002", "00003" }, playlist.Items[0].Clips.Select(clip => clip.Id));
        Assert.Equal(7, playlist.Items[0].Clips[1].StcId);
        Assert.Equal(3, playlist.Items[0].AngleFlags);
        Assert.True(playlist.Features.HasFlag(BluRayPlaylistFeatures.MultiAngle));
        Assert.Null(playlist.CreateLinearChapterMap());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomAndShufflePlaylists_AreNotNormalizedToSequentialPlayback(byte type)
    {
        var playlist = BluRayPlaylistReader.Parse(Playlist(playbackType: type));
        Assert.Equal(3, playlist.PlaybackCount);
        Assert.True(playlist.Features.HasFlag(BluRayPlaylistFeatures.NonSequential));
        Assert.Null(playlist.CreateLinearChapterMap());
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 0)]
    public void StillHolds_DoNotBecomeOrdinaryClipDurations(byte mode, ushort time)
    {
        var playlist = BluRayPlaylistReader.Parse(Playlist([Item(stillMode: mode, stillTime: time)]));
        Assert.Equal(mode, playlist.Items[0].StillMode);
        Assert.Equal(time, playlist.Items[0].StillTime);
        Assert.True(playlist.Features.HasFlag(BluRayPlaylistFeatures.StillFrames));
        Assert.Null(playlist.CreateLinearChapterMap());
    }

    [Fact]
    public void SubPathsAndExtensionMetadata_PreventLinearChapterMaps()
    {
        var subPaths = BluRayPlaylistReader.Parse(Playlist(subPaths: [new byte[6]]));
        Assert.Equal(1, subPaths.SubPathCount);
        Assert.Null(subPaths.CreateLinearChapterMap());
        var extended = BluRayPlaylistReader.Parse(Playlist(extension: new byte[12]));
        Assert.True(extended.Features.HasFlag(BluRayPlaylistFeatures.ExtendedData));
        Assert.Null(extended.CreateLinearChapterMap());
    }

    [Fact]
    public void LinkMarks_AreRetainedWithoutBecomingChapters()
    {
        var playlist = BluRayPlaylistReader.Parse(Playlist(marks:
            [Mark(time: 45000), Mark(time: 135000, type: 2, duration: 4500), Mark(time: 225000)]));
        Assert.Equal(3, playlist.Marks.Count);
        Assert.Equal(4500u, playlist.Marks[1].Duration);
        Assert.Equal(new long[] { 0, 4000 }, playlist.CreateLinearChapterMap()!.Starts);
    }

    [Fact]
    public void EmptyPlaylistAndChapterlessPlaylist_AreDifferentFromInvalidMetadata()
    {
        var empty = BluRayPlaylistReader.Parse(Playlist(items: [], marks: []));
        Assert.Empty(empty.Items);
        Assert.Null(empty.CreateLinearChapterMap());
        Assert.Empty(BluRayPlaylistReader.Parse(Playlist(marks: [])).CreateLinearChapterMap()!.Starts);
    }

    [Fact]
    public void AmbiguousChapterEntries_AreNotSilentlyReorderedOrMerged()
    {
        foreach (var marks in new[]
        {
            new[] { Mark(time: 135000), Mark(time: 45000) },
            new[] { Mark(), Mark() }, new[] { Mark(), Mark(time: 45001) },
            new[] { Mark(), Mark(time: 495000) }, new[] { Mark(type: 3) }
        })
        {
            var playlist = BluRayPlaylistReader.Parse(Playlist(marks: marks));
            Assert.Equal(marks.Length, playlist.Marks.Count);
            Assert.Null(playlist.CreateLinearChapterMap());
        }
    }

    [Theory]
    [InlineData(1, 45000)]
    [InlineData(0, 44999)]
    [InlineData(0, 495001)]
    public void InvalidMarkReferencesOrTimes_AreRejected(ushort reference, uint time)
    {
        Assert.Throws<InvalidDataException>(() => BluRayPlaylistReader.Parse(Playlist(marks: [Mark(reference, time)])));
    }

    [Theory]
    [InlineData("../12")]
    [InlineData("00/01")]
    [InlineData("C:001")]
    [InlineData("000a1")]
    public void ClipNames_CannotBecomeArbitraryPaths(string clip) =>
        Assert.Throws<InvalidDataException>(() => BluRayPlaylistReader.Parse(Playlist([Item(clip)])));

    [Fact]
    public void MalformedDescriptorsAndIntervals_AreRejected()
    {
        var invalid = new[]
        {
            Playlist([Item(input: 500000, output: 45000)]),
            Playlist([Item(table: Sized16(new byte[13]))]),
            Playlist([Item(table: Table(audio: [Stream(1, 0x81, [0x31])]))]),
            Playlist([Item(table: Table(pg: [Stream(1, 0x90, Text("e\ng"))]))]),
            Playlist([Item(table: Table(secondaryAudio: [Join(Stream(1, 0xa1, Join([0x31], Text("eng"))), [1, 0, 0, 0])]))]),
            Playlist(subPaths: [new byte[5]]), Playlist(playbackType: 0), Playlist(version: "9999")
        };
        foreach (var bytes in invalid) Assert.Throws<InvalidDataException>(() => BluRayPlaylistReader.Parse(bytes));
    }

    [Fact]
    public void EveryTruncationAndOverlappingSection_IsRejected()
    {
        var complete = Playlist([Item(table: Table(audio: [Stream(1, 0x81, Join([0x31], Text("eng")))]))]);
        for (var length = 0; length < complete.Length; length++)
        {
            var truncated = complete[..length];
            Assert.Throws<InvalidDataException>(() => BluRayPlaylistReader.Parse(truncated));
        }
        foreach (var offset in new[] { 8, 12, 16 })
        foreach (var value in new uint[] { 1, 40, uint.MaxValue })
        {
            var corrupt = (byte[])complete.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(corrupt.AsSpan(offset), value);
            Assert.Throws<InvalidDataException>(() => BluRayPlaylistReader.Parse(corrupt));
        }
    }

    [Fact]
    public void DeclaredLengthsAndCounts_CannotEscapeTheirContainingRecord()
    {
        var file = Playlist();
        var playlistStart = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(8));
        var marksStart = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(12));
        foreach (var offset in new[] { 40, playlistStart, marksStart })
        {
            var corrupt = (byte[])file.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(corrupt.AsSpan(offset), uint.MaxValue);
            Assert.Throws<InvalidDataException>(() => BluRayPlaylistReader.Parse(corrupt));
        }
        foreach (var offset in new[] { playlistStart + 6, marksStart + 4, playlistStart + 10 })
        {
            var corrupt = (byte[])file.Clone();
            BinaryPrimitives.WriteUInt16BigEndian(corrupt.AsSpan(offset), ushort.MaxValue);
            Assert.Throws<InvalidDataException>(() => BluRayPlaylistReader.Parse(corrupt));
        }
        Assert.Throws<InvalidDataException>(() => BluRayPlaylistReader.Parse(new byte[BluRayPlaylistReader.MaximumFileBytes + 1]));
    }

    [Fact]
    public void FileReader_IsReadOnlyAndDoesNotNeedMediaOrAacsFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirMpls-" + Guid.NewGuid() + ".mpls");
        var file = Playlist();
        try
        {
            File.WriteAllBytes(path, file);
            using var heldReadOnly = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(10000, BluRayPlaylistReader.Read(path).SequentialDurationMilliseconds);
            Assert.Equal(file, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownTrailingData_DoesNotBecomeAnApprovedLinearTimeline()
    {
        var padded = BluRayPlaylistReader.Parse(Join(Playlist(), new byte[12]));
        Assert.NotNull(padded.CreateLinearChapterMap());
        var extended = BluRayPlaylistReader.Parse(Join(Playlist(), [0x80]));
        Assert.True(extended.Features.HasFlag(BluRayPlaylistFeatures.UnparsedData));
        Assert.Null(extended.CreateLinearChapterMap());
    }

    [Fact]
    public void StreamLimit_AppliesAcrossTheEntirePlaylist()
    {
        var streams = Enumerable.Repeat(Stream(0x1011, 0x1b, [0x64]), 65).ToArray();
        var item = Item(table: Table(video: streams));
        var items = Enumerable.Repeat(item, 505).ToArray();
        Assert.Throws<InvalidDataException>(() => BluRayPlaylistReader.Parse(Playlist(items)));
    }

    [Fact]
    public void CorruptedFields_OnlyProduceBoundedMetadataOrDataErrors()
    {
        var valid = Playlist([Item(table: Table(audio: [Stream(0x1100, 0x81, Join([0x31], Text("eng")))]))]);
        var random = new Random(1206);
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var bytes = (byte[])valid.Clone();
            bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
            try
            {
                var result = BluRayPlaylistReader.Parse(bytes);
                Assert.True(result.SequentialDurationTicks >= 0);
                Assert.All(result.Marks, mark => Assert.InRange(mark.PlaylistTimeTicks, 0, result.SequentialDurationTicks));
            }
            catch (InvalidDataException) { }
        }
    }
}
