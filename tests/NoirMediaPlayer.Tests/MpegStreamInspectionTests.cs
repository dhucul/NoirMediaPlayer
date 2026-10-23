using System.Buffers.Binary;
using System.IO;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class MpegStreamInspectionTests
{
    [Theory]
    [InlineData(".mpg", false)]
    [InlineData(".mpeg", false)]
    [InlineData(".vob", false)]
    [InlineData(".mpg", true)]
    public void ValidatedStandaloneLayoutsPreferWindowsAndKeepDiscsAndNetworkOnCompatibility(string extension, bool videoOnly)
    {
        WithFile(extension, path =>
        {
            MpegFixture.Write(path, videoOnly);
            var before = File.ReadAllBytes(path);
            var inventory = MpegStreamInspection.TryRead(path);
            Assert.NotNull(inventory);
            Assert.Equal(1, inventory.VideoCount);
            Assert.Equal(videoOnly ? 0 : 1, inventory.AudioCount);
            var item = new PlaylistItem { Source = path, Title = "MPEG" };
            var settings = new PlayerSettings();
            Assert.NotNull(WindowsPlaybackPolicy.CreatePlan(item, settings, 0, null));
            settings.HardwareDecoding = false;
            Assert.NotNull(WindowsPlaybackPolicy.CreatePlan(item, settings, 0, null));
            settings.PreferWindowsPlayback = false;
            Assert.Null(WindowsPlaybackPolicy.CreatePlan(item, settings, 0, null));
            settings.PreferWindowsPlayback = true;
            Assert.Null(WindowsPlaybackPolicy.CreatePlan(new PlaylistItem { Source = path, Title = "Disc", IsDisc = true }, settings, 0, null));
            Assert.Null(WindowsPlaybackPolicy.CreatePlan(new PlaylistItem { Source = "https://example.com/movie" + extension, Title = "Web", IsNetwork = true }, settings, 0, null));
            Assert.Equal(before, File.ReadAllBytes(path));
        });
    }

    [Theory]
    [InlineData(".srt", "1\n00:00:00,000 --> 00:00:25,000\nMPEG caption\n")]
    [InlineData(".vtt", "WEBVTT\n\n00:00.000 --> 00:25.000\nMPEG caption\n")]
    public void AutomaticCaptionsKeepTheCompleteCompatibilitySession(string extension, string content) => WithFile(".mpg", path =>
    {
        MpegFixture.Write(path);
        var captions = Path.ChangeExtension(path, extension);
        try
        {
            File.WriteAllText(captions, content);
            Assert.NotNull(MpegStreamInspection.TryRead(path));
            Assert.Null(WindowsPlaybackPolicy.CreatePlan(new PlaylistItem { Source = path, Title = "Captioned MPEG" }, new PlayerSettings(), 0, null));
        }
        finally { File.Delete(captions); }
    });

    [Theory]
    [InlineData(".ts")]
    [InlineData(".m2ts")]
    [InlineData(".mts")]
    public void TransportStreamsRetainCompatibilityUntilCaptionHandoffIsReady(string extension) => WithFile(extension, path =>
    {
        MpegFixture.Write(path);
        Assert.False(MpegStreamInspection.SupportsExtension(path));
        Assert.Null(WindowsPlaybackPolicy.CreatePlan(new PlaylistItem { Source = path, Title = "Transport" }, new PlayerSettings(), 0, null));
    });

    [Theory]
    [InlineData("crc")]
    [InlineData("unknown-codec")]
    [InlineData("extra-audio")]
    [InlineData("private-pes")]
    [InlineData("undeclared-pes")]
    [InlineData("scrambled-pes")]
    [InlineData("oversized-pes-header")]
    [InlineData("conditional-access")]
    [InlineData("changed-map")]
    [InlineData("missing-map")]
    public void UnsupportedOrMalformedProgramMetadataRetainsCompatibility(string fault) => WithFile(".mpg", path =>
    {
        MpegFixture.Write(path);
        var data = File.ReadAllBytes(path);
        var mapOffset = FindPacket(data, 0xbc);
        var mapLength = 6 + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(mapOffset + 4));
        var map = data.AsSpan(mapOffset, mapLength).ToArray();
        var pes = FindPacket(data, 0xe0);
        switch (fault)
        {
            case "crc": map[^1] ^= 1; break;
            case "unknown-codec": map[12] = 0x06; UpdateCrc(map); break;
            case "extra-audio":
                map = map[..^4].Concat(new byte[] { 0x0f, 0xc1, 0, 0 }).Concat(new byte[4]).ToArray();
                BinaryPrimitives.WriteUInt16BigEndian(map.AsSpan(4), (ushort)(map.Length - 6));
                BinaryPrimitives.WriteUInt16BigEndian(map.AsSpan(10), (ushort)(map.Length - 16));
                UpdateCrc(map);
                break;
            case "private-pes": data[pes + 3] = 0xbd; break;
            case "undeclared-pes": data[pes + 3] = 0xe1; break;
            case "scrambled-pes": data[pes + 6] |= 0x10; break;
            case "oversized-pes-header":
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(pes + 4), 3);
                data[pes + 8] = 10;
                break;
            case "conditional-access":
                map = map[..10].Concat(new byte[] { 9, 0 }).Concat(map[10..]).ToArray();
                BinaryPrimitives.WriteUInt16BigEndian(map.AsSpan(4), (ushort)(map.Length - 6));
                BinaryPrimitives.WriteUInt16BigEndian(map.AsSpan(8), 2);
                UpdateCrc(map);
                break;
            case "changed-map":
                var changed = map.ToArray(); changed[6] ^= 1; UpdateCrc(changed);
                map = map.Concat(changed).ToArray();
                break;
            case "missing-map": map[3] = 0xbe; break;
        }
        data = data[..mapOffset].Concat(map).Concat(data[(mapOffset + mapLength)..]).ToArray();
        File.WriteAllBytes(path, data);
        Assert.Null(MpegStreamInspection.TryRead(path));
        Assert.Null(WindowsPlaybackPolicy.CreatePlan(new PlaylistItem { Source = path, Title = "Unready MPEG" }, new PlayerSettings(), 0, null));
    });

    private static int FindPacket(byte[] data, byte id)
    {
        for (var offset = 0; offset + 6 <= data.Length;)
        {
            if (data[offset + 3] == id) return offset;
            offset += data[offset + 3] == 0xba ? 14 + (data[offset + 13] & 7) : 6 + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4));
        }
        throw new InvalidDataException("Missing fixture packet.");
    }

    private static void UpdateCrc(byte[] map) => BinaryPrimitives.WriteUInt32BigEndian(map.AsSpan(map.Length - 4), MpegFixture.Crc(map.AsSpan(0, map.Length - 4)));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(383)]
    public void MissingOrTruncatedTablesRetainCompatibility(int length) => WithFile(".mpg", path =>
    {
        MpegFixture.Write(path);
        var data = File.ReadAllBytes(path);
        File.WriteAllBytes(path, data[..length]);
        Assert.Null(MpegStreamInspection.TryRead(path));
    });

    [Fact]
    public void RenamedUnrelatedContainerIsNotAdmitted() => WithFile(".ts", path =>
    {
        Mp4Fixture.Write(path);
        Assert.Null(MpegStreamInspection.TryRead(path));
    });

    private static void WithFile(string extension, Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirMpegInspection-" + Guid.NewGuid().ToString("N") + extension);
        try { action(path); }
        finally { File.Delete(path); }
    }
}
