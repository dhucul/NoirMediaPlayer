using System.IO;
using System.Text.Json;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdNavigationPacketTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ReadsButtonGroupsAndDsiWithoutModifyingOrRetainingSource(int groups)
    {
        var bytes = DvdNavFixture.Create(groups); var original = bytes.ToArray();
        DvdNavFixture.Command(bytes, 0, 0x3002000000010000);
        var packet = DvdNavigationPacket.Read(bytes);
        Assert.Null(packet.CompatibilityReason); Assert.Equal(100u, packet.Sector);
        Assert.Equal(1, packet.VobId); Assert.Equal(2, packet.CellId);
        Assert.Equal(9u, packet.VobuEndRelativeSector); Assert.Equal(0x8000000au, packet.NextVobuRaw);
        Assert.Equal(0x3fffffffu, packet.PreviousVobuRaw);
        Assert.Equal(groups, packet.Highlight.Groups.Count);
        var button = packet.Highlight.Groups[0].Buttons[0];
        Assert.Equal(new DvdButtonBounds(10, 20, 50, 40), button.Bounds);
        Assert.Equal(2, button.Down); Assert.Equal(2, button.Right);
        Assert.Equal("3002000000010000", button.Command.Hex);
        DvdNavFixture.Command(original, 0, 0x3002000000010000);
        Assert.Equal(original, bytes);
        Array.Clear(bytes);
        Assert.Equal("3002000000010000", button.Command.Hex);
        Assert.Contains("3002000000010000", JsonSerializer.Serialize(packet));
        Assert.Equal(0x1234abcdu, packet.Highlight.Colors[0].Selected);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2047)] [InlineData(2049)] [InlineData(4096)]
    public void RequiresExactlyOneSector(int length) => Assert.Throws<InvalidDataException>(() => DvdNavigationPacket.Read(new byte[length]));

    [Theory]
    [InlineData(2, 0)] [InlineData(4, 0)] [InlineData(13, 0)]
    [InlineData(40, 0)] [InlineData(42, 255)] [InlineData(44, 1)]
    [InlineData(1027, 0xe0)] [InlineData(1030, 0)]
    public void RejectsBadPackPacketLengthsPrefixesAndDuplicateSubstreams(int offset, byte value)
    {
        var bytes = DvdNavFixture.Create(); bytes[offset] = value;
        Assert.Throws<InvalidDataException>(() => DvdNavigationPacket.Read(bytes));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void RejectsInvalidIdentityCountsAndButtonReferences(int mutation)
    {
        var b = DvdNavFixture.Create();
        switch (mutation)
        {
            case 0: DvdNavFixture.U32(b, DvdNavFixture.Dsi + 4, 200); break;
            case 1: b[DvdNavFixture.Dsi + 27] = 0; break;
            case 2: b[DvdNavFixture.Hli + 14] = 0; break;
            case 3: b[DvdNavFixture.Hli + 17] = 37; break;
            case 4: b[DvdNavFixture.Hli + 21] = 3; break;
            case 5: b[DvdNavFixture.ButtonOffset(0) + 6] = 3; break;
        }
        Assert.Throws<InvalidDataException>(() => DvdNavigationPacket.Read(b));
    }

    [Fact]
    public void RejectsInvertedGeometryAndOverflowingVobuRange()
    {
        var b = DvdNavFixture.Create(); b[DvdNavFixture.ButtonOffset(0) + 2] = 0;
        Assert.Throws<InvalidDataException>(() => DvdNavigationPacket.Read(b));
        b = DvdNavFixture.Create(); DvdNavFixture.U32(b, DvdNavFixture.Dsi + 8, uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => DvdNavigationPacket.Read(b));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void UnknownFlagsAndOffsetNumberingAreExplicitCompatibilityCases(int mutation)
    {
        var b = DvdNavFixture.Create();
        if (mutation == 0) b[DvdNavFixture.Hli + 16] = 1;
        if (mutation == 1) b[DvdNavFixture.ButtonOffset(0) + 3] |= 0x80;
        if (mutation == 2) b[DvdNavFixture.Hli + 19] = 1;
        Assert.NotNull(DvdNavigationPacket.Read(b).CompatibilityReason);
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(3)]
    public void ContinuationsAndNoHighlightDoNotParseUninitializedGeometry(ushort update)
    {
        var b = DvdNavFixture.Create(); Array.Fill(b, (byte)0xff, DvdNavFixture.Hli, 694);
        DvdNavFixture.U16(b, DvdNavFixture.Hli, update);
        var packet = DvdNavigationPacket.Read(b);
        Assert.Empty(packet.Highlight.Groups); Assert.Equal((DvdHighlightUpdate)update, packet.Highlight.Update);
        Assert.Null(packet.CompatibilityReason);
    }
}
