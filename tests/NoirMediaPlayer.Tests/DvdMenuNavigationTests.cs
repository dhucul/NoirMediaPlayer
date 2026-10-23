using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdMenuNavigationTests
{
    private static readonly DvdMenuContext Context = new(DvdMenuDomain.TitleSetMenu, 1, 1, 0);
    private const uint Now = 100_000;

    [Fact]
    public void KeyboardMouseAndNumericSelectionUseAuthoredLinksAndInclusiveBounds()
    {
        var nav = Load(DvdNavFixture.Create());
        Assert.Equal(DvdMenuOutcome.Unchanged, nav.Tick(Now).Outcome);
        Assert.Equal(1, nav.SelectedButton);
        Assert.Equal(DvdMenuOutcome.SelectionChanged, nav.Move(DvdMenuDirection.Right, Now).Outcome);
        Assert.Equal(2, nav.SelectedButton);
        Assert.Equal(DvdMenuOutcome.Unchanged, nav.Move(DvdMenuDirection.Right, Now).Outcome); // neighbor zero
        Assert.Equal(DvdMenuOutcome.SelectionChanged, nav.Move(DvdMenuDirection.Up, Now).Outcome);
        Assert.Equal(DvdMenuOutcome.SelectionChanged, nav.Point(90, 40, Now).Outcome);
        Assert.Equal(2, nav.SelectedButton);
        Assert.Equal(DvdMenuOutcome.Unchanged, nav.Point(500, 500, Now).Outcome);
        Assert.Equal(DvdMenuOutcome.SelectionChanged, nav.SelectNumber(1, Now).Outcome);
        Assert.Equal(DvdMenuOutcome.Prohibited, nav.SelectNumber(0, Now).Outcome);
        Assert.Equal(DvdMenuOutcome.Prohibited, nav.SelectNumber(3, Now).Outcome);
        Assert.Equal(new DvdButtonHighlight(1, new(10, 20, 50, 40), 0x1234abcd, false), nav.GetHighlight(Now));
        Assert.Equal(DvdMenuOutcome.NoOperation, nav.Point(10, 20, Now, activate: true).Outcome);
        Assert.Equal(0x4321dcbau, nav.GetHighlight(Now)!.ColorAndAlpha);
        Assert.True(nav.GetHighlight(Now)!.Activated);
    }

    [Fact]
    public void NumericRestrictionDoesNotBlockMouseOrDirectionalSelection()
    {
        var bytes = DvdNavFixture.Create(); bytes[DvdNavFixture.Hli + 18] = 1;
        var nav = Load(bytes);
        Assert.Equal(DvdMenuOutcome.Prohibited, nav.SelectNumber(2, Now).Outcome);
        Assert.Equal(DvdMenuOutcome.SelectionChanged, nav.Move(DvdMenuDirection.Down, Now).Outcome);
        Assert.Equal(2, nav.SelectedButton);
    }

    [Theory]
    [InlineData(0, 10)] [InlineData(1, 110)] [InlineData(2, 210)]
    public void UsesTheCorrectGeometryAndCommandsForDisplayGroup(int mode, int left)
    {
        var display = (DvdMenuDisplay)mode;
        var b = DvdNavFixture.Create(3); DvdNavFixture.Command(b, 12, 0x3002000000040000);
        var nav = Load(b, display);
        Assert.Equal(left, nav.GetHighlight(Now)!.Bounds.Left);
        var action = nav.Activate(Now);
        Assert.Equal(display == DvdMenuDisplay.Wide ? DvdMenuOutcome.CommandRequiresCompatibility : DvdMenuOutcome.NoOperation, action.Outcome);
        if (display == DvdMenuDisplay.Wide) Assert.Equal("3002000000040000", action.Command!.Hex);
    }

    [Fact]
    public void MissingOrAmbiguousDisplayGroupNeverUsesAnotherGroupsCoordinates()
    {
        var b = DvdNavFixture.Create(2); var nav = new DvdMenuNavigation();
        Assert.Equal(DvdMenuOutcome.Unsupported, nav.Load(DvdNavigationPacket.Read(b), Context, DvdMenuDisplay.PanScan).Outcome);
        b[DvdNavFixture.Hli + 14] |= 1;
        Assert.Equal(DvdMenuOutcome.Unsupported, nav.Load(DvdNavigationPacket.Read(b), Context, DvdMenuDisplay.Wide).Outcome);
        Assert.Null(nav.GetHighlight(Now));
    }

    [Fact]
    public void SelectionEndDoesNotPrematurelyEndHighlightOrActivation()
    {
        var nav = Load(DvdNavFixture.Create());
        Assert.Null(nav.GetHighlight(89_999));
        Assert.Equal(DvdMenuOutcome.Inactive, nav.Activate(89_999).Outcome);
        Assert.NotNull(nav.GetHighlight(90_000));
        Assert.Equal(DvdMenuOutcome.SelectionChanged, nav.Move(DvdMenuDirection.Right, 269_999).Outcome);
        Assert.Equal(DvdMenuOutcome.Prohibited, nav.Move(DvdMenuDirection.Left, 270_000).Outcome);
        Assert.Equal(DvdMenuOutcome.NoOperation, nav.Activate(270_000).Outcome);
        Assert.NotNull(nav.GetHighlight(359_999));
        Assert.Null(nav.GetHighlight(360_000));
        Assert.Equal(DvdMenuOutcome.Inactive, nav.Activate(360_000).Outcome);
    }

    [Fact]
    public void TimingSupportsClockWrapAndIndefiniteStillMenus()
    {
        var b = DvdNavFixture.Create();
        DvdNavFixture.U32(b, DvdNavFixture.Hli + 2, uint.MaxValue - 999);
        DvdNavFixture.U32(b, DvdNavFixture.Hli + 6, 1000);
        DvdNavFixture.U32(b, DvdNavFixture.Hli + 10, 500);
        var nav = Load(b);
        Assert.Null(nav.GetHighlight(uint.MaxValue - 1000));
        Assert.NotNull(nav.GetHighlight(0)); Assert.NotNull(nav.GetHighlight(999));
        Assert.Null(nav.GetHighlight(1000));
        DvdNavFixture.U32(b, DvdNavFixture.Hli + 6, uint.MaxValue);
        DvdNavFixture.U32(b, DvdNavFixture.Hli + 10, uint.MaxValue);
        nav = Load(b);
        Assert.Equal(DvdMenuOutcome.NoOperation, nav.Activate(50_000_000).Outcome);
    }

    [Theory]
    [InlineData(90_000, 90_000, 90_000)] [InlineData(90_000, 89_999, 89_999)]
    [InlineData(90_000, 100_000, 110_000)] [InlineData(90_000, 100_000, uint.MaxValue)]
    public void UnsupportedTimingClearsTheOldMenu(uint start, uint end, uint selectionEnd)
    {
        var nav = Load(DvdNavFixture.Create()); var b = DvdNavFixture.Create();
        DvdNavFixture.U32(b, DvdNavFixture.Hli + 2, start); DvdNavFixture.U32(b, DvdNavFixture.Hli + 6, end);
        DvdNavFixture.U32(b, DvdNavFixture.Hli + 10, selectionEnd);
        Assert.Equal(DvdMenuOutcome.Unsupported, nav.Load(DvdNavigationPacket.Read(b), Context, DvdMenuDisplay.Normal).Outcome);
        Assert.Equal(0, nav.SelectedButton); Assert.Null(nav.GetHighlight(Now));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BothPgcAndNavProhibitionsBlockUserSelectionAndActivation(bool pgc)
    {
        var b = DvdNavFixture.Create(); if (!pgc) DvdNavFixture.U32(b, DvdNavFixture.Pci + 8, 1u << 17);
        var nav = Load(b, restrictions: pgc ? 1u << 17 : 0);
        Assert.Equal(DvdMenuOutcome.Prohibited, nav.Move(DvdMenuDirection.Right, Now).Outcome);
        Assert.Equal(DvdMenuOutcome.Prohibited, nav.Point(60, 20, Now, true).Outcome);
        Assert.Equal(DvdMenuOutcome.Prohibited, nav.SelectNumber(2, Now).Outcome);
        Assert.Equal(DvdMenuOutcome.Prohibited, nav.Activate(Now).Outcome);
        Assert.Equal(1, nav.SelectedButton);
    }

    [Fact]
    public void ForcedAndAutomaticActionsAreEmittedOnceAndPreserveOpaqueVmBytes()
    {
        var b = DvdNavFixture.Create(); b[DvdNavFixture.Hli + 20] = 1; b[DvdNavFixture.Hli + 21] = 2;
        DvdNavFixture.Command(b, 1, 0x5600aabbccddeeff);
        var nav = Load(b, restrictions: 1u << 17);
        Assert.Equal(DvdMenuOutcome.Inactive, nav.Tick(89_999).Outcome);
        var action = nav.Tick(90_000);
        Assert.Equal(2, action.Button); Assert.Equal(DvdMenuOutcome.CommandRequiresCompatibility, action.Outcome);
        Assert.Equal("5600AABBCCDDEEFF", action.Command!.Hex);
        Assert.NotEqual(DvdMenuOutcome.CommandRequiresCompatibility, nav.Tick(Now).Outcome);
        Assert.Equal(DvdMenuOutcome.Unsupported, nav.Activate(Now).Outcome);
        Assert.Equal(DvdMenuOutcome.Unsupported, nav.Move(DvdMenuDirection.Left, Now).Outcome);
        Assert.True(nav.GetHighlight(Now)!.Activated);

        b = DvdNavFixture.Create(); b[DvdNavFixture.ButtonOffset(1) + 3] |= 0x40;
        DvdNavFixture.Command(b, 1, 0x3002000000010000);
        nav = Load(b);
        Assert.Equal(DvdMenuOutcome.CommandRequiresCompatibility, nav.Move(DvdMenuDirection.Right, Now).Outcome);
        Assert.Equal(2, nav.SelectedButton);
    }

    [Fact]
    public void InitialForcedSelectionCanAutoActivateWithoutAUserEvent()
    {
        var b = DvdNavFixture.Create(); b[DvdNavFixture.Hli + 20] = 2;
        b[DvdNavFixture.ButtonOffset(1) + 3] |= 0x40;
        var nav = Load(b);
        Assert.Equal(2, nav.SelectedButton);
        Assert.Equal(DvdMenuOutcome.NoOperation, nav.Tick(Now).Outcome);
        Assert.Equal(DvdMenuOutcome.Unchanged, nav.Tick(Now).Outcome);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)]
    public void ContinuationsKeepGeometryTimingAndSelectionButCanUpdateOnlyCommands(ushort update)
    {
        var original = DvdNavFixture.Create(2);
        var nav = Load(original, DvdMenuDisplay.Wide);
        nav.Move(DvdMenuDirection.Right, Now);
        var repeat = DvdNavFixture.Create(); Array.Clear(repeat, DvdNavFixture.Hli, 694);
        DvdNavFixture.U16(repeat, DvdNavFixture.Hli, update); DvdNavFixture.Sector(repeat, 110);
        DvdNavFixture.Command(repeat, 19, 0x3002000000050000);
        Assert.Equal(DvdMenuOutcome.Unchanged, nav.Load(DvdNavigationPacket.Read(repeat), Context, DvdMenuDisplay.Wide).Outcome);
        Assert.Equal(160, nav.GetHighlight(Now)!.Bounds.Left); Assert.Equal(2, nav.SelectedButton);
        var action = nav.Activate(Now);
        Assert.Equal(update == 3 ? DvdMenuOutcome.CommandRequiresCompatibility : DvdMenuOutcome.NoOperation, action.Outcome);
        if (update == 3) Assert.Equal("3002000000050000", action.Command!.Hex);
        Assert.Null(nav.GetHighlight(360_000));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void RepeatsNeverReuseMenuAcrossDiscontinuitiesOrWithoutPriorState(int change)
    {
        var nav = change == 0 ? new DvdMenuNavigation() : Load(DvdNavFixture.Create());
        var b = DvdNavFixture.Create(); DvdNavFixture.U16(b, DvdNavFixture.Hli, 2); DvdNavFixture.Sector(b, 110);
        var context = Context;
        if (change == 1) context = context with { Epoch = 1 };
        if (change == 2) context = context with { ProgramChain = 2 };
        if (change == 3) context = context with { Domain = DvdMenuDomain.Manager };
        if (change == 4) b[DvdNavFixture.Dsi + 27] = 3;
        if (change == 5) DvdNavFixture.Sector(b, 100);
        Assert.Equal(DvdMenuOutcome.Unsupported, nav.Load(DvdNavigationPacket.Read(b), context, DvdMenuDisplay.Normal).Outcome);
        Assert.Null(nav.GetHighlight(Now)); Assert.Equal(0, nav.SelectedButton);
    }

    [Fact]
    public void NoHighlightAndResetRemoveStaleButtonsAndPendingActions()
    {
        var nav = Load(DvdNavFixture.Create()); var b = DvdNavFixture.Create();
        DvdNavFixture.U16(b, DvdNavFixture.Hli, 0);
        Assert.Equal(DvdMenuOutcome.Inactive, nav.Load(DvdNavigationPacket.Read(b), Context, DvdMenuDisplay.Normal).Outcome);
        Assert.Equal(DvdMenuOutcome.Inactive, nav.Move(DvdMenuDirection.Right, Now).Outcome);
        nav = Load(DvdNavFixture.Create()); nav.Reset(); Assert.Null(nav.GetHighlight(Now));
    }

    [Fact]
    public void DuplicatePacketsNeverReissueForcedCommandsAndEpochAllowsAnIntentionalRestart()
    {
        var b = DvdNavFixture.Create(); b[DvdNavFixture.Hli + 21] = 2;
        DvdNavFixture.Command(b, 1, 0x3002000000010000);
        var packet = DvdNavigationPacket.Read(b);
        var nav = new DvdMenuNavigation(); nav.Load(packet, Context, DvdMenuDisplay.Normal);
        Assert.Equal(DvdMenuOutcome.CommandRequiresCompatibility, nav.Tick(Now).Outcome);
        Assert.Equal(DvdMenuOutcome.Unchanged, nav.Load(packet, Context, DvdMenuDisplay.Normal).Outcome);
        Assert.Equal(DvdMenuOutcome.Unsupported, nav.Tick(Now).Outcome);
        Assert.Equal(DvdMenuOutcome.Unsupported, nav.Load(DvdNavigationPacket.Read(b), Context, DvdMenuDisplay.Normal).Outcome);
        nav.Load(packet, Context with { Epoch = 1 }, DvdMenuDisplay.Normal);
        Assert.Equal(DvdMenuOutcome.CommandRequiresCompatibility, nav.Tick(Now).Outcome);
    }

    [Fact]
    public void OverlappingMouseTargetsAreNotGuessedAndColorZeroOmitsTheOverlay()
    {
        var b = DvdNavFixture.Create();
        Array.Copy(b, DvdNavFixture.ButtonOffset(0), b, DvdNavFixture.ButtonOffset(1), 6);
        var nav = Load(b);
        Assert.Equal(DvdMenuOutcome.Unsupported, nav.Point(20, 30, Now, true).Outcome);
        Assert.Equal(1, nav.SelectedButton);
        b[DvdNavFixture.ButtonOffset(0)] &= 0x3f;
        nav = Load(b); Assert.Null(nav.GetHighlight(Now));
        Assert.Equal(DvdMenuOutcome.NoOperation, nav.Activate(Now).Outcome);
    }

    [Fact]
    public void ADisplayChangeOnARepeatClearsTheOldCoordinates()
    {
        var b = DvdNavFixture.Create(2); var nav = Load(b);
        DvdNavFixture.Sector(b, 110); DvdNavFixture.U16(b, DvdNavFixture.Hli, 2);
        Assert.Equal(DvdMenuOutcome.Unsupported, nav.Load(DvdNavigationPacket.Read(b), Context, DvdMenuDisplay.Wide).Outcome);
        Assert.Null(nav.GetHighlight(Now));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedDisplayMasksCannotMakeARepeatOrDuplicateAmbiguous(bool duplicate)
    {
        var b = DvdNavFixture.Create(2); b[DvdNavFixture.Hli + 14] |= 3; // group 1: wide + letterbox; group 2: wide
        var packet = DvdNavigationPacket.Read(b); var nav = new DvdMenuNavigation();
        Assert.Equal(DvdMenuOutcome.Unchanged, nav.Load(packet, Context, DvdMenuDisplay.Letterbox).Outcome);
        if (!duplicate)
        {
            DvdNavFixture.Sector(b, 110); DvdNavFixture.U16(b, DvdNavFixture.Hli, 2);
            packet = DvdNavigationPacket.Read(b);
        }
        Assert.Equal(DvdMenuOutcome.Unsupported, nav.Load(packet, Context, DvdMenuDisplay.Wide).Outcome);
        Assert.Null(nav.GetHighlight(Now));
    }

    private static DvdMenuNavigation Load(byte[] bytes, DvdMenuDisplay display = DvdMenuDisplay.Normal, uint restrictions = 0)
    {
        var nav = new DvdMenuNavigation();
        Assert.Equal(DvdMenuOutcome.Unchanged, nav.Load(DvdNavigationPacket.Read(bytes), Context, display, restrictions).Outcome);
        return nav;
    }
}
