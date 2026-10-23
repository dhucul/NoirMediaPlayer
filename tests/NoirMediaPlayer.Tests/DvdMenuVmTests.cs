using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdMenuVmTests
{
    private static readonly DvdMenuContext Context = new(DvdMenuDomain.TitleSetMenu, 1, 1, 0);
    private const uint Now = 100_000;

    [Fact]
    public void ButtonRegisterCommandsExecuteLocallyAndRegistersSurviveMenuAndDomainChanges()
    {
        var bytes = DvdNavFixture.Create(); DvdNavFixture.Command(bytes, 0, 0x7300000000010000);
        var nav = Load(bytes);
        Assert.Equal(DvdMenuOutcome.CommandExecuted, nav.Activate(Now).Outcome);
        Assert.Equal(1, nav.VmState.General[0]); Assert.Equal(DvdMenuOutcome.Unchanged, nav.Tick(Now).Outcome);
        Assert.Equal(DvdMenuOutcome.CommandExecuted, nav.Activate(Now).Outcome); Assert.Equal(2, nav.VmState.General[0]);
        DvdNavFixture.Sector(bytes, 110);
        nav.Load(DvdNavigationPacket.Read(bytes), Context with { ProgramChain = 2, Epoch = 1 }, DvdMenuDisplay.Normal);
        Assert.Equal(2, nav.VmState.General[0]);
        DvdNavFixture.U16(bytes, DvdNavFixture.Hli, 0);
        nav.Load(DvdNavigationPacket.Read(bytes), Context, DvdMenuDisplay.Normal);
        Assert.Equal(2, nav.VmState.General[0]); nav.Reset(); Assert.Equal(0, nav.VmState.General[0]);
    }

    [Theory]
    [InlineData("5600000008000000")] [InlineData("2001000000000800")]
    public void VmHighlightAndSelectOnlyLinkMoveTheCurrentButtonWithoutAPlaybackHandoff(string hex)
    {
        var b = DvdNavFixture.Create(); DvdNavFixture.Command(b, 0, Convert.ToUInt64(hex, 16));
        var nav = Load(b); var action = nav.Activate(Now);
        Assert.Equal(DvdMenuOutcome.CommandExecuted, action.Outcome); Assert.Equal(2, nav.SelectedButton);
        Assert.Equal<ushort?>(2 << 10, nav.VmState.System[8]); Assert.False(nav.GetHighlight(Now)!.Activated);
        Assert.Equal(DvdMenuOutcome.NoOperation, nav.Activate(Now).Outcome);
    }

    [Fact]
    public void AnOutOfGroupHighlightRollsBackAndRequiresCompatibility()
    {
        var b = DvdNavFixture.Create(); DvdNavFixture.Command(b, 0, 0x560000000C000000);
        var nav = Load(b); var before = nav.VmState; var action = nav.Activate(Now);
        Assert.Equal(DvdMenuOutcome.CommandRequiresCompatibility, action.Outcome);
        Assert.Same(before, nav.VmState); Assert.Equal(1, nav.SelectedButton); Assert.Equal<ushort?>(1 << 10, nav.VmState.System[8]);
        Assert.Equal(DvdMenuOutcome.Unsupported, nav.Activate(Now).Outcome);
    }

    [Fact]
    public void PlayerAndCurrentHighlightRegistersAreAvailableToButtonConditionsAndCopies()
    {
        var b = DvdNavFixture.Create(); DvdNavFixture.Command(b, 0, 0x6100000000800000);
        DvdNavFixture.Command(b, 1, 0x6100000000880000);
        var nav = Load(b, new DvdVmState().WithSystem(0, 0x656e));
        Assert.Equal(DvdMenuOutcome.CommandExecuted, nav.Activate(Now).Outcome);
        Assert.Equal(0x656e, nav.VmState.General[0]); nav.Move(DvdMenuDirection.Right, Now);
        Assert.Equal<ushort?>(2 << 10, nav.VmState.System[8]); nav.Activate(Now);
        Assert.Equal(2 << 10, nav.VmState.General[0]);
        var preselected = Load(b, new DvdVmState().WithSystem(8, 2 << 10));
        Assert.Equal(2, preselected.SelectedButton);
    }

    [Theory]
    [InlineData("6100000000800000", "CompatibilityRequired")]
    [InlineData("0001000000000001", "StepLimit")]
    [InlineData("E000000000000000", "CompatibilityRequired")]
    [InlineData("3002000000070000", "NavigationRequested")]
    public void UnreadyCommandsPreserveVmStateAndExposeTheExactExecutionResult(string hex, string status)
    {
        var b = DvdNavFixture.Create(); DvdNavFixture.Command(b, 0, Convert.ToUInt64(hex, 16));
        var nav = Load(b, new DvdVmState().WithGeneral(0, 12)); var before = nav.VmState;
        var action = nav.Activate(Now);
        Assert.Equal(DvdMenuOutcome.CommandRequiresCompatibility, action.Outcome); Assert.Same(before, nav.VmState);
        Assert.Equal(hex, action.Command!.Hex); Assert.Equal(status, action.Execution!.Status.ToString());
        Assert.Equal(DvdMenuOutcome.Unsupported, nav.Tick(Now).Outcome);
        if (status == "NavigationRequested")
        {
            Assert.Equal(DvdVmNavigationKind.GlobalTitle, action.Execution.Navigation!.Kind);
            Assert.Equal(7, action.Execution.Navigation.Target);
        }
    }

    [Fact]
    public void MouseClickDoesNotExecuteAnAutomaticRegisterCommandTwice()
    {
        var b = DvdNavFixture.Create(); b[DvdNavFixture.ButtonOffset(1) + 3] |= 0x40;
        DvdNavFixture.Command(b, 1, 0x7300000000010000); var nav = Load(b);
        Assert.Equal(DvdMenuOutcome.CommandExecuted, nav.Point(60, 20, Now, activate: true).Outcome);
        Assert.Equal(1, nav.VmState.General[0]); nav.Tick(Now); Assert.Equal(1, nav.VmState.General[0]);
    }

    [Fact]
    public void ForcedRegisterCommandRunsOnlyOnceAndUserProhibitionsStillApply()
    {
        var b = DvdNavFixture.Create(); b[DvdNavFixture.Hli + 21] = 1;
        DvdNavFixture.Command(b, 0, 0x7300000000010000);
        var nav = Load(b, restrictions: 1u << 17);
        Assert.Equal(DvdMenuOutcome.CommandExecuted, nav.Tick(Now).Outcome);
        Assert.Equal(1, nav.VmState.General[0]); Assert.Equal(DvdMenuOutcome.Unchanged, nav.Tick(Now).Outcome);
        Assert.Equal(DvdMenuOutcome.Prohibited, nav.Activate(Now).Outcome); Assert.Equal(1, nav.VmState.General[0]);
    }

    private static DvdMenuNavigation Load(byte[] bytes, DvdVmState? state = null, uint restrictions = 0)
    {
        var nav = new DvdMenuNavigation(state);
        Assert.Equal(DvdMenuOutcome.Unchanged, nav.Load(DvdNavigationPacket.Read(bytes), Context, DvdMenuDisplay.Normal, restrictions).Outcome);
        return nav;
    }
}
