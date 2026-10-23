using System.IO;

namespace NoirMediaPlayer.Services;

internal enum DvdCellPlaybackStatus { Ready, AwaitingPresentation, Holding, Advanced, Stopped, Prohibited, Stale, CompatibilityRequired, Limit }
internal sealed record DvdCellPlaybackResult(DvdCellPlaybackStatus Status, DvdDomainState State,
    Guid? Ticket = null, TimeSpan? Remaining = null, bool Indefinite = false, bool Paused = false,
    int InstructionsExecuted = 0, int Transitions = 0, string? Reason = null)
{
    public bool PlaybackVerified => false;
}

// A serialized, first-party presentation/completion model, not a decoder or live
// backend. The host must acknowledge the last displayed frame and drained audio
// before a still clock starts. It must continue processing menu input while held.
// No timer callback, delay or wall clock mutates the navigation state on its own.
internal sealed partial class DvdCellPlayback
{
    private readonly object sync = new();
    private readonly DvdDomainResolver resolver;
    private readonly TimeProvider clock;
    private DvdStreamRead? pending;
    private Guid? ticket;
    private bool presented, paused, indefinite;
    private TimeSpan remaining;
    private long timestamp;
    private DvdCellPlaybackStatus? fault;
    private string? faultReason;
    private DvdDomainState state;
    internal DvdDomainState State { get { lock (sync) return state; } }

    internal DvdCellPlayback(DvdNavigationCatalog catalog, DvdDomainState initial, TimeProvider? timeProvider = null)
    {
        resolver = new(catalog); resolver.CheckState(initial);
        state = initial; clock = timeProvider ?? TimeProvider.System;
    }

    internal DvdCellPlaybackResult Queue(DvdStreamRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (sync)
        {
            if (fault.HasValue) return Snapshot();
            if (pending is not null || read.State != state) return Result(DvdCellPlaybackStatus.Stale, "The VOBU is not the current unqueued DVD state.");
            if (read.Status != DvdStreamStatus.Ready || read.Unit is null) return Fail(read.Reason ?? "The DVD VOBU is not ready.");
            try
            {
                var unit = read.Unit; var chain = resolver.StreamChain(state); var cell = chain.Cells[state.Position.Cell - 1];
                var nav = unit.Navigation;
                if (unit.Cell != cell || nav.Sector != state.Position.Sector || nav.VobId != cell.VobId || nav.CellId != cell.CellId ||
                    nav.CompatibilityReason is not null || nav.TraversalCompatibilityReason is not null ||
                    nav.VobuEndRelativeSector >= DvdVobSource.MaximumReadSectors ||
                    unit.ByteCount != ((long)nav.VobuEndRelativeSector + 1) * 2048)
                    return Fail("The queued DVD unit does not match its current cell/NAV position.");
                if (unit.Boundary is not null)
                {
                    resolver.CompletionChain(state, unit);
                    if (read.Next is not null) return Fail("A DVD boundary cannot have a same-cell continuation.");
                    var boundary = unit.Boundary;
                    indefinite = boundary.CellStillSeconds == 255 || boundary.ProgramChainStillSeconds == 255;
                    remaining = indefinite ? TimeSpan.Zero : TimeSpan.FromSeconds(boundary.CellStillSeconds + boundary.ProgramChainStillSeconds);
                }
                else
                {
                    var distance = nav.NextVobuRaw & 0x3fffffff;
                    var next = (ulong)nav.Sector + distance;
                    if ((nav.NextVobuRaw & 0xc0000000) != 0x80000000 || distance == 0x3fffffff || distance <= nav.VobuEndRelativeSector ||
                        next > cell.LastSector || read.Next != state with { Position = state.Position with { Sector = (uint)next } })
                        return Fail("The queued DVD unit has an invalid same-cell continuation.");
                    indefinite = false; remaining = TimeSpan.Zero;
                }
                pending = read; ticket = Guid.NewGuid(); presented = false;
                return Snapshot();
            }
            catch (InvalidDataException exception) { return Fail(exception.Message); }
        }
    }

    internal DvdCellPlaybackResult Presented(Guid completedTicket, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ticket != completedTicket || pending is null) return Result(DvdCellPlaybackStatus.Stale, "Stale DVD presentation acknowledgement.");
            if (fault.HasValue || presented) return Snapshot();
            presented = true; timestamp = clock.GetTimestamp();
            if (pending.Unit!.Boundary is null)
            {
                state = pending.Next!; ClearPending(); return Result(DvdCellPlaybackStatus.Advanced);
            }
            return !indefinite && remaining == TimeSpan.Zero ? Complete(cancellationToken) : Snapshot();
        }
    }

    internal DvdCellPlaybackResult Tick(Guid activeTicket, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ticket != activeTicket || pending is null) return Result(DvdCellPlaybackStatus.Stale, "Stale DVD hold tick.");
            if (fault.HasValue || !presented) return Snapshot();
            if (!AccountTime()) return Snapshot();
            return !indefinite && remaining == TimeSpan.Zero ? Complete(cancellationToken) : Snapshot();
        }
    }

    internal DvdCellPlaybackResult SetPaused(bool value)
    {
        lock (sync)
        {
            if (fault.HasValue) return Snapshot();
            if (value && !paused && pending is not null && (Prohibitions() & (1u << 19)) != 0)
                return Result(DvdCellPlaybackStatus.Prohibited, "Pausing is prohibited by this DVD.");
            if (presented && !AccountTime()) return Snapshot();
            paused = value; return Snapshot();
        }
    }

    internal DvdCellPlaybackResult SkipStill(Guid activeTicket, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ticket != activeTicket || pending is null) return Result(DvdCellPlaybackStatus.Stale, "Stale DVD still release.");
            if (fault.HasValue || !presented) return Snapshot();
            var domain = state.Position.Chain.Domain;
            if ((Prohibitions() & (1u << 18)) != 0 || domain == DvdPlaybackDomain.FirstPlay ||
                domain is DvdPlaybackDomain.ManagerMenu or DvdPlaybackDomain.TitleSetMenu && pending.Unit!.Boundary!.HasHighlight)
                return Result(DvdCellPlaybackStatus.Prohibited, "This DVD still requires its authored menu action or prohibits Still_Off.");
            return Complete(cancellationToken);
        }
    }

    // Bind menu input to the held NAV/epoch. Pass DvdMenuAction.InputState, never
    // its already executed result: preparing the same command from its input
    // commits local effects once. Local effects keep the original hold clock;
    // navigation cancels the old hold and all of its presentation tickets.
    internal DvdCellPlaybackResult MenuCommand(DvdMenuContext context, DvdNavigationPacket packet,
        DvdButtonCommand command, DvdVmState beforeCommand)
    {
        lock (sync)
        {
            if (fault.HasValue) return Snapshot();
            if (pending is null || !presented || pending.Unit!.Navigation != packet)
                return Result(DvdCellPlaybackStatus.Stale, "DVD menu input is not bound to the presented held NAV packet.");
            if (!state.Registers.General.SequenceEqual(beforeCommand.General))
                return Result(DvdCellPlaybackStatus.Stale, "DVD menu input must supply the current pre-command general registers.");
            var proposal = resolver.PrepareMenuCommand(state, context, packet, command, beforeCommand);
            if (proposal.Status != DvdDomainStatus.Resolved) return Fail(proposal.Reason ?? "DVD menu command requires compatibility.", proposal);
            var moved = proposal.State.Position != state.Position || proposal.State.Epoch != state.Epoch || proposal.State.Stopped;
            state = proposal.State;
            if (moved) ClearPending();
            return moved ? Result(state.Stopped ? DvdCellPlaybackStatus.Stopped : DvdCellPlaybackStatus.Advanced,
                instructions: proposal.InstructionsExecuted, transitions: proposal.Transitions) :
                Result(DvdCellPlaybackStatus.Holding, instructions: proposal.InstructionsExecuted, transitions: proposal.Transitions);
        }
    }

    // Only accepted discontinuity proposals may replace the position. Equal/older
    // epochs cannot re-arm a completed unit or revive a stale timer after a seek.
    internal DvdCellPlaybackResult ReplaceState(DvdDomainState replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (sync)
        {
            try { resolver.CheckState(replacement); }
            catch (InvalidDataException exception) { return Result(DvdCellPlaybackStatus.Stale, exception.Message); }
            if (replacement.Epoch <= state.Epoch) return Result(DvdCellPlaybackStatus.Stale, "A DVD discontinuity requires a newer navigation epoch.");
            state = replacement; ClearPending(); fault = null; faultReason = null;
            return Result(state.Stopped ? DvdCellPlaybackStatus.Stopped : DvdCellPlaybackStatus.Advanced);
        }
    }

    private DvdCellPlaybackResult Complete(CancellationToken cancellationToken)
    {
        var proposal = resolver.CompleteCell(state, pending!.Unit!, cancellationToken: cancellationToken);
        if (proposal.Status != DvdDomainStatus.Resolved) return Fail(proposal.Reason ?? "DVD cell completion requires compatibility.", proposal);
        state = proposal.State; ClearPending();
        return Result(state.Stopped ? DvdCellPlaybackStatus.Stopped : DvdCellPlaybackStatus.Advanced,
            instructions: proposal.InstructionsExecuted, transitions: proposal.Transitions);
    }

    private bool AccountTime()
    {
        var now = clock.GetTimestamp(); var elapsed = clock.GetElapsedTime(timestamp, now);
        if (elapsed < TimeSpan.Zero) { Fail("The DVD hold clock moved backwards."); return false; }
        timestamp = now;
        if (!paused && !indefinite) remaining -= elapsed < remaining ? elapsed : remaining;
        return true;
    }
    private uint Prohibitions() => resolver.StreamChain(state).ProhibitedOperations | pending!.Unit!.Navigation.ProhibitedOperations;
    private void ClearPending() { pending = null; ticket = null; presented = indefinite = false; remaining = TimeSpan.Zero; }
    private DvdCellPlaybackResult Fail(string reason, DvdDomainResolution? proposal = null)
    {
        fault = proposal?.Status == DvdDomainStatus.Limit ? DvdCellPlaybackStatus.Limit : DvdCellPlaybackStatus.CompatibilityRequired;
        faultReason = reason;
        return Result(fault.Value, reason, proposal?.InstructionsExecuted ?? 0, proposal?.Transitions ?? 0);
    }
    private DvdCellPlaybackResult Snapshot() => Result(fault ?? (state.Stopped ? DvdCellPlaybackStatus.Stopped :
        pending is null ? DvdCellPlaybackStatus.Ready : !presented ? DvdCellPlaybackStatus.AwaitingPresentation : DvdCellPlaybackStatus.Holding), faultReason);
    private DvdCellPlaybackResult Result(DvdCellPlaybackStatus status, string? reason = null, int instructions = 0, int transitions = 0) =>
        new(status, state, ticket, pending is null || indefinite ? null : remaining, indefinite, paused, instructions, transitions, reason);
}
