using System.IO;

namespace NoirMediaPlayer.Services;

internal sealed partial class DvdDomainResolver
{
    // Called by the completion gate only after presentation and still release.
    // This remains an atomic proposal: a later post/pre failure rolls back the
    // cell command's registers, resume slot and proposed stream movement too.
    internal DvdDomainResolution CompleteCell(DvdDomainState state, DvdStreamUnit unit,
        int instructionLimit = 4096, int transitionLimit = 64, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(unit);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ValidateBoundary(state, unit);
            var chain = Chain(state.Position.Chain);
            var number = unit.Cell.CommandNumber;
            Require(number <= chain.CellCommands.Count, "The DVD cell completion command is missing.");
            // cell_cmd_nr is a one-based selector, not a program counter into
            // the whole table. Goto is confined to this single selected row.
            IReadOnlyList<DvdButtonCommand> command = number == 0 ? [] : [chain.CellCommands[number - 1]];
            return Run(state, command, instructionLimit, transitionLimit, cancellationToken, completeCell: true);
        }
        catch (DomainFailure exception) { return new(DvdDomainStatus.CompatibilityRequired, state, 0, 0, exception.Message); }
    }

    internal DvdNavigationChain CompletionChain(DvdDomainState state, DvdStreamUnit unit)
    {
        try { ValidateBoundary(state, unit); return Chain(state.Position.Chain); }
        catch (DomainFailure exception) { throw new InvalidDataException(exception.Message, exception); }
    }

    internal void CheckState(DvdDomainState state)
    {
        try
        {
            Validate(state);
            Require(state.Stopped || state.Position.Phase == DvdNavigationPhase.Cell,
                "DVD presentation needs a resolved cell or stopped state, not pending command execution.");
        }
        catch (DomainFailure exception) { throw new InvalidDataException(exception.Message, exception); }
    }

    private void ValidateBoundary(DvdDomainState state, DvdStreamUnit unit)
    {
        Validate(state);
        Require(!state.Stopped && state.Position.Phase == DvdNavigationPhase.Cell,
            "Cell completion requires an active DVD cell.");
        var chain = Chain(state.Position.Chain); var cell = chain.Cells[state.Position.Cell - 1];
        var nav = unit.Navigation;
        Require(unit.Cell == cell && nav.Sector == state.Position.Sector && nav.VobId == cell.VobId && nav.CellId == cell.CellId &&
            (ulong)nav.Sector + nav.VobuEndRelativeSector == cell.LastSector &&
            (nav.NextVobuRaw & 0x7fffffff) == 0x3fffffff &&
            nav.CompatibilityReason is null && nav.TraversalCompatibilityReason is null &&
            nav.VobuEndRelativeSector < DvdVobSource.MaximumReadSectors && unit.ByteCount == ((long)nav.VobuEndRelativeSector + 1) * 2048,
            "The completed VOBU does not match the current DVD cell boundary.");
        var expected = new DvdCellBoundary(cell.StillSeconds, state.Position.Cell == chain.Cells.Count ? chain.StillSeconds : (byte)0,
            cell.CommandNumber, nav.Highlight.Update != DvdHighlightUpdate.None);
        Require(unit.Boundary == expected, "The pending DVD still/command boundary does not match its metadata.");
    }
}
