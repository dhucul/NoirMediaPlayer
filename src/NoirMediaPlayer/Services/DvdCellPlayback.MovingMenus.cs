namespace NoirMediaPlayer.Services;

internal sealed partial class DvdCellPlayback
{
    private readonly HashSet<Guid> movingCommands = new();
    private long movingCommandEpoch = -1;
    // A painted moving NAV is an observation inside this cell, never a cell-end
    // acknowledgement. Local effects keep the queued completion position; a
    // resolved branch replaces it and invalidates any old presentation ticket.
    internal DvdCellPlaybackResult MovingMenuCommand(DvdMovingMenuCommand request)
    {
        lock (sync)
        {
            if (fault.HasValue) return Snapshot();
            if (movingCommandEpoch != state.Epoch) { movingCommands.Clear(); movingCommandEpoch = state.Epoch; }
            if (movingCommands.Contains(request.Ticket)) return Result(DvdCellPlaybackStatus.Stale, "Moving DVD menu command was already committed.");
            var read = request.Read; var action = request.Action;
            if (pending is not null || state.Stopped || read.Status != DvdStreamStatus.Ready || read.Unit is null ||
                read.State.CatalogIdentity != state.CatalogIdentity || read.State.Epoch != state.Epoch ||
                read.State.Position.Chain != state.Position.Chain || read.State.Position.Cell != state.Position.Cell ||
                read.State.Position.Program != state.Position.Program || read.State.Position.Phase != DvdNavigationPhase.Cell ||
                read.State.Position.Sector != read.Unit.Navigation.Sector || action.Command is null || action.InputState is null)
                return Result(DvdCellPlaybackStatus.Stale, "Moving DVD menu command belongs to another cell/epoch or completion run.");
            if (!state.Registers.General.SequenceEqual(action.InputState.General))
                return Result(DvdCellPlaybackStatus.Stale, "Moving DVD menu command needs current pre-command registers.");
            if (movingCommands.Count == 4096) return Fail("Moving DVD menu command budget exceeded.");
            var observed = state with { Position = read.State.Position };
            var chain = resolver.StreamChain(observed);
            if (read.Unit.Cell != chain.Cells[state.Position.Cell - 1])
                return Result(DvdCellPlaybackStatus.Stale, "Moving DVD menu cell metadata changed.");
            var proposal = resolver.PrepareMenuCommand(observed, request.Context, read.Unit.Navigation, action.Command, action.InputState);
            if (proposal.Status != DvdDomainStatus.Resolved) return Fail(proposal.Reason ?? "Moving DVD command requires compatibility.", proposal);
            var moved = proposal.State.Position != observed.Position || proposal.State.Epoch != observed.Epoch || proposal.State.Stopped;
            movingCommands.Add(request.Ticket);
            state = moved ? proposal.State : state with { Registers = proposal.State.Registers };
            if (moved) ClearPending();
            return Result(moved ? state.Stopped ? DvdCellPlaybackStatus.Stopped : DvdCellPlaybackStatus.Advanced : DvdCellPlaybackStatus.Ready,
                instructions: proposal.InstructionsExecuted, transitions: proposal.Transitions);
        }
    }
    internal DvdCellPlaybackResult HeldMenuCommand(DvdMovingMenuCommand request)
    {
        lock (sync)
        {
            if (fault.HasValue) return Snapshot();
            if (movingCommandEpoch != state.Epoch) { movingCommands.Clear(); movingCommandEpoch = state.Epoch; }
            if (movingCommands.Contains(request.Ticket)) return Result(DvdCellPlaybackStatus.Stale, "DVD menu command was already committed.");
            var read = request.Read;
            if (pending is null || !presented || state.Stopped || read.Status != DvdStreamStatus.Ready || read.Unit is null ||
                read.State.CatalogIdentity != state.CatalogIdentity || read.State.Epoch != state.Epoch ||
                read.State.Position != state.Position || read.Unit.Navigation != pending.Unit!.Navigation ||
                request.Action.Command is null || request.Action.InputState is null)
                return Result(DvdCellPlaybackStatus.Stale, "Held DVD menu request belongs to another presentation/epoch.");
            if (movingCommands.Count == 4096) return Fail("DVD menu command budget exceeded.");
            var result = MenuCommand(request.Context, read.Unit.Navigation, request.Action.Command, request.Action.InputState);
            if (result.Status is DvdCellPlaybackStatus.Holding or DvdCellPlaybackStatus.Advanced or DvdCellPlaybackStatus.Stopped)
                movingCommands.Add(request.Ticket);
            return result;
        }
    }
    internal DvdCellPlaybackResult MovingMenuSelection(DvdVmState registers, bool held = false)
    {
        lock (sync)
        {
            if (fault.HasValue) return Snapshot();
            if ((held ? pending is null || !presented : pending is not null) || state.Stopped || !state.Registers.General.SequenceEqual(registers.General) ||
                !state.Registers.System.Where((_, i) => i != 8).SequenceEqual(registers.System.Where((_, i) => i != 8)))
                return Result(DvdCellPlaybackStatus.Stale, "Stale moving DVD menu selection.");
            var selected = registers.System[8];
            if (selected.HasValue && ((selected.Value & 1023) != 0 || selected.Value >> 10 > 36))
                return Result(DvdCellPlaybackStatus.Stale, "Invalid moving DVD menu highlight register.");
            if (selected.HasValue && state.Registers.System[8] != selected) state = state with { Registers = state.Registers.WithSystem(8, selected.Value) };
            return Snapshot();
        }
    }
}
