namespace NoirMediaPlayer.Services;

internal enum DvdMenuDomain { Manager, TitleSetMenu, Title }
// Epoch must change on seek, branch, restart or disc change. Coordinates are decoded
// subpicture coordinates; the eventual renderer must invert its aspect/viewport transform.
internal sealed record DvdMenuContext(DvdMenuDomain Domain, int TitleSet, int ProgramChain, long Epoch);
internal enum DvdMenuDirection { Up, Down, Left, Right }
internal enum DvdMenuOutcome { Unchanged, SelectionChanged, NoOperation, CommandRequiresCompatibility, Inactive, Prohibited, Unsupported, CommandExecuted }
internal sealed record DvdMenuAction(DvdMenuOutcome Outcome, int Button = 0, DvdButtonCommand? Command = null, string? Reason = null,
    DvdVmExecution? Execution = null, DvdVmState? InputState = null);
internal sealed record DvdButtonHighlight(int Button, DvdButtonBounds Bounds, uint ColorAndAlpha, bool Activated);

// First-party menu interaction and bounded VM execution. It never calls VLC, reads a
// disc, decodes video or moves a stream using an unresolved symbolic navigation target.
internal sealed class DvdMenuNavigation
{
    private DvdNavigationPacket? packet;
    private DvdMenuContext? context;
    private DvdMenuHighlight? menu;
    private DvdButtonGroup? group;
    private int groupIndex;
    private uint prohibitedOperations;
    private bool initialized, activated, commandPending;
    private DvdButtonCommand? pendingCommand;
    public int SelectedButton { get; private set; }
    public string? CompatibilityReason { get; private set; }
    public DvdVmState VmState { get; private set; }

    internal DvdMenuNavigation(DvdVmState? state = null) => VmState = state ?? new();

    internal DvdMenuAction Load(DvdNavigationPacket next, DvdMenuContext location, DvdMenuDisplay display, uint programChainProhibitions = 0)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(location);
        if (!Enum.IsDefined(location.Domain) || !Enum.IsDefined(display) || location.TitleSet is < 0 or > 99 ||
            location.ProgramChain < 1 || location.Epoch < 0)
            throw new ArgumentException("A valid DVD navigation context is required.", nameof(location));
        if (next.CompatibilityReason is not null) return Reject(next.CompatibilityReason);
        var update = next.Highlight.Update;
        if (update == DvdHighlightUpdate.None) { ResetInteraction(); return new(DvdMenuOutcome.Inactive); }
        var sameCell = context == location && packet is not null && packet.VobId == next.VobId && packet.CellId == next.CellId;
        if (sameCell && packet!.Sector == next.Sector && packet == next)
        {
            prohibitedOperations = programChainProhibitions | next.ProhibitedOperations;
            if (!MatchesCurrentGroup(display)) return Reject("A display change needs one unambiguous current menu group.");
            return new(DvdMenuOutcome.Unchanged, SelectedButton);
        }
        if (sameCell && packet!.Sector >= next.Sector)
            return Reject("Out-of-order or re-read navigation requires a new discontinuity epoch.");
        if (commandPending && context == location)
            return Reject("The pending DVD command needs a VM/backend handoff or a new navigation epoch.");
        if (update is DvdHighlightUpdate.Continue or DvdHighlightUpdate.CommandsOnly)
        {
            // Never resurrect an old menu after a seek/domain/cell change, or extend its
            // original timing using uninitialized fields from a repeat packet.
            if (!sameCell || menu is null || group is null || packet!.Sector >= next.Sector)
                return Reject("A repeated highlight needs an earlier menu in the same navigation context.");
            if (update == DvdHighlightUpdate.CommandsOnly)
            {
                var stride = 36 / menu.Groups.Count;
                group = group with { Buttons = Array.AsReadOnly(group.Buttons.Select((button, index) =>
                    button with { Command = next.Highlight.Commands[groupIndex * stride + index] }).ToArray()) };
            }
            packet = next;
            prohibitedOperations = programChainProhibitions | next.ProhibitedOperations;
            // A display change needs a fresh group selection; do not use old coordinates.
            if (!MatchesCurrentGroup(display)) return Reject("The repeated menu does not match one unambiguous current display group.");
            return new(DvdMenuOutcome.Unchanged, SelectedButton);
        }

        var matches = next.Highlight.Groups.Select((value, index) => (value, index)).Where(item => Matches(item.value.DisplayMask, display)).ToArray();
        if (matches.Length != 1) return Reject("The requested display mode needs one unambiguous button group.");
        var h = next.Highlight;
        if (!ValidEnd(h.Start90Khz, h.End90Khz) || !ValidEnd(h.Start90Khz, h.SelectionEnd90Khz) ||
            h.End90Khz != uint.MaxValue && (h.SelectionEnd90Khz == uint.MaxValue ||
                unchecked(h.SelectionEnd90Khz - h.Start90Khz) > unchecked(h.End90Khz - h.Start90Khz)))
            return Reject("The menu highlight timing is unsupported or inconsistent.");
        var previousSelection = sameCell ? SelectedButton : (VmState.System[8] ?? 0) >> 10;
        ResetInteraction();
        context = location;
        packet = next;
        menu = h;
        group = matches[0].value;
        groupIndex = matches[0].index;
        prohibitedOperations = programChainProhibitions | next.ProhibitedOperations;
        SelectedButton = h.ForcedSelection != 0 ? h.ForcedSelection : previousSelection >= 1 && previousSelection <= group.Buttons.Count ? previousSelection : 1;
        SyncHighlightRegister();
        return new(DvdMenuOutcome.Unchanged, SelectedButton);
    }

    internal void Reset()
    {
        ResetInteraction(); VmState = new();
    }

    private void ResetInteraction()
    {
        packet = null; context = null; menu = null; group = null;
        groupIndex = 0; prohibitedOperations = 0; initialized = activated = commandPending = false;
        pendingCommand = null;
        SelectedButton = 0; CompatibilityReason = null;
    }

    // Call on the presentation clock, including while a still menu is held. Authored
    // forced/automatic activation is emitted once, without bypassing it as a user action.
    internal DvdMenuAction Tick(uint time90Khz)
    {
        if (CompatibilityReason is not null) return new(DvdMenuOutcome.Unsupported, Reason: CompatibilityReason);
        if (!IsActive(time90Khz)) return new(DvdMenuOutcome.Inactive);
        if (commandPending) return new(DvdMenuOutcome.Unsupported, SelectedButton, Reason: "The emitted DVD command requires a VM/backend handoff before further input.");
        if (!initialized)
        {
            initialized = true;
            if (menu!.ForcedActivation != 0)
            {
                SelectedButton = menu.ForcedActivation;
                SyncHighlightRegister();
                return ActivateSelected();
            }
            if (CurrentButton.AutoActivate) return ActivateSelected();
        }
        return new(DvdMenuOutcome.Unchanged, SelectedButton);
    }

    internal DvdMenuAction Move(DvdMenuDirection direction, uint time90Khz)
    {
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        var pending = CheckInput(time90Khz, selecting: true);
        if (pending is not null) return pending;
        var button = CurrentButton;
        var target = direction switch { DvdMenuDirection.Up => button.Up, DvdMenuDirection.Down => button.Down,
            DvdMenuDirection.Left => button.Left, _ => button.Right };
        return target == 0 ? new(DvdMenuOutcome.Unchanged, SelectedButton) : Select(target);
    }

    internal DvdMenuAction SelectNumber(int number, uint time90Khz)
    {
        var pending = CheckInput(time90Khz, selecting: true);
        if (pending is not null) return pending;
        if (number < 1 || number > menu!.NumericSelectionCount) return new(DvdMenuOutcome.Prohibited, SelectedButton, Reason: "This button is not selectable by number.");
        return Select(number);
    }

    internal DvdMenuAction Point(int x, int y, uint time90Khz, bool activate = false)
    {
        var pending = CheckInput(time90Khz, selecting: true);
        if (pending is not null) return pending;
        var matches = group!.Buttons.Where(button => button.Bounds.Contains(x, y)).ToArray();
        if (matches.Length > 1) return new(DvdMenuOutcome.Unsupported, SelectedButton, Reason: "Overlapping button hit regions require compatibility handling.");
        if (matches.Length == 0) return new(DvdMenuOutcome.Unchanged, SelectedButton);
        var selection = Select(matches[0].Number);
        return activate && selection.Outcome is not (DvdMenuOutcome.NoOperation or DvdMenuOutcome.CommandRequiresCompatibility or DvdMenuOutcome.CommandExecuted)
            ? ActivateSelected() : selection;
    }

    internal DvdMenuAction Activate(uint time90Khz) => CheckInput(time90Khz, selecting: false) ?? ActivateSelected();

    // A metadata/VM proposal only. The caller must keep compatibility playback until
    // a complete native playback coordinator can accept and present this position.
    internal DvdDomainResolution PreparePendingNavigation(DvdDomainResolver resolver, DvdDomainState state) =>
        !commandPending || pendingCommand is null || context is null || packet is null
            ? new(DvdDomainStatus.CompatibilityRequired, state, 0, 0, "There is no pending DVD navigation command.")
            : resolver.PrepareMenuCommand(state, context, packet, pendingCommand, VmState);

    internal DvdButtonHighlight? GetHighlight(uint time90Khz)
    {
        if (!IsActive(time90Khz) || CurrentButton.ColorNumber == 0) return null;
        var colors = menu!.Colors[CurrentButton.ColorNumber - 1];
        return new(SelectedButton, CurrentButton.Bounds, activated ? colors.Activated : colors.Selected, activated);
    }

    private DvdMenuButton CurrentButton => group!.Buttons[SelectedButton - 1];
    private bool MatchesCurrentGroup(DvdMenuDisplay display) => group is not null && menu is not null &&
        Matches(group.DisplayMask, display) && menu.Groups.Count(candidate => Matches(candidate.DisplayMask, display)) == 1;
    private bool IsActive(uint time) => menu is not null && group is not null && InWindow(time, menu.Start90Khz, menu.End90Khz);
    private DvdMenuAction? CheckInput(uint time, bool selecting)
    {
        var tick = Tick(time);
        if (tick.Outcome != DvdMenuOutcome.Unchanged) return tick;
        if ((prohibitedOperations & (1u << 17)) != 0) return new(DvdMenuOutcome.Prohibited, SelectedButton, Reason: "Button selection/activation is prohibited by this DVD.");
        if (selecting && !InWindow(time, menu!.Start90Khz, menu.SelectionEnd90Khz)) return new(DvdMenuOutcome.Prohibited, SelectedButton, Reason: "The button selection period has ended.");
        return null;
    }
    private DvdMenuAction Select(int number)
    {
        if (number == SelectedButton) return new(DvdMenuOutcome.Unchanged, SelectedButton);
        SelectedButton = number; activated = false;
        SyncHighlightRegister();
        return CurrentButton.AutoActivate ? ActivateSelected() : new(DvdMenuOutcome.SelectionChanged, SelectedButton);
    }
    private DvdMenuAction ActivateSelected()
    {
        activated = true;
        var command = CurrentButton.Command;
        var input = VmState;
        var execution = DvdVirtualMachine.Execute([command], input);
        var completed = execution.Status is DvdVmStatus.Completed or DvdVmStatus.Break;
        var selectOnly = execution is { Status: DvdVmStatus.NavigationRequested, Navigation.Kind: DvdVmNavigationKind.SelectButton };
        var targetButton = selectOnly ? execution.Navigation!.Button : (execution.State.System[8] ?? 0) >> 10;
        if ((completed || selectOnly) && targetButton >= 1 && targetButton <= group!.Buttons.Count)
        {
            VmState = execution.State;
            if (targetButton != SelectedButton) { SelectedButton = targetButton; activated = false; SyncHighlightRegister(); }
            commandPending = false;
            pendingCommand = null;
            return new(command.Value == 0 ? DvdMenuOutcome.NoOperation : DvdMenuOutcome.CommandExecuted,
                SelectedButton, command, Execution: execution, InputState: input);
        }
        commandPending = true;
        pendingCommand = command;
        var reason = completed || selectOnly ? "The VM highlighted a button outside this menu group." :
            execution.Reason ?? $"DVD {execution.Navigation?.Kind} requires a validated domain/stream/return-stack resolver.";
        return new(DvdMenuOutcome.CommandRequiresCompatibility, SelectedButton, command, reason, execution, input);
    }
    private void SyncHighlightRegister() => VmState = VmState.WithSystem(8, (ushort)(SelectedButton << 10));
    private DvdMenuAction Reject(string reason)
    {
        ResetInteraction(); CompatibilityReason = reason;
        return new(DvdMenuOutcome.Unsupported, Reason: reason);
    }
    private static bool Matches(int mask, DvdMenuDisplay display) => display == DvdMenuDisplay.Normal ? mask == 0 :
        (mask & (display == DvdMenuDisplay.Wide ? 1 : display == DvdMenuDisplay.Letterbox ? 2 : 4)) != 0;
    private static bool ValidEnd(uint start, uint end) => end == uint.MaxValue || unchecked(end - start) is > 0 and <= int.MaxValue;
    private static bool InWindow(uint time, uint start, uint end) => unchecked(time - start) <= int.MaxValue &&
        (end == uint.MaxValue || unchecked(time - start) < unchecked(end - start));
}
