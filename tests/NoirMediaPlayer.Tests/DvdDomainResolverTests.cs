using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdDomainResolverTests
{
    private static readonly DvdPgcKey TitleKey = new(DvdPlaybackDomain.Title, 1, 0, 1);
    private static readonly DvdPgcKey ManagerKey = new(DvdPlaybackDomain.ManagerMenu, 0, 0x656e, 1);
    private static DvdVmState Registers => new DvdVmState().WithGeneral(0, 5).WithSystem(0, 0x656e).WithSystem(1, 1).WithSystem(8, 1 << 10);

    [Fact]
    public void FirstPlayAndTitleEntryRunTheirAuthoredPreCommandsWithoutChoosingAMovie() => WithCatalog((catalog, resolver) =>
    {
        var start = resolver.Start(Registers); Resolved(start, DvdPlaybackDomain.ManagerMenu, 10); Assert.Null(start.State.Title);
        var title = Run(resolver, start.State, "3002000000010000"); Resolved(title, DvdPlaybackDomain.Title, 100);
        Assert.Equal(7, title.State.Registers.General[0]); Assert.Equal(1, title.State.Title!.Number);
        Assert.Equal<ushort?>(1, title.State.Registers.System[4]); Assert.Equal<ushort?>(1, title.State.Registers.System[7]);
        Assert.Equal(5, start.State.Registers.General[0]); Assert.False(title.PlaybackVerified);
    }, titlePre: [0x7100000000070000]);

    [Theory]
    [InlineData("2001000000000001", 2, 200)] [InlineData("2001000000000002", 2, 300)]
    [InlineData("2001000000000003", 2, 100)] [InlineData("2001000000000005", 2, 100)]
    [InlineData("2001000000000006", 2, 300)] [InlineData("2001000000000007", 3, 100)]
    [InlineData("2006000000000002", 2, 300)] [InlineData("2007000000000003", 2, 300)]
    [InlineData("2005000000000002", 2, 300)]
    public void ResolvesLocalCellProgramAndChapterLinks(string command, int cell, uint sector) => WithCatalog((catalog, resolver) =>
    {
        var result = Run(resolver, TitleState(resolver, cell), command); Resolved(result, DvdPlaybackDomain.Title, sector);
        Assert.Equal(sector == 300 ? 2 : 1, result.State.Position.Program);
        Assert.Equal<ushort?>(sector == 300 ? (ushort)2 : (ushort)1, result.State.Registers.System[7]);
        Assert.Equal(5, result.State.Registers.General[0]); Assert.Equal(1, result.State.Epoch);
    });

    [Fact]
    public void ChapterLinkSkipsPreCommandsButTitleChapterJumpRunsThem() => WithCatalog((catalog, resolver) =>
    {
        var state = TitleState(resolver);
        var link = Run(resolver, state, "2005000000000002"); var jump = Run(resolver, state, "3005000200010000");
        Resolved(link, DvdPlaybackDomain.Title, 300); Resolved(jump, DvdPlaybackDomain.Title, 300);
        Assert.Equal(5, link.State.Registers.General[0]); Assert.Equal(12, jump.State.Registers.General[0]);
    }, titlePre: [0x7300000000070000]);

    [Theory]
    [InlineData("3008000000830000", "TitleSetMenu", 20, 2, 200)]
    [InlineData("3008000003420000", "ManagerMenu", 10, 3, 300)]
    [InlineData("3008000004000000", "ManagerMenu", 10, 4, 0)]
    public void CallsSaveOneResumeSlotAndResumeRestoresOnlyTitleRegisters(string call, string domain, uint menuSector, int resumeCell, uint sector) => WithCatalog((catalog, resolver) =>
    {
        var state = TitleState(resolver); var called = Run(resolver, state, call);
        if (resumeCell == 4) { Assert.Equal(DvdDomainStatus.CompatibilityRequired, called.Status); Assert.Same(state, called.State); return; }
        Assert.Equal(domain, called.State.Position.Chain.Domain.ToString()); Assert.Equal(menuSector, called.State.Position.Sector);
        Assert.Equal(resumeCell, called.State.Resume!.Position.Cell); Assert.Equal(sector, called.State.Resume.Position.Sector);
        var changed = called.State with { Registers = called.State.Registers.WithGeneral(0, 99).WithSystem(1, 7).WithSystem(8, 2 << 10) };
        var resumed = Run(resolver, changed, "2001000000000010"); Resolved(resumed, DvdPlaybackDomain.Title, sector);
        Assert.Equal(99, resumed.State.Registers.General[0]); Assert.Equal<ushort?>(7, resumed.State.Registers.System[1]);
        Assert.Equal<ushort?>(1 << 10, resumed.State.Registers.System[8]);
        Assert.Equal<ushort?>(resumeCell == 3 ? (ushort)2 : (ushort)1, resumed.State.Registers.System[7]);
        Assert.Same(called.State.Resume, resumed.State.Resume); Assert.Null(state.Resume); Assert.Equal(5, state.Registers.General[0]);
    });

    [Fact]
    public void MenuHopsPreserveResumeAndASecondTitleCallReplacesRatherThanPushesIt() => WithCatalog((catalog, resolver) =>
    {
        var called = Run(resolver, TitleState(resolver), "3008000000830000");
        var hopped = Run(resolver, called.State, "3006000000420000"); Assert.Same(called.State.Resume, hopped.State.Resume);
        var resumed = Run(resolver, hopped.State, "2001000000000810"); Resolved(resumed, DvdPlaybackDomain.Title, 200);
        Assert.Equal<ushort?>(2 << 10, resumed.State.Registers.System[8]);
        var next = Run(resolver, resumed.State, "3008000001420000");
        Assert.Equal(1, next.State.Resume!.Position.Cell); Assert.NotSame(called.State.Resume, next.State.Resume);
        Resolved(Run(resolver, next.State, "2001000000000010"), DvdPlaybackDomain.Title, 100);
        var nested = Run(resolver, called.State, "3008000000420000");
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, nested.Status); Assert.Same(called.State, nested.State);
    });

    [Theory]
    [InlineData("3006000000420000")] [InlineData("3002000000010000")]
    [InlineData("3006000101830000")] [InlineData("2007000000000009")]
    [InlineData("2001000000000010")]
    public void IllegalTitleTransfersAndMissingTargetsNeverChangeState(string command) => WithCatalog((catalog, resolver) =>
    {
        var state = TitleState(resolver); var result = Run(resolver, state, "7100000000630000", command);
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, result.Status); Assert.Same(state, result.State);
        Assert.Equal(5, result.State.Registers.General[0]); Assert.NotNull(result.Reason);
    });

    [Fact]
    public void LocalTitleSetScopeRelativeMenusAndGlobalTitlesStayDistinct() => WithCatalog((catalog, resolver) =>
    {
        var manager = resolver.ObserveCell(ManagerKey, 1, 10, Registers);
        var setTwo = Run(resolver, manager, "3006000102830000"); Resolved(setTwo, DvdPlaybackDomain.TitleSetMenu, 20);
        Assert.Equal(2, setTwo.State.Position.Chain.TitleSet);
        var titleTwo = Run(resolver, setTwo.State, "3003000000010000"); Resolved(titleTwo, DvdPlaybackDomain.Title, 100);
        Assert.Equal(2, titleTwo.State.Title!.Number);
        var relative = Run(resolver, setTwo.State, "3006000100830000"); Resolved(relative, DvdPlaybackDomain.TitleSetMenu, 20);
        Assert.Equal(2, relative.State.Position.Chain.TitleSet);
        var cross = Run(resolver, setTwo.State, "3006000101830000");
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, cross.Status); Assert.Same(setTwo.State, cross.State);
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, Run(resolver, manager, "3003000000010000").Status);
    }, secondSet: true);

    [Theory]
    [InlineData("200100000000000A")] [InlineData("2004000000000002")] [InlineData("200100000000000D")]
    public void PgcLinksAndTailResolveAuthoredLinksAndPostCommands(string command) => WithCatalog((catalog, resolver) =>
    {
        var state = resolver.ObserveCell(ManagerKey, 1, 10, Registers);
        var result = Run(resolver, state, command); Resolved(result, DvdPlaybackDomain.ManagerMenu, 30);
        Assert.Equal(2, result.State.Position.Chain.Number);
        Resolved(Run(resolver, result.State, "200100000000000B"), DvdPlaybackDomain.ManagerMenu, 10);
        Resolved(Run(resolver, result.State, "200100000000000C"), DvdPlaybackDomain.ManagerMenu, 10);
        var stop = Run(resolver, result.State, "200100000000000D");
        Assert.Equal(DvdDomainStatus.Resolved, stop.Status); Assert.True(stop.State.Stopped);
    });

    [Fact]
    public void PostCommandsRunBeforeTheDefaultNextPgc() => WithCatalog((catalog, resolver) =>
    {
        var state = resolver.ObserveCell(ManagerKey, 1, 10, Registers);
        Resolved(Run(resolver, state, "200100000000000D"), DvdPlaybackDomain.Title, 100);
    }, managerPost: [0x3002000000010000]);

    [Fact]
    public void AmbiguousLanguagesRequireAnExplicitAvailablePlayerLanguage() => WithCatalog((catalog, resolver) =>
    {
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, resolver.Start(new DvdVmState()).Status);
        var french = resolver.Start(new DvdVmState().WithSystem(0, 0x6672)); Resolved(french, DvdPlaybackDomain.ManagerMenu, 10);
        Assert.Equal(0x6672, french.State.Position.Chain.Language);
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, resolver.Start(new DvdVmState().WithSystem(0, 0x6465)).Status);
    }, extraLanguage: true);

    [Fact]
    public void FailedDestinationPreCommandsRollBackTheResumeSlotAndEveryRegister() => WithCatalog((catalog, resolver) =>
    {
        var state = TitleState(resolver); var result = Run(resolver, state, "7100000000630000", "3008000000830000");
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, result.Status); Assert.Same(state, result.State);
        Assert.Null(result.State.Resume); Assert.Equal(5, result.State.Registers.General[0]);
    }, rootPre: [0xe000000000000000]);

    [Fact]
    public void PgcCyclesShareBudgetsAndDiscardIntermediateEffects() => WithCatalog((catalog, resolver) =>
    {
        var state = resolver.ObserveCell(ManagerKey, 1, 10, Registers);
        var result = resolver.Execute(state, [Command("2001000000000009")], transitionLimit: 5);
        Assert.Equal(DvdDomainStatus.Limit, result.Status); Assert.Same(state, result.State);
        var budget = resolver.Execute(state, [Command("2001000000000009")], instructionLimit: 3);
        Assert.Equal(DvdDomainStatus.Limit, budget.Status); Assert.Same(state, budget.State);
    }, managerPre: [0x7300000000010000, 0x3006000000420000]);

    [Fact]
    public void StatesCannotCrossCatalogsAndCancellationHasNoSideEffects() => WithCatalog((catalog, resolver) =>
    {
        var state = TitleState(resolver);
        var other = new DvdDomainResolver(new DvdNavigationCatalog(catalog.VideoTsDirectory, catalog.MetadataFiles, catalog.Chains, catalog.Titles));
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, Run(other, state, "0000000000000000").Status);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => resolver.Execute(state, [], cancellationToken: cancelled.Token));
        Assert.Equal(5, state.Registers.General[0]);
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, Run(resolver, state with { Epoch = -1 }, "0000000000000000").Status);
    });

    [Fact]
    public void PendingMenuNavigationIsOnlyAProposalAndStaleEpochsAreRejected() => WithCatalog((catalog, resolver) =>
    {
        var state = resolver.ObserveCell(ManagerKey, 1, 10, Registers);
        var bytes = DvdNavFixture.Create(); DvdNavFixture.Sector(bytes, 10); bytes[DvdNavFixture.Dsi + 27] = 1;
        DvdNavFixture.Command(bytes, 0, 0x3002000000010000);
        var menu = new DvdMenuNavigation(state.Registers);
        menu.Load(DvdNavigationPacket.Read(bytes), new(DvdMenuDomain.Manager, 0, 1, state.Epoch), DvdMenuDisplay.Normal);
        Assert.Equal(DvdMenuOutcome.CommandRequiresCompatibility, menu.Activate(100_000).Outcome);
        Resolved(menu.PreparePendingNavigation(resolver, state), DvdPlaybackDomain.Title, 100);
        Assert.Equal(DvdPlaybackDomain.ManagerMenu, state.Position.Chain.Domain); Assert.Equal(1, menu.SelectedButton);
        Assert.Equal(DvdMenuOutcome.Unsupported, menu.Tick(100_000).Outcome);
        var stale = state with { Epoch = 1 }; var rejected = menu.PreparePendingNavigation(resolver, stale);
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, rejected.Status); Assert.Same(stale, rejected.State);
    });

    [Theory]
    [InlineData("3008000001000000", 10, 1)]
    [InlineData("3008000200C00000", 30, 2)]
    public void CallsToFirstPlayAndExplicitManagerPgcsKeepTheTitleReturnPoint(string command, uint sector, int cell) => WithCatalog((catalog, resolver) =>
    {
        var called = Run(resolver, TitleState(resolver), command); Resolved(called, DvdPlaybackDomain.ManagerMenu, sector);
        var resumed = Run(resolver, called.State, "2001000000000010");
        Resolved(resumed, DvdPlaybackDomain.Title, (uint)(100 + (cell - 1) * 100));
    });

    [Fact]
    public void MissingResumeAndInvalidCellProgramPositionsFailWithoutInventingTargets() => WithCatalog((catalog, resolver) =>
    {
        var menu = resolver.ObserveCell(ManagerKey, 1, 10, Registers);
        var missing = Run(resolver, menu, "2001000000000010");
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, missing.Status); Assert.Same(menu, missing.State);
        var state = TitleState(resolver);
        foreach (var invalid in new[] { state with { Position = state.Position with { Sector = 900 } },
            state with { Position = state.Position with { Program = 2 } }, state with { Epoch = long.MaxValue } })
        {
            var result = Run(resolver, invalid, "2007000000000001");
            Assert.Equal(DvdDomainStatus.CompatibilityRequired, result.Status); Assert.Same(invalid, result.State);
        }
    });

    [Fact]
    public void AWholeTitleJumpCannotSilentlySkipAnUnmappedIntroProgram() => DvdNavigationCatalogTests.WithDisc(root =>
    {
        DvdDomainFixture.Write(root);
        DvdFixture.Edit(root, "VTS_01_0.IFO", bytes => DvdFixture.U16(bytes, DvdFixture.PartTable + 14, 2));
        var catalog = DvdNavigationCatalog.Read(root); var resolver = new DvdDomainResolver(catalog);
        var state = resolver.ObserveCell(ManagerKey, 1, 10, Registers);
        var result = Run(resolver, state, "3002000000010000");
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, result.Status); Assert.Same(state, result.State);
    });

    [Fact]
    public void UnsupportedCellModesAndParentalDataRemainOnCompatibility() => DvdNavigationCatalogTests.WithDisc(root =>
    {
        DvdDomainFixture.Write(root);
        DvdFixture.Edit(root, "VTS_01_0.IFO", bytes => bytes[DvdFixture.Chain + DvdFixture.Playback] = 0x40);
        var catalog = DvdNavigationCatalog.Read(root); var resolver = new DvdDomainResolver(catalog);
        var state = resolver.ObserveCell(ManagerKey, 1, 10, Registers);
        var result = Run(resolver, state, "3002000000010000");
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, result.Status); Assert.Same(state, result.State);
    });

    private static DvdDomainState TitleState(DvdDomainResolver resolver, int cell = 2) => resolver.ObserveCell(TitleKey, cell, (uint)(100 + (cell - 1) * 100 + 15), Registers, 1);
    private static DvdButtonCommand Command(string value) => new(Convert.ToUInt64(value, 16));
    private static DvdDomainResolution Run(DvdDomainResolver resolver, DvdDomainState state, params string[] commands) => resolver.Execute(state, commands.Select(Command).ToArray());
    private static void Resolved(DvdDomainResolution result, DvdPlaybackDomain domain, uint sector)
    {
        Assert.Equal(DvdDomainStatus.Resolved, result.Status); Assert.Null(result.Reason);
        Assert.Equal(domain, result.State.Position.Chain.Domain); Assert.Equal(sector, result.State.Position.Sector);
        Assert.Equal(DvdNavigationPhase.Cell, result.State.Position.Phase); Assert.False(result.State.Stopped);
    }
    private static void WithCatalog(Action<DvdNavigationCatalog, DvdDomainResolver> test, ulong[]? titlePre = null,
        ulong[]? managerPre = null, ulong[]? managerPost = null, ulong[]? rootPre = null, bool extraLanguage = false, bool secondSet = false) =>
        DvdNavigationCatalogTests.WithDisc(root =>
        {
            DvdDomainFixture.Write(root, titlePre, managerPre, managerPost, rootPre, extraLanguage, secondSet);
            var catalog = DvdNavigationCatalog.Read(root); test(catalog, new(catalog));
        });
}
