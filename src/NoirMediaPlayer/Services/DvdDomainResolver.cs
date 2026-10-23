using System.IO;

namespace NoirMediaPlayer.Services;

internal enum DvdNavigationPhase { PreCommands, Cell, PostCommands }
internal sealed record DvdNavigationPosition(DvdPgcKey Chain, DvdNavigationPhase Phase, int Program, int Cell, uint Sector);

// DVD has one saved title-resume slot, not a nested call stack. Only title SPRMs 4..8
// are saved; GPRMs and other player parameters changed in menus survive a resume.
internal sealed class DvdResumePoint(Guid catalog, DvdNavigationPosition position, DvdNavigationTitle title, DvdVmState registers)
{
    public Guid CatalogIdentity { get; } = catalog;
    public DvdNavigationPosition Position { get; } = position;
    public DvdNavigationTitle Title { get; } = title;
    public IReadOnlyList<ushort?> TitleRegisters { get; } = Array.AsReadOnly(registers.System.Skip(4).Take(5).ToArray());
}
internal sealed record DvdDomainState(Guid CatalogIdentity, DvdNavigationPosition Position, DvdVmState Registers,
    DvdNavigationTitle? Title = null, DvdResumePoint? Resume = null, bool Stopped = false, long Epoch = 0);
internal enum DvdDomainStatus { Resolved, CompatibilityRequired, Limit }
internal sealed record DvdDomainResolution(DvdDomainStatus Status, DvdDomainState State, int InstructionsExecuted,
    int Transitions, string? Reason = null)
{
    public bool PlaybackVerified => false;
}

// Pure proposals backed by one immutable metadata snapshot. No stream is opened or
// repositioned, and failures return the original state including its resume slot.
internal sealed partial class DvdDomainResolver
{
    private readonly DvdNavigationCatalog catalog;
    private readonly Dictionary<DvdPgcKey, DvdNavigationChain> chains;
    internal DvdDomainResolver(DvdNavigationCatalog catalog)
    {
        this.catalog = catalog;
        chains = catalog.Chains.ToDictionary(chain => chain.Key);
    }

    internal DvdDomainState ObserveCell(DvdPgcKey key, int cell, uint sector, DvdVmState registers, int globalTitle = 0)
    {
        var chain = Chain(key);
        var title = globalTitle == 0 ? null : Title(globalTitle);
        var position = CellPosition(chain, cell, sector);
        var state = Normalize(new(catalog.Identity, position, registers, title));
        Validate(state); return state;
    }

    internal DvdDomainResolution Start(DvdVmState? registers = null)
    {
        var key = new DvdPgcKey(DvdPlaybackDomain.FirstPlay, 0, 0, 1);
        var state = new DvdDomainState(catalog.Identity, new(key, DvdNavigationPhase.PreCommands, 1, 0, 0), registers ?? new());
        return Run(state, null, 4096, 64, default);
    }

    internal DvdDomainResolution Execute(DvdDomainState state, IReadOnlyList<DvdButtonCommand> commands,
        int instructionLimit = 4096, int transitionLimit = 64, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(commands);
        return Run(state, commands, instructionLimit, transitionLimit, cancellationToken);
    }

    // Shared validation for the read-only stream layer; it must not bypass title
    // context, canonical program/cell positions or disc snapshot checks.
    internal DvdNavigationChain StreamChain(DvdDomainState state)
    {
        try
        {
            Validate(state);
            Require(!state.Stopped && state.Position.Phase == DvdNavigationPhase.Cell,
                "DVD stream traversal requires an active cell position.");
            return Chain(state.Position.Chain);
        }
        catch (DomainFailure exception) { throw new InvalidDataException(exception.Message, exception); }
    }

    internal DvdDomainResolution PrepareMenuCommand(DvdDomainState state, DvdMenuContext context,
        DvdNavigationPacket packet, DvdButtonCommand command, DvdVmState menuRegisters)
    {
        try
        {
            Validate(state);
            var domain = context.Domain switch { DvdMenuDomain.Manager => DvdPlaybackDomain.ManagerMenu,
                DvdMenuDomain.TitleSetMenu => DvdPlaybackDomain.TitleSetMenu, _ => DvdPlaybackDomain.Title };
            Require(!state.Stopped && state.Position.Phase == DvdNavigationPhase.Cell && context.Epoch == state.Epoch &&
                context.TitleSet == state.Position.Chain.TitleSet && context.ProgramChain == state.Position.Chain.Number &&
                domain == state.Position.Chain.Domain && packet.Sector == state.Position.Sector,
                "The pending DVD menu command no longer matches the domain position/epoch.");
            var cell = Chain(state.Position.Chain).Cells[state.Position.Cell - 1];
            Require(cell.VobId == packet.VobId && cell.CellId == packet.CellId, "The pending menu command belongs to another DVD VOB/cell.");
            Require(state.Registers.System.Where((_, i) => i != 8).SequenceEqual(menuRegisters.System.Where((_, i) => i != 8)),
                "Menu player parameters do not match the supplied DVD domain state.");
            var proposal = Execute(state with { Registers = menuRegisters }, [command]);
            return proposal.Status == DvdDomainStatus.Resolved ? proposal : proposal with { State = state };
        }
        catch (DomainFailure exception) { return new(DvdDomainStatus.CompatibilityRequired, state, 0, 0, exception.Message); }
    }

    private DvdDomainResolution Run(DvdDomainState original, IReadOnlyList<DvdButtonCommand>? commands,
        int instructionLimit, int transitionLimit, CancellationToken cancellationToken, bool completeCell = false)
    {
        if (instructionLimit is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(instructionLimit));
        if (transitionLimit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(transitionLimit));
        var state = original; var instructions = 0; var transitions = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested(); Validate(state);
                if (state.Stopped || commands is null && state.Position.Phase == DvdNavigationPhase.Cell)
                    return new(DvdDomainStatus.Resolved, state, instructions, transitions);
                if (transitions == transitionLimit) return new(DvdDomainStatus.Limit, original, instructions, transitions, "DVD domain transition limit reached; proposal discarded.");
                var chain = Chain(state.Position.Chain);
                if (chain.CompatibilityReason is not null) throw new DomainFailure(chain.CompatibilityReason);
                var phaseTable = commands is null;
                var table = commands ?? (state.Position.Phase == DvdNavigationPhase.PreCommands ? chain.PreCommands : chain.PostCommands);
                if (instructions == instructionLimit && table.Count != 0)
                    return new(DvdDomainStatus.Limit, original, instructions, transitions, "DVD domain instruction limit reached; proposal discarded.");
                var execution = DvdVirtualMachine.Execute(table, state.Registers, Math.Max(1, instructionLimit - instructions), cancellationToken);
                instructions += execution.InstructionsExecuted;
                if (execution.Status is DvdVmStatus.CompatibilityRequired or DvdVmStatus.StepLimit)
                    return new(execution.Status == DvdVmStatus.StepLimit ? DvdDomainStatus.Limit : DvdDomainStatus.CompatibilityRequired,
                        original, instructions, transitions, execution.Reason);
                state = state with { Registers = execution.State };
                commands = null; transitions++;
                if (execution.Navigation is { } navigation)
                {
                    if (navigation.Kind != DvdVmNavigationKind.SelectButton)
                    {
                        state = Resolve(state, navigation); completeCell = false; continue;
                    }
                    state = Highlight(state, navigation.Button);
                }
                if (completeCell)
                {
                    // Falling through the selected cell command advances once;
                    // an authored link/call above already chose its destination.
                    Require(state.Epoch < long.MaxValue, "DVD navigation epoch exhausted.");
                    state = state with { Epoch = state.Epoch + 1 };
                    state = state.Position.Cell == chain.Cells.Count ? Post(state) : AtCell(state, state.Position.Cell + 1);
                    completeCell = false; continue;
                }
                if (!phaseTable) return new(DvdDomainStatus.Resolved, state, instructions, transitions);
                if (state.Position.Phase == DvdNavigationPhase.PreCommands)
                    state = chain.Cells.Count == 0 ? Post(state) : AtCell(state, chain.Programs[state.Position.Program - 1]);
                else state = chain.Next == 0 ? state with { Stopped = true } : Enter(state, chain.Key with { Number = chain.Next });
            }
        }
        catch (DomainFailure exception)
        {
            return new(DvdDomainStatus.CompatibilityRequired, original, instructions, transitions, exception.Message);
        }
    }

    private DvdDomainState Resolve(DvdDomainState state, DvdVmNavigation nav)
    {
        var key = state.Position.Chain; var chain = Chain(key);
        var domain = key.Domain;
        // The VM has validated encoding fields; resolve their legality and references
        // in this domain without opening or repositioning a playback stream.
        if (nav.Button is < 0 or > 36 || nav.ResumeCell is < 0 or > 255) throw new DomainFailure("Invalid DVD navigation button/resume cell.");
        Require(state.Epoch < long.MaxValue, "DVD navigation epoch exhausted.");
        state = state with { Epoch = state.Epoch + 1 };
        state = Highlight(state, nav.Button);
        switch (nav.Kind)
        {
            case DvdVmNavigationKind.TopCell: return AtCell(state, CurrentCell(state));
            case DvdVmNavigationKind.PreviousCell: return AtCell(state, CurrentCell(state) - 1);
            case DvdVmNavigationKind.NextCell:
                return CurrentCell(state) == chain.Cells.Count ? Post(state) : AtCell(state, state.Position.Cell + 1);
            case DvdVmNavigationKind.Cell: return AtCell(state, nav.Target);
            case DvdVmNavigationKind.TopProgram: return AtProgram(state, state.Position.Program);
            case DvdVmNavigationKind.PreviousProgram: return AtProgram(state, state.Position.Program - 1);
            case DvdVmNavigationKind.NextProgram:
                return state.Position.Program == chain.Programs.Count ? Post(state) : AtProgram(state, state.Position.Program + 1);
            case DvdVmNavigationKind.Program: return AtProgram(state, nav.Target);
            case DvdVmNavigationKind.TopProgramChain: return Enter(state, key);
            case DvdVmNavigationKind.NextProgramChain: return Enter(state, key with { Number = chain.Next });
            case DvdVmNavigationKind.PreviousProgramChain: return Enter(state, key with { Number = chain.Previous });
            case DvdVmNavigationKind.GoUpProgramChain: return Enter(state, key with { Number = chain.GoUp });
            case DvdVmNavigationKind.ProgramChain: return Enter(state, key with { Number = nav.Target });
            case DvdVmNavigationKind.TailProgramChain: return Post(state);
            case DvdVmNavigationKind.Chapter:
                Require(domain == DvdPlaybackDomain.Title, "Chapter links require the title domain.");
                return EnterTitle(state, state.Title!, nav.Target, false);
            case DvdVmNavigationKind.GlobalTitle:
                Require(domain is DvdPlaybackDomain.FirstPlay or DvdPlaybackDomain.ManagerMenu, "Global title jumps require first-play/manager-menu domain.");
                return EnterTitle(state, Title(nav.Target), 1, true, true);
            case DvdVmNavigationKind.TitleSetTitle:
            case DvdVmNavigationKind.TitleSetChapter:
                Require(domain is DvdPlaybackDomain.Title or DvdPlaybackDomain.TitleSetMenu, "Local title jumps require the current title set.");
                return EnterTitle(state, LocalTitle(key.TitleSet, nav.Kind == DvdVmNavigationKind.TitleSetTitle ? nav.Target : nav.Title),
                    nav.Kind == DvdVmNavigationKind.TitleSetTitle ? 1 : nav.Target, true, nav.Kind == DvdVmNavigationKind.TitleSetTitle);
            case DvdVmNavigationKind.FirstPlay:
                Require(domain is DvdPlaybackDomain.ManagerMenu or DvdPlaybackDomain.TitleSetMenu, "First-play jumps require a menu domain.");
                return Enter(state, new(DvdPlaybackDomain.FirstPlay, 0, 0, 1));
            case DvdVmNavigationKind.ManagerMenu:
            case DvdVmNavigationKind.ManagerProgramChain:
                Require(domain != DvdPlaybackDomain.Title, "Title-domain menu transfers must use CallSS.");
                return Enter(state, MenuKey(state, DvdPlaybackDomain.ManagerMenu, 0, nav.Target, nav.Kind == DvdVmNavigationKind.ManagerMenu));
            case DvdVmNavigationKind.TitleSetMenu:
                Require(domain != DvdPlaybackDomain.Title, "Title-domain menu transfers must use CallSS.");
                var set = nav.TitleSet == 0 ? key.TitleSet : nav.TitleSet;
                Require(nav.TitleSet != 0 || domain == DvdPlaybackDomain.TitleSetMenu, "A relative VTS-menu jump requires that menu domain.");
                Require(domain != DvdPlaybackDomain.TitleSetMenu || set == key.TitleSet, "Cross-VTS menu jumps from VTSM are not supported.");
                var title = LocalTitle(set, nav.Title);
                state = state with { Title = title, Registers = state.Registers.WithSystem(4, (ushort)title.Number).WithSystem(5, (ushort)title.TitleInSet) };
                return Enter(state, MenuKey(state, DvdPlaybackDomain.TitleSetMenu, set, nav.Target, true));
            case DvdVmNavigationKind.CallFirstPlay:
            case DvdVmNavigationKind.CallManagerMenu:
            case DvdVmNavigationKind.CallTitleSetMenu:
            case DvdVmNavigationKind.CallManagerProgramChain:
                Require(domain == DvdPlaybackDomain.Title, "DVD calls save one title resume slot; nested/menu calls are not legal.");
                var resumeCell = nav.ResumeCell != 0 ? nav.ResumeCell : state.Position.Cell != 0 ? state.Position.Cell : 1;
                var resumePosition = CellPosition(chain, resumeCell, null);
                state = state with { Resume = new(catalog.Identity, resumePosition, state.Title!, state.Registers) };
                var destination = nav.Kind == DvdVmNavigationKind.CallFirstPlay ? new DvdPgcKey(DvdPlaybackDomain.FirstPlay, 0, 0, 1) :
                    MenuKey(state, nav.Kind == DvdVmNavigationKind.CallTitleSetMenu ? DvdPlaybackDomain.TitleSetMenu : DvdPlaybackDomain.ManagerMenu,
                        nav.Kind == DvdVmNavigationKind.CallTitleSetMenu ? key.TitleSet : 0, nav.Target,
                        nav.Kind != DvdVmNavigationKind.CallManagerProgramChain);
                return Enter(state, destination);
            case DvdVmNavigationKind.Resume:
                Require(domain is DvdPlaybackDomain.FirstPlay or DvdPlaybackDomain.ManagerMenu or DvdPlaybackDomain.TitleSetMenu, "Resume requires a system/menu domain.");
                var saved = state.Resume ?? throw new DomainFailure("No DVD title resume point has been saved.");
                Require(saved.CatalogIdentity == catalog.Identity, "The saved DVD resume point belongs to another disc snapshot.");
                var system = state.Registers.System.ToArray();
                for (var i = 0; i < 5; i++) system[4 + i] = saved.TitleRegisters[i];
                state = Normalize(state with { Position = saved.Position, Title = saved.Title, Registers = new(state.Registers.General, system) });
                Validate(state); return Highlight(state, nav.Button);
            case DvdVmNavigationKind.Exit: return state with { Stopped = true };
            default: throw new DomainFailure("This DVD domain operation is not implemented.");
        }
    }

    private DvdDomainState EnterTitle(DvdDomainState state, DvdNavigationTitle title, int chapter, bool pre, bool wholeTitle = false)
    {
        GuardTitle(title);
        Require(chapter >= 1 && chapter <= title.Chapters.Count, "The requested DVD chapter does not exist.");
        var part = title.Chapters[chapter - 1];
        Require(!wholeTitle || part.Program == 1, "Whole-title entry before the first chapter needs an unmapped intro-program policy.");
        var key = new DvdPgcKey(DvdPlaybackDomain.Title, title.TitleSet, 0, part.ProgramChain);
        state = state with { Title = title };
        return pre ? Enter(state, key, part.Program) : AtCell(state with { Position = state.Position with { Chain = key } }, part.FirstCell);
    }

    private DvdDomainState Enter(DvdDomainState state, DvdPgcKey key, int program = 1)
    {
        var chain = Chain(key);
        Require(chain.CompatibilityReason is null, chain.CompatibilityReason ?? "Unsupported DVD program chain.");
        Require(program >= 1 && program <= Math.Max(1, chain.Programs.Count), "Missing DVD entry program.");
        return Normalize(state with { Position = new(key, DvdNavigationPhase.PreCommands, program, 0, 0) });
    }
    private DvdDomainState AtProgram(DvdDomainState state, int program)
    {
        var chain = Chain(state.Position.Chain);
        Require(program >= 1 && program <= chain.Programs.Count, "Missing DVD program.");
        return AtCell(state, chain.Programs[program - 1]);
    }
    private DvdDomainState AtCell(DvdDomainState state, int cell) => Normalize(state with { Position = CellPosition(Chain(state.Position.Chain), cell, null) });
    private static DvdDomainState Post(DvdDomainState state) => state with { Position = state.Position with { Phase = DvdNavigationPhase.PostCommands, Program = 0, Cell = 0, Sector = 0 } };
    private static int CurrentCell(DvdDomainState state)
    {
        Require(state.Position.Phase == DvdNavigationPhase.Cell, "Cell-relative DVD links need a current cell."); return state.Position.Cell;
    }
    private DvdNavigationPosition CellPosition(DvdNavigationChain chain, int number, uint? sector)
    {
        Require(number >= 1 && number <= chain.Cells.Count, "Missing DVD cell.");
        var cell = chain.Cells[number - 1]; var address = sector ?? cell.FirstSector;
        Require(address >= cell.FirstSector && address <= cell.LastSector, "DVD sector is outside its authored cell.");
        var program = chain.Programs.TakeWhile(first => first <= number).Count();
        return new(chain.Key, DvdNavigationPhase.Cell, program, number, address);
    }
    private DvdDomainState Normalize(DvdDomainState state)
    {
        if (state.Position.Chain.Domain != DvdPlaybackDomain.Title) return state;
        var title = state.Title ?? throw new DomainFailure("The DVD title context is unknown."); GuardTitle(title);
        Require(title.TitleSet == state.Position.Chain.TitleSet, "The DVD title context belongs to another title set.");
        var program = state.Position.Program;
        if (state.Position.Phase == DvdNavigationPhase.PostCommands) return state;
        var parts = title.Chapters.Where(part => part.ProgramChain == state.Position.Chain.Number && part.Program <= program).ToArray();
        Require(parts.Length != 0, "The program does not have a known chapter in this DVD title.");
        var lastProgram = parts.Max(part => part.Program);
        var matches = parts.Where(part => part.Program == lastProgram).ToArray();
        Require(matches.Length == 1, "The DVD chapter context is ambiguous.");
        var registers = state.Registers.WithSystem(4, (ushort)title.Number).WithSystem(5, (ushort)title.TitleInSet)
            .WithSystem(6, (ushort)state.Position.Chain.Number).WithSystem(7, (ushort)matches[0].Number);
        return state with { Registers = registers };
    }
    private DvdPgcKey MenuKey(DvdDomainState state, DvdPlaybackDomain domain, int set, int target, bool entry)
    {
        var choices = catalog.Chains.Where(chain => chain.Key.Domain == domain && chain.Key.TitleSet == set).ToArray();
        var languages = choices.Select(chain => chain.Key.Language).Distinct().ToArray();
        var preferred = state.Registers.System[0];
        var language = preferred.HasValue && languages.Contains(preferred.Value) ? preferred.Value :
            languages.Length == 1 ? languages[0] : throw new DomainFailure("Missing or ambiguous DVD menu language; an explicit available language is required.");
        var matches = choices.Where(chain => chain.Key.Language == language && (entry ? chain.EntryId == (0x80 | target) : chain.Key.Number == target)).ToArray();
        Require(target > 0 && matches.Length == 1, "Missing or ambiguous DVD menu entry/PGC."); return matches[0].Key;
    }
    private DvdNavigationChain Chain(DvdPgcKey key) => chains.TryGetValue(key, out var chain) ? chain : throw new DomainFailure("The requested DVD domain/program chain is absent.");
    private DvdNavigationTitle Title(int number) => catalog.Titles.SingleOrDefault(title => title.Number == number) ?? throw new DomainFailure("Missing DVD global title.");
    private DvdNavigationTitle LocalTitle(int set, int local)
    {
        var matches = catalog.Titles.Where(title => title.TitleSet == set && title.TitleInSet == local).ToArray();
        Require(matches.Length == 1, "Missing or ambiguous DVD title-set title."); return matches[0];
    }
    private static void GuardTitle(DvdNavigationTitle title) => Require(title.Angles == 1 && title.ParentalId == 0 && (title.PlaybackFlags & 0xc0) == 0,
        "Angle, random/multiple-PGC or parental title navigation requires compatibility.");
    private static DvdDomainState Highlight(DvdDomainState state, int button)
    {
        Require(button is >= 0 and <= 36, "Invalid DVD highlight button.");
        return button == 0 ? state : state with { Registers = state.Registers.WithSystem(8, (ushort)(button << 10)) };
    }
    private void Validate(DvdDomainState state)
    {
        Require(state.CatalogIdentity == catalog.Identity, "DVD navigation state belongs to another disc snapshot.");
        Require(state.Epoch >= 0, "Invalid DVD navigation epoch.");
        var chain = Chain(state.Position.Chain);
        Require(chain.CompatibilityReason is null, chain.CompatibilityReason ?? "Unsupported DVD program chain.");
        if (state.Position.Phase == DvdNavigationPhase.Cell)
            Require(CellPosition(chain, state.Position.Cell, state.Position.Sector) == state.Position, "Inconsistent DVD cell/program position.");
        else if (state.Position.Phase == DvdNavigationPhase.PreCommands)
            Require(state.Position.Cell == 0 && state.Position.Sector == 0 && state.Position.Program >= 1 && state.Position.Program <= Math.Max(1, chain.Programs.Count), "Invalid DVD pre-command position.");
        else Require(state.Position.Phase == DvdNavigationPhase.PostCommands && state.Position.Cell == 0 && state.Position.Program == 0 && state.Position.Sector == 0, "Invalid DVD post-command position.");
        if (state.Title is not null) Require(catalog.Titles.Contains(state.Title), "DVD title context belongs to another metadata snapshot.");
        if (state.Position.Chain.Domain == DvdPlaybackDomain.Title)
            Require(Normalize(state).Registers.System.SequenceEqual(state.Registers.System), "DVD title registers do not match the current position.");
    }
    private static void Require(bool condition, string reason) { if (!condition) throw new DomainFailure(reason); }
    private sealed class DomainFailure(string message) : Exception(message);
}
