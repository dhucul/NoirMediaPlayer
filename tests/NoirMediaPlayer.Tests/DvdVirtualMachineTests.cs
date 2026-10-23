using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdVirtualMachineTests
{
    [Theory]
    [InlineData("7100000000070000", 123, 7)]
    [InlineData("7300000000070000", 10, 17)]
    [InlineData("7300000000070000", 65534, 65535)]
    [InlineData("7400000000070000", 3, 0)]
    [InlineData("7400000000070000", 10, 3)]
    [InlineData("75000000FFFF0000", 65535, 65535)]
    [InlineData("7500000000070000", 10, 70)]
    [InlineData("7600000000070000", 20, 2)]
    [InlineData("7600000000000000", 20, 65535)]
    [InlineData("7700000000070000", 20, 6)]
    [InlineData("7700000000000000", 20, 65535)]
    [InlineData("79000000000F0000", 51, 3)]
    [InlineData("7A000000000F0000", 48, 63)]
    [InlineData("7B000000000F0000", 51, 60)]
    public void ExecutesWordArithmeticWithDvdSaturationAndZeroDivisorRules(string hex, ushort initial, ushort expected)
    {
        var state = new DvdVmState().WithGeneral(0, initial);
        var result = Run(state, hex);
        Assert.Equal(DvdVmStatus.Completed, result.Status); Assert.Equal(expected, result.State.General[0]);
        Assert.Equal(initial, state.General[0]); Assert.Equal(1, result.InstructionsExecuted);
    }

    [Fact]
    public void CopiesSwapsAndReadsKnownSystemParametersWithoutMutatingInputs()
    {
        var g = new ushort[16]; g[1] = 9; var s = new ushort?[24]; s[0] = 0x656e;
        var state = new DvdVmState(g, s); Array.Clear(g); Array.Clear(s);
        var copy = Run(state, "6100000000010000");
        Assert.Equal(9, copy.State.General[0]);
        var swap = Run(copy.State.WithGeneral(0, 3), "6200000000010000");
        Assert.Equal(9, swap.State.General[0]); Assert.Equal(3, swap.State.General[1]);
        Assert.Equal(0x656e, Run(state, "6100000000800000").State.General[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<ushort>)state.General)[0] = 4);
    }

    [Theory]
    [InlineData(1, 1, 5, true)] [InlineData(1, 2, 5, false)]
    [InlineData(2, 5, 5, true)] [InlineData(2, 4, 5, false)]
    [InlineData(3, 4, 5, true)] [InlineData(3, 5, 5, false)]
    [InlineData(4, 5, 5, true)] [InlineData(4, 4, 5, false)]
    [InlineData(5, 6, 5, true)] [InlineData(5, 5, 5, false)]
    [InlineData(6, 5, 5, true)] [InlineData(6, 6, 5, false)]
    [InlineData(7, 4, 5, true)] [InlineData(7, 5, 5, false)]
    public void AllComparisonOperatorsGateTheRegisterWrite(int comparison, ushort left, ushort right, bool taken)
    {
        // Family 3: immediate assignment to g0, compare g1 with an immediate word.
        var command = 0x7100010000070000UL | ((ulong)(0x80 | comparison << 4) << 48) | right;
        var state = new DvdVmState().WithGeneral(0, 99).WithGeneral(1, left);
        var result = DvdVirtualMachine.Execute([new(command)], state);
        Assert.Equal(DvdVmStatus.Completed, result.Status); Assert.Equal(taken ? 7 : 99, result.State.General[0]);
    }

    [Fact]
    public void ConditionsReadRegisterOperandsAndPlayerParametersOnlyWhenKnown()
    {
        var state = new DvdVmState().WithGeneral(1, 5).WithGeneral(2, 5);
        Assert.Equal(7, Run(state, "7120010000070002").State.General[0]);
        Assert.Equal(DvdVmStatus.CompatibilityRequired, Run(state, "71A080000007656E").Status);
        state = state.WithSystem(0, 0x656e);
        Assert.Equal(7, Run(state, "71A080000007656E").State.General[0]);
    }

    [Fact]
    public void ExecutesForwardAndBackwardGotoAndStopsAtBreak()
    {
        var state = new DvdVmState();
        var forward = Run(state, "7100000000010000", "00A1000000010004", "71000001FFFF0000", "7100000200070000");
        Assert.Equal(DvdVmStatus.Completed, forward.Status); Assert.Equal(0, forward.State.General[1]);
        Assert.Equal(7, forward.State.General[2]); Assert.Equal(3, forward.InstructionsExecuted);
        var loop = Run(state, "7300000000010000", "00F1000000030001");
        Assert.Equal(3, loop.State.General[0]); Assert.Equal(6, loop.InstructionsExecuted);
        var stop = Run(state, "7100000000050000", "0002000000000000", "7100000000630000");
        Assert.Equal(DvdVmStatus.Break, stop.Status); Assert.Equal(5, stop.State.General[0]); Assert.Equal(2, stop.Line);
    }

    [Fact]
    public void StepExhaustionUnsupportedRowsAndCancellationNeverCommitPartialState()
    {
        var state = new DvdVmState().WithGeneral(0, 12);
        var exhausted = DvdVirtualMachine.Execute([Command("7300000000010000"), Command("0001000000000001")], state, 5);
        Assert.Equal(DvdVmStatus.StepLimit, exhausted.Status); Assert.Same(state, exhausted.State); Assert.Equal(5, exhausted.InstructionsExecuted);
        var unsupported = Run(state, "7100000000070000", "E000000000000000");
        Assert.Equal(DvdVmStatus.CompatibilityRequired, unsupported.Status); Assert.Same(state, unsupported.State); Assert.Equal(2, unsupported.Line);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => DvdVirtualMachine.Execute([], state, cancellationToken: cancelled.Token));
        Assert.Equal(12, state.General[0]);
    }

    [Theory]
    [InlineData("0001000000000000")] [InlineData("0001000000000002")]
    [InlineData("6100000000100000")] [InlineData("6100000000950000")]
    [InlineData("6100000000800000")] [InlineData("7100001000010000")]
    [InlineData("71A0011000070005")] [InlineData("0000000000000001")]
    [InlineData("7800000000070000")] [InlineData("5300000000000000")]
    [InlineData("7101000000010000")] [InlineData("8000000000000000")]
    [InlineData("7200000000010000")] [InlineData("6200000000800000")]
    [InlineData("2001000000000004")] [InlineData("2001000000009400")]
    [InlineData("3002000000000000")] [InlineData("3006000000410000")]
    public void InvalidReservedUnknownAndUnimplementedInstructionsRequireCompatibility(string hex)
    {
        var state = new DvdVmState(); var result = Run(state, hex);
        Assert.Equal(DvdVmStatus.CompatibilityRequired, result.Status); Assert.Same(state, result.State);
        Assert.NotNull(result.Reason); Assert.Null(result.Navigation);
    }

    [Theory]
    [InlineData("3002000000070000", "GlobalTitle", 7, 0)]
    [InlineData("3003000000020000", "TitleSetTitle", 2, 0)]
    [InlineData("3005000300020000", "TitleSetChapter", 3, 0)]
    [InlineData("2004000000000009", "ProgramChain", 9, 0)]
    [InlineData("2005000000000803", "Chapter", 3, 2)]
    [InlineData("2006000000000803", "Program", 3, 2)]
    [InlineData("2007000000000803", "Cell", 3, 2)]
    [InlineData("2001000000000800", "SelectButton", 0, 2)]
    [InlineData("2001000000000802", "NextCell", 0, 2)]
    [InlineData("2001000000000010", "Resume", 0, 0)]
    [InlineData("3001000000000000", "Exit", 0, 0)]
    [InlineData("3006000000000000", "FirstPlay", 0, 0)]
    [InlineData("3006000000420000", "ManagerMenu", 2, 0)]
    [InlineData("3006000201830000", "TitleSetMenu", 3, 0)]
    [InlineData("3006000900C00000", "ManagerProgramChain", 9, 0)]
    [InlineData("3008000004000000", "CallFirstPlay", 0, 0)]
    [InlineData("3008000004420000", "CallManagerMenu", 2, 0)]
    [InlineData("3008000004830000", "CallTitleSetMenu", 3, 0)]
    [InlineData("3008000904C00000", "CallManagerProgramChain", 9, 0)]
    public void ProducesSymbolicNavigationTargetsWithoutChoosingOrOpeningMedia(string hex, string kind, int target, int button)
    {
        var state = new DvdVmState(); var result = Run(state, hex, "7100000000630000");
        Assert.Equal(DvdVmStatus.NavigationRequested, result.Status); Assert.Equal(1, result.InstructionsExecuted);
        Assert.Equal(kind, result.Navigation!.Kind.ToString()); Assert.Equal(target, result.Navigation.Target);
        Assert.Equal(button, result.Navigation.Button); Assert.Equal(0, result.State.General[0]);
        if (kind == "TitleSetChapter") Assert.Equal(2, result.Navigation.Title);
        if (kind == "TitleSetMenu") { Assert.Equal(1, result.Navigation.TitleSet); Assert.Equal(2, result.Navigation.Title); }
        if (kind.StartsWith("Call")) Assert.Equal(4, result.Navigation.ResumeCell);
    }

    [Fact]
    public void ConditionalJumpsCompareRegistersAndDoNotReturnSkippedTargets()
    {
        var state = new DvdVmState().WithGeneral(1, 5).WithGeneral(2, 6);
        Assert.Equal(DvdVmStatus.Completed, Run(state, "3022000000070102").Status);
        Assert.Equal(DvdVmStatus.NavigationRequested, Run(state.WithGeneral(2, 5), "3022000000070102").Status);
        Assert.Equal(DvdVmStatus.Completed, Run(state, "20A4000000010009").Status); // g0 != 1: skipped LinkPGCN
        Assert.Equal(DvdVmStatus.NavigationRequested, Run(state.WithGeneral(0, 1), "20A4000000010009").Status);
        Assert.Equal(DvdVmStatus.Completed, Run(state, "2001000000000000").Status);
    }

    [Fact]
    public void AControlTransferOnlyProposesRegisterChangesForTheFutureResolver()
    {
        var state = new DvdVmState().WithGeneral(0, 12);
        var result = Run(state, "7100000000070000", "3002000000010000");
        Assert.Equal(DvdVmStatus.NavigationRequested, result.Status);
        Assert.Equal(7, result.State.General[0]); Assert.Equal(12, state.General[0]);
    }

    [Fact]
    public void HighlightWritesSupportImmediateAndGeneralRegisterSources()
    {
        var state = new DvdVmState().WithGeneral(1, 2 << 10);
        Assert.Equal<ushort?>(2 << 10, Run(state, "5600000008000000").State.System[8]);
        Assert.Equal<ushort?>(2 << 10, Run(state, "4600000000010000").State.System[8]);
        Assert.Equal(DvdVmStatus.CompatibilityRequired, Run(state, "5600000008010000").Status);
        Assert.Equal(DvdVmStatus.CompatibilityRequired, Run(state, "5600000094000000").Status);
    }

    [Fact]
    public void InputTablesAndBudgetsAreBounded()
    {
        var state = new DvdVmState();
        Assert.Equal(DvdVmStatus.Completed, DvdVirtualMachine.Execute([], state).Status);
        Assert.Throws<ArgumentException>(() => DvdVirtualMachine.Execute(Enumerable.Repeat(new DvdButtonCommand(0), 256).ToArray(), state));
        Assert.Throws<ArgumentException>(() => DvdVirtualMachine.Execute([null!], state));
        Assert.Throws<ArgumentOutOfRangeException>(() => DvdVirtualMachine.Execute([], state, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DvdVirtualMachine.Execute([], state, 16385));
        Assert.Throws<ArgumentException>(() => new DvdVmState(new ushort[15]));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.WithSystem(21, 0));
    }

    private static DvdButtonCommand Command(string hex) => new(Convert.ToUInt64(hex, 16));
    private static DvdVmExecution Run(DvdVmState state, params string[] commands) => DvdVirtualMachine.Execute(commands.Select(Command).ToArray(), state);
}
