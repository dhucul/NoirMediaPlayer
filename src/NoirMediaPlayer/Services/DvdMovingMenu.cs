using System.IO;

namespace NoirMediaPlayer.Services;

internal enum DvdMenuInputKind { Move, Activate, Number, Point, Click }
// Pointer coordinates are client pixels at the renderer boundary, DVD canvas
// coordinates inside DvdMovingMenu. The renderer performs the inverse transform.
internal sealed record DvdMenuInput(DvdMenuInputKind Kind, DvdMenuDirection Direction = DvdMenuDirection.Right,
    int Number = 0, int X = 0, int Y = 0);
internal sealed record DvdMovingMenuCommand(Guid Ticket, DvdMenuContext Context, DvdStreamRead Read, DvdMenuAction Action);

// Serialized by the engine's frame/input lock. Preparation validates layouts but
// never ticks/executes them. Only the host's painted-frame acknowledgement may
// tick automatic actions or accept input. One pending command freezes the frame.
internal sealed class DvdMovingMenu
{
    private readonly DvdStreamRead[] units;
    private readonly DvdMenuContext context;
    private readonly DvdMenuDisplay display;
    private readonly uint prohibitions, start;
    private readonly DvdMenuNavigation menu;
    private int index = -1;
    private uint? time;
    internal bool Held { get; private set; }
    internal DvdMovingMenuCommand? Pending { get; private set; }
    internal DvdVmState Registers => menu.VmState;
    internal DvdButtonHighlight? Highlight => Active ? menu.GetHighlight(time!.Value) : null;
    private bool Active => time.HasValue && index >= 0 && (Held ||
        unchecked(time.Value - units[index].Unit!.Navigation.Start90Khz) <
        unchecked(units[index].Unit!.Navigation.End90Khz - units[index].Unit!.Navigation.Start90Khz));

    internal DvdMovingMenu(IReadOnlyList<DvdStreamRead> source, DvdMenuContext context,
        DvdVmState registers, uint prohibitions = 0, DvdMenuDisplay display = DvdMenuDisplay.Normal)
    {
        if (source.Count is < 1 or > 1024 || source.Any(read => read.Status != DvdStreamStatus.Ready || read.Unit is null))
            throw new InvalidDataException("Moving DVD menu needs validated NAV units.");
        units = source.ToArray(); this.context = context; this.display = display; this.prohibitions = prohibitions;
        var key = units[0].State.Position.Chain;
        var domain = key.Domain == DvdPlaybackDomain.ManagerMenu ? DvdMenuDomain.Manager : key.Domain == DvdPlaybackDomain.TitleSetMenu ? DvdMenuDomain.TitleSetMenu : DvdMenuDomain.Title;
        if (context.Domain != domain || context.TitleSet != key.TitleSet || context.ProgramChain != key.Number)
            throw new ArgumentException("Moving DVD menu context does not match its domain.", nameof(context));
        start = units[0].Unit!.Navigation.Start90Khz;
        var validate = new DvdMenuNavigation(registers);
        long end = 0; uint sector = 0;
        for (var i = 0; i < units.Length; i++)
        {
            var read = units[i]; var nav = read.Unit!.Navigation;
            var begin = (long)unchecked(nav.Start90Khz - start); var finish = (long)unchecked(nav.End90Khz - start);
            if (begin < end || finish <= begin || finish > 3600L * 90000 || i != 0 && nav.Sector <= sector ||
                read.State.Epoch != context.Epoch || read.State.Position.Chain != units[0].State.Position.Chain ||
                read.State.Position.Cell != units[0].State.Position.Cell || read.State.Position.Sector != nav.Sector)
                throw new NotSupportedException("Discontinuous or stale moving DVD menu NAV timeline.");
            if (nav.Highlight.Groups.SelectMany(group => group.Buttons).Any(button => button.Bounds.Right >= 720 || button.Bounds.Bottom >= 576))
                throw new NotSupportedException("Moving DVD menu buttons exceed the DVD canvas.");
            Check(validate.Load(nav, context, display, prohibitions)); end = finish; sector = nav.Sector;
        }
        menu = new(registers);
    }
    internal void ValidateCanvas(int height)
    {
        if (height is not (480 or 576) || units.Any(read => read.Unit!.Navigation.Highlight.Groups
            .SelectMany(group => group.Buttons).Any(button => button.Bounds.Bottom >= height)))
            throw new NotSupportedException("Moving DVD menu coordinates do not match the decoded video canvas.");
    }
    internal void Advance(long source90Khz)
    {
        if (source90Khz is < 0 or > DvdSubpicture.ClockMask) throw new ArgumentOutOfRangeException(nameof(source90Khz));
        if (Held) throw new InvalidOperationException("A held DVD menu has a frozen presentation clock.");
        if (Pending is not null) return;
        var next = (uint)source90Khz; var elapsed = unchecked(next - start);
        if (elapsed > 3600u * 90000 || time.HasValue && elapsed < unchecked(time.Value - start))
            throw new NotSupportedException("Moving DVD menu clock reversed or left its bounds.");
        while (index + 1 < units.Length && unchecked(units[index + 1].Unit!.Navigation.Start90Khz - start) <= elapsed)
        { index++; Check(menu.Load(units[index].Unit!.Navigation, context, display, prohibitions)); }
        time = next;
    }
    internal void EnterHold(long source90Khz)
    {
        if (Held) return;
        if (!time.HasValue || Pending is not null || (uint)source90Khz != units[^1].Unit!.Navigation.End90Khz)
            throw new InvalidOperationException("Hold the painted final DVD menu only after resolving its pending command.");
        // Load any final continuation without reconstructing the menu. Its own
        // HLI expiry rules still apply; holding never revives an expired layout.
        Advance(source90Khz); Held = true;
    }
    internal DvdMenuAction Painted(DvdMenuInput? input = null)
    {
        if (Pending is not null) return new(DvdMenuOutcome.Inactive, Reason: "A painted DVD menu command is awaiting resolution.");
        if (!Active) return new(DvdMenuOutcome.Inactive);
        var now = time!.Value;
        var action = input is null ? menu.Tick(now) : input.Kind switch
        {
            DvdMenuInputKind.Move => menu.Move(input.Direction, now),
            DvdMenuInputKind.Activate => menu.Activate(now),
            DvdMenuInputKind.Number => menu.SelectNumber(input.Number, now),
            DvdMenuInputKind.Point => menu.Point(input.X, input.Y, now),
            DvdMenuInputKind.Click => menu.Point(input.X, input.Y, now, activate: true),
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        };
        Check(action);
        if (action.Command is not null && action.InputState is not null)
            Pending = new(Guid.NewGuid(), context, units[index], action);
        return action;
    }
    internal void Complete(Guid ticket)
    {
        if (Pending?.Ticket != ticket) throw new InvalidOperationException("Stale moving DVD menu command acknowledgement.");
        if (Pending.Action.Outcome == DvdMenuOutcome.CommandRequiresCompatibility)
            throw new InvalidOperationException("A branching DVD menu needs a new cell/engine.");
        Pending = null;
    }
    private static void Check(DvdMenuAction action)
    { if (action.Outcome == DvdMenuOutcome.Unsupported) throw new NotSupportedException(action.Reason); }
}

internal static class DvdMenuViewport
{
    internal static (int X, int Y)? Point(int clientWidth, int clientHeight, double aspect, int rotation, int canvasHeight, int x, int y)
    {
        if (clientWidth <= 0 || clientHeight <= 0 || !double.IsFinite(aspect) || aspect <= 0 ||
            rotation is not (0 or 90 or 180 or 270) || canvasHeight is not (480 or 576)) return null;
        var width = (int)Math.Round(Math.Min(clientWidth, clientHeight * aspect));
        var height = (int)Math.Round(width / aspect);
        if (width <= 0 || height <= 0) return null;
        var left = (clientWidth - width) / 2; var top = (clientHeight - height) / 2;
        if (x < left || x >= left + width || y < top || y >= top + height) return null;
        double u = (x - left + .5) / width, v = (y - top + .5) / height;
        (u, v) = rotation switch { 90 => (v, 1 - u), 180 => (1 - u, 1 - v), 270 => (1 - v, u), _ => (u, v) };
        return (Math.Clamp((int)(u * 720), 0, 719), Math.Clamp((int)(v * canvasHeight), 0, canvasHeight - 1));
    }
}
