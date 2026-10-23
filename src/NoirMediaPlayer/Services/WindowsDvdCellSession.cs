using System.Runtime.InteropServices;

namespace NoirMediaPlayer.Services;

internal enum DvdNativeStatus { Loading, Playing, Holding, CellComplete, CompatibilityRequired, Stopped }
internal sealed record DvdNativeState(DvdNativeStatus Status, DvdDomainState Navigation, long DecodedFrames = 0, string? Reason = null)
{
    public bool DiscPlaybackVerified => false;
}
internal sealed record DvdNativePresentation(bool Ready, bool Ended, long Frames, bool Painted, int Error, string? Reason = null,
    DvdMovingMenuCommand? MenuCommand = null, DvdVmState? MenuRegisters = null);
internal interface IDvdCellRenderer : IDisposable
{
    void Open(DvdCellComStream stream, int audioStreams, DvdSubpictureTiming? subpictures = null);
    DvdNativePresentation Poll();
    void Pause(bool paused);
    void SetSubpicture(DvdSubpictureFrame? frame, IReadOnlyList<uint> palette, DvdButtonHighlight? highlight);
    DvdMenuAction MenuInput(DvdMenuInput input);
    void CompleteMenu(Guid ticket);
    void HoldPicture();
}

// Native diagnostic path only. Whole-cell EOF is deliberately the acknowledgement
// boundary: no decoder read-ahead, metadata event or source-byte EOF runs the VM.
// Moving menu input is bound to painted frames; decoding across cells resets.
internal sealed class WindowsDvdCellSession : IDisposable
{
    private readonly DvdNavigationCatalog catalog;
    private readonly DvdCellPlayback playback;
    private readonly Func<IDvdCellRenderer> createRenderer;
    private IDvdCellRenderer? renderer;
    private DvdCellMedia? media;
    private DvdCellComStream? stream;
    private Guid? holdTicket;
    private bool acknowledged, disposed, subpicturePrepared;
    private bool userPaused;
    private string? failure;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly System.Diagnostics.Stopwatch loadClock = new();
    private DvdStreamRead? last;
    private DvdMenuNavigation? heldMenu;
    internal DvdDomainState Navigation => playback.State;
    // The timed menu remains the input owner after moving video becomes still.
    internal bool HasMovingMenu => media?.MovingSubpictures?.Menu is not null && (!acknowledged || holdTicket.HasValue);
    internal WindowsDvdCellSession(DvdNavigationCatalog catalog, DvdDomainState initial, nint window)
        : this(catalog, initial, () => new WindowsDvdCellRenderer(window)) { }
    internal WindowsDvdCellSession(DvdNavigationCatalog catalog, DvdDomainState initial, Func<IDvdCellRenderer> factory, TimeProvider? clock = null)
    { this.catalog = catalog; playback = new(catalog, initial, clock); createRenderer = factory; }

    internal DvdNativeState OpenCell(CancellationToken cancellationToken = default)
    {
        CheckThread();
        if (renderer is not null || failure is not null) throw new InvalidOperationException("Close/reposition the native cell before reopening it.");
        try
        {
            if (Navigation.Stopped) return new(DvdNativeStatus.Stopped, Navigation);
            media = DvdCellMedia.Prepare(catalog, Navigation, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            stream = media.OpenStream(); renderer = createRenderer(); renderer.Open(stream, media.AudioStreams, media.MovingSubpictures);
            if (userPaused) renderer.Pause(true);
            loadClock.Restart(); return new(DvdNativeStatus.Loading, Navigation);
        }
        catch (OperationCanceledException) { ReleaseCell(); throw; }
        catch (Exception exception) when (Expected(exception)) { failure = exception.Message; ReleaseCell(); return Failed(); }
    }
    internal DvdNativeState Pump()
    {
        CheckThread();
        if (failure is not null) return Failed();
        if (renderer is null || media is null) return new(Navigation.Stopped ? DvdNativeStatus.Stopped : DvdNativeStatus.CellComplete, Navigation);
        if (acknowledged && !holdTicket.HasValue)
            return new(Navigation.Stopped ? DvdNativeStatus.Stopped : DvdNativeStatus.CellComplete, Navigation);
        try
        {
            var native = renderer.Poll();
            if (native.Error < 0) throw new COMException($"Windows DVD decoding/rendering failed (0x{native.Error:X8}). {native.Reason}".TrimEnd(), native.Error);
            if (!native.Ready || native.Frames == 0)
            {
                if (loadClock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Windows did not produce a supported DVD video frame.");
                return new(DvdNativeStatus.Loading, Navigation, native.Frames);
            }
            if (native.MenuCommand is { } request && native.Painted)
            {
                if (!HasMovingMenu || !media.Units.Any(read => ReferenceEquals(read, request.Read)))
                    throw new NotSupportedException("Stale or unbound moving DVD menu request.");
                var committed = acknowledged ? playback.HeldMenuCommand(request) : playback.MovingMenuCommand(request);
                if (committed.Status is not (DvdCellPlaybackStatus.Ready or DvdCellPlaybackStatus.Holding or DvdCellPlaybackStatus.Advanced or DvdCellPlaybackStatus.Stopped))
                    throw new NotSupportedException(committed.Reason);
                if (committed.Status is DvdCellPlaybackStatus.Advanced or DvdCellPlaybackStatus.Stopped)
                {
                    ReleaseCell(); return Navigation.Stopped ? new(DvdNativeStatus.Stopped, Navigation) : OpenCell();
                }
                renderer.CompleteMenu(request.Ticket);
                if (!acknowledged) return new(DvdNativeStatus.Playing, Navigation, native.Frames);
            }
            if (native.Painted && native.MenuRegisters is { } registers)
            {
                var selection = playback.MovingMenuSelection(registers, acknowledged);
                if (selection.Status != (acknowledged ? DvdCellPlaybackStatus.Holding : DvdCellPlaybackStatus.Ready)) throw new NotSupportedException(selection.Reason);
            }
            if (!native.Ended || !native.Painted) return new(DvdNativeStatus.Playing, Navigation, native.Frames);
            var boundary = media.Units[^1].Unit!.Boundary;
            var timedHold = media.MovingSubpictures is not null && boundary is not null &&
                (boundary.CellStillSeconds != 0 || boundary.ProgramChainStillSeconds != 0);
            if (!subpicturePrepared && (media.Subpictures.HasPictures || timedHold))
            {
                var nav = media.Units[^1].Unit!.Navigation;
                if (timedHold) renderer.HoldPicture();
                else renderer.SetSubpicture(media.Subpictures.AtNav(nav.End90Khz), media.Palette, null);
                subpicturePrepared = true;
                // The composited final picture must also reach WM_PAINT before
                // still timing or cell commands can begin.
                return new(DvdNativeStatus.Playing, Navigation, native.Frames);
            }
            if (!acknowledged)
            {
                foreach (var read in media.Units)
                {
                    // Moving local commands can change registers without ending
                    // the cell. Preserve those committed effects at completion.
                    var current = Navigation.Registers;
                    var bound = read with { State = read.State with { Registers = current },
                        Next = read.Next is { } next ? next with { Registers = current } : null };
                    var queued = playback.Queue(bound);
                    if (queued.Status != DvdCellPlaybackStatus.AwaitingPresentation) throw new NotSupportedException(queued.Reason);
                    holdTicket = queued.Ticket; last = read;
                    var completed = playback.Presented(holdTicket!.Value);
                    RequireCompletion(completed);
                }
                acknowledged = true;
            }
            var result = holdTicket.HasValue ? playback.Tick(holdTicket.Value) : null;
            if (result is not null) RequireCompletion(result);
            return new(result?.Status == DvdCellPlaybackStatus.Holding ? DvdNativeStatus.Holding :
                Navigation.Stopped ? DvdNativeStatus.Stopped : DvdNativeStatus.CellComplete, Navigation, native.Frames);
        }
        catch (Exception exception) when (Expected(exception)) { failure = exception.Message; ReleaseCell(); return Failed(); }
    }
    internal DvdNativeState SetPaused(bool paused)
    {
        CheckThread();
        if (failure is not null) return Failed();
        if (Navigation.Stopped) return new(DvdNativeStatus.Stopped, Navigation);
        try
        {
            if (paused && media is not null)
            {
                var prohibited = media.Units.Aggregate(0u, (bits, unit) => bits | unit.Unit!.Navigation.ProhibitedOperations) |
                    new DvdDomainResolver(catalog).StreamChain(Navigation).ProhibitedOperations;
                if ((prohibited & (1u << 19)) != 0) return new(DvdNativeStatus.Playing, Navigation, Reason: "Pausing is prohibited by this DVD cell.");
            }
            var result = playback.SetPaused(paused);
            if (result.Status == DvdCellPlaybackStatus.Prohibited) return new(DvdNativeStatus.Holding, Navigation, Reason: result.Reason);
            userPaused = paused; renderer?.Pause(paused); return Pump();
        }
        catch (Exception exception) when (Expected(exception)) { return Fail(exception); }
    }
    internal DvdNativeState SkipStill()
    {
        CheckThread();
        if (failure is not null) return Failed();
        if (!acknowledged || !holdTicket.HasValue) return new(DvdNativeStatus.Playing, Navigation);
        try
        {
            var result = playback.SkipStill(holdTicket.Value);
            if (result.Status == DvdCellPlaybackStatus.Prohibited) return new(DvdNativeStatus.Holding, Navigation, Reason: result.Reason);
            RequireCompletion(result); return Pump();
        }
        catch (Exception exception) when (Expected(exception)) { return Fail(exception); }
    }
    internal DvdNativeState MenuCommand(DvdMenuContext context, DvdMenuAction action)
    {
        CheckThread();
        if (failure is not null) return Failed();
        if (!acknowledged || last?.Unit is null || action.Command is null || action.InputState is null)
            return new(DvdNativeStatus.CompatibilityRequired, Navigation, Reason: "DVD menu command requires the active held presentation.");
        try
        {
            var result = playback.MenuCommand(context, last.Unit.Navigation, action.Command, action.InputState);
            RequireCompletion(result); return Pump();
        }
        catch (Exception exception) when (Expected(exception)) { return Fail(exception); }
    }
    internal DvdNativeState ContinueCell(CancellationToken cancellationToken = default)
    {
        CheckThread();
        if (failure is not null) return Failed();
        if (!acknowledged || holdTicket.HasValue || Navigation.Stopped) return Pump();
        ReleaseCell(); return OpenCell(cancellationToken);
    }
    internal DvdNativeState MovingMenuInput(DvdMenuInput input)
    {
        CheckThread(); if (failure is not null) return Failed();
        if (!HasMovingMenu || renderer is null) return new(DvdNativeStatus.Playing, Navigation, Reason: "There is no active timed DVD menu.");
        try
        {
            var action = renderer.MenuInput(input);
            return new(acknowledged ? DvdNativeStatus.Holding : DvdNativeStatus.Playing, Navigation, Reason: action.Reason);
        }
        catch (Exception exception) when (Expected(exception)) { return Fail(exception); }
    }
    internal DvdNavigationPacket? HeldNavigation => acknowledged ? last?.Unit?.Navigation : null;
    internal DvdMenuNavigation CreateHeldMenu(DvdMenuContext context, DvdMenuDisplay display)
    {
        CheckThread();
        if (!acknowledged || !holdTicket.HasValue || media is null) throw new InvalidOperationException("A held DVD cell is required.");
        if (HasMovingMenu) throw new InvalidOperationException("The held DVD picture retains its existing timed menu.");
        var expected = Navigation.Position.Chain;
        var domain = expected.Domain == DvdPlaybackDomain.Title ? DvdMenuDomain.Title :
            expected.Domain == DvdPlaybackDomain.TitleSetMenu ? DvdMenuDomain.TitleSetMenu : DvdMenuDomain.Manager;
        if (context != new DvdMenuContext(domain, expected.TitleSet, expected.Number, Navigation.Epoch))
            throw new ArgumentException("Stale held-menu context.", nameof(context));
        var menu = new DvdMenuNavigation(Navigation.Registers);
        var chain = new DvdDomainResolver(catalog).StreamChain(Navigation);
        foreach (var unit in media.Units)
        {
            var loaded = menu.Load(unit.Unit!.Navigation, context, display, chain.ProhibitedOperations);
            if (loaded.Outcome == DvdMenuOutcome.Unsupported) throw new NotSupportedException(loaded.Reason);
        }
        heldMenu = menu; return menu;
    }
    internal void RefreshHeldSubpicture(DvdMenuNavigation menu)
    {
        CheckThread();
        if (!acknowledged || !holdTicket.HasValue || media is null || last?.Unit is null)
            throw new InvalidOperationException("A held DVD cell is required.");
        if (!ReferenceEquals(menu, heldMenu)) throw new ArgumentException("Stale held-menu renderer.", nameof(menu));
        if (menu.CompatibilityReason is not null) throw new NotSupportedException(menu.CompatibilityReason);
        var time = last.Unit.Navigation.End90Khz;
        renderer!.SetSubpicture(media.Subpictures.AtNav(time), media.Palette, menu.GetHighlight(time));
    }
    internal DvdNativeState Reposition(DvdDomainState proposal)
    {
        CheckThread(); var result = playback.ReplaceState(proposal);
        if (result.Status == DvdCellPlaybackStatus.Stale) return new(DvdNativeStatus.CompatibilityRequired, Navigation, Reason: result.Reason);
        ReleaseCell(); failure = null; return OpenCell();
    }
    private void RequireCompletion(DvdCellPlaybackResult result)
    {
        if (result.Status is DvdCellPlaybackStatus.CompatibilityRequired or DvdCellPlaybackStatus.Limit)
            throw new NotSupportedException(result.Reason);
        if (result.Status is not (DvdCellPlaybackStatus.Holding or DvdCellPlaybackStatus.AwaitingPresentation)) holdTicket = null;
    }
    private static bool Expected(Exception error) => error is System.IO.IOException or System.IO.InvalidDataException or COMException or
        NotSupportedException or ArgumentException or InvalidOperationException or TimeoutException;
    private DvdNativeState Failed() => new(DvdNativeStatus.CompatibilityRequired, Navigation, Reason: failure);
    private DvdNativeState Fail(Exception error) { failure = error.Message; ReleaseCell(); return Failed(); }
    private void ReleaseCell()
    {
        // Shutdown native readers before releasing their managed CCW/media bytes.
        try { renderer?.Dispose(); }
        finally { renderer = null; stream?.Dispose(); stream = null; media = null; last = null; heldMenu = null; holdTicket = null; acknowledged = subpicturePrepared = false; }
    }
    private void CheckThread()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Use the native DVD session on its creating UI thread.");
    }
    public void Dispose() { if (disposed) return; CheckThread(); disposed = true; ReleaseCell(); }
}

internal sealed class WindowsDvdCellRenderer(nint window) : IDvdCellRenderer
{
    private WindowsMediaEngine? engine;
    public void Open(DvdCellComStream stream, int audioStreams, DvdSubpictureTiming? subpictures = null)
    {
        var probe = WindowsMediaEngine.ProbeDvdMpeg2();
        if (probe < 0) throw new COMException($"Windows MPEG-2 decoder is unavailable (0x{probe:X8}); DVD requires compatibility playback.", probe);
        engine = new(window, _ => { }, softwareVideo: true);
        engine.OpenDvdStream(stream, audioStreams, autoPlay: subpictures is null);
        if (subpictures is not null) engine.SetDvdSubpictureTiming(subpictures);
    }
    public DvdNativePresentation Poll()
    {
        var error = engine!.ErrorCode;
        if (error < 0) return new(false, false, 0, false, error, engine.DvdFrameFailure);
        var ready = engine.DvdCanPresent && engine.HasVideo && engine.HasSupportedStreams;
        if (ready) engine.UpdateVideo();
        var menu = engine.PollDvdMenu();
        var video = engine.DvdPresentation;
        return new(ready, video.Ended, video.Frames, video.Painted, 0, MenuCommand: menu.Command, MenuRegisters: menu.Registers);
    }
    public void Pause(bool paused) => engine?.SetDvdPaused(paused);
    public DvdMenuAction MenuInput(DvdMenuInput input) => engine!.DvdMenuInput(input);
    public void CompleteMenu(Guid ticket) => engine!.CompleteDvdMenu(ticket);
    public void HoldPicture() => engine!.HoldDvdPicture();
    public void SetSubpicture(DvdSubpictureFrame? frame, IReadOnlyList<uint> palette, DvdButtonHighlight? highlight)
    {
        var size = engine!.VideoSize;
        engine.SetDvdSubpicture(frame?.Colorize(palette, checked((int)size.Height), highlight));
    }
    public void Dispose() { engine?.Dispose(); engine = null; }
}
