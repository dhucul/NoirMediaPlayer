using System.Text;
using System.Text.Json;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.LogicTests;

public sealed class SnapshotProtocolTests
{
    private static string Destination => Path.Combine(Path.GetTempPath(), "snapshot-protocol.tmp");

    [Fact]
    public void ExternalCue_IsCapturedAtRequestTime_WithoutReloadingFile()
    {
        var track = PlainSubtitleTrack.Parse("missing.srt", "1\n00:00:01,000 --> 00:00:02,000\n<b>current</b>\n\n2\n00:00:02,000 --> 00:00:03,000\nnext");
        Assert.NotNull(track);
        var request = new SourceSnapshotRequest("fake", 1500, Subtitles: track, EmbeddedCue: new(0, 4000, "embedded"));
        using var input = new MemoryStream(SnapshotProtocol.Encode(request, Destination));
        var restored = SnapshotProtocol.Read(input).ToRequest();
        Assert.Null(restored.Subtitles);
        Assert.Equal("current", restored.EmbeddedCue!.Text);
        Assert.Equal(SubtitleStyle.Bold, restored.EmbeddedCue.Styles[0].Style);
        Assert.Equal(1500, restored.Time);
    }

    [Fact]
    public void NullCaption_RoundTrips()
    {
        using var input = new MemoryStream(SnapshotProtocol.Encode(new("fake", 0), Destination));
        Assert.Null(SnapshotProtocol.Read(input).ToRequest().EmbeddedCue);
    }

    [Theory]
    [InlineData(null, 0, 0)]
    [InlineData("", 0, 0)]
    [InlineData("fake", -1, 0)]
    [InlineData("fake", long.MaxValue, 0)]
    [InlineData("fake", 0, 45)]
    public void InvalidState_IsRejected(string? source, long time, int rotation) =>
        Assert.Throws<InvalidDataException>(() => SnapshotProtocol.Encode(new(source!, time, Rotation: rotation), Destination));

    [Theory]
    [InlineData("null\n")]
    [InlineData("{\"Version\":2}\n")]
    public void NullOrUnsupportedVersion_IsRejected(string json)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => SnapshotProtocol.Read(input));
    }

    [Fact]
    public void UnterminatedAndOversizedMessages_AreBounded()
    {
        using var truncated = new MemoryStream("{}"u8.ToArray());
        Assert.Throws<EndOfStreamException>(() => SnapshotProtocol.Read(truncated));
        using var oversized = new MemoryStream(Enumerable.Repeat((byte)' ', SnapshotProtocol.MaxMessageBytes + 1).ToArray());
        Assert.Throws<InvalidDataException>(() => SnapshotProtocol.Read(oversized));
        Assert.Equal(SnapshotProtocol.MaxMessageBytes, oversized.Position);
    }

    [Fact]
    public void InvalidStyleBoundsAndNullStyleArray_AreRejected()
    {
        var cue = new SnapshotCaption(0, 1000, "short", [new(1, int.MaxValue, SubtitleStyle.Bold)], SubtitleAlignment.BottomCenter);
        foreach (var invalid in new[] { cue, cue with { Styles = null! }, cue with { Text = null! } })
        {
            var message = new SnapshotMessage(1, "fake", 0, null, 0, invalid, Destination);
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n"));
            Assert.Throws<InvalidDataException>(() => SnapshotProtocol.Read(input));
        }
    }
}
