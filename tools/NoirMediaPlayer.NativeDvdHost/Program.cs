using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer.NativeDvdHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = new JsonSerializerOptions(); options.Converters.Add(new JsonStringEnumConverter());
        if (args.Length == 3 && args[0] == "--probe" && args[1] == "--report")
        {
            try
            {
                using var report = new FileStream(Path.GetFullPath(args[2]), FileMode.CreateNew);
                var result = WindowsMediaEngine.ProbeDvdMpeg2();
                JsonSerializer.Serialize(report, new { Decoder = "Microsoft MPEG-2", HResult = $"0x{result:X8}", Activates = result >= 0,
                    NativeDiscPlaybackVerified = false, VlcInitialized = false, DiscOpened = false }, options);
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return 1; }
        }
        if (args.Length != 4 || args[0] != "--source" || args[2] != "--report") return 2;
        try
        {
            using var report = new StreamWriter(new FileStream(Path.GetFullPath(args[3]), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            var app = new Application(); var surface = new Surface(); var status = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
            var panel = new DockPanel(); DockPanel.SetDock(status, Dock.Bottom); panel.Children.Add(status); panel.Children.Add(surface);
            var window = new Window { Title = "NOIR · Own DVD navigation preview", Width = 960, Height = 600, Content = panel };
            WindowsDvdCellSession? session = null; DvdMenuNavigation? menu = null; DvdMenuContext? context = null;
            DvdNavigationPacket? held = null; DvdNativeStatus? previous = null; var paused = false;
            void Log(object item) => report.WriteLine(JsonSerializer.Serialize(item, options));
            void Show(DvdNativeState state)
            {
                status.Text = $"{state.Status}: {state.Reason ?? "Native preview only; main DVD/Blu-ray playback is unchanged."}";
                if (state.Status != previous) { Log(state); previous = state.Status; }
            }
            surface.MenuPointer = input => { if (session?.HasMovingMenu == true) Show(session.MovingMenuInput(input)); };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (session is null) return;
                    var state = session.Pump(); Show(state);
                    if (state.Status == DvdNativeStatus.CellComplete) { held = null; menu = null; Show(session.ContinueCell()); }
                    else if (state.Status == DvdNativeStatus.Holding && !session.HasMovingMenu && session.HeldNavigation is { } packet && held != packet)
                    {
                        held = packet; var position = session.Navigation;
                        context = new(position.Position.Chain.Domain == DvdPlaybackDomain.Title ? DvdMenuDomain.Title :
                            position.Position.Chain.Domain == DvdPlaybackDomain.TitleSetMenu ? DvdMenuDomain.TitleSetMenu : DvdMenuDomain.Manager,
                            position.Position.Chain.TitleSet, position.Position.Chain.Number, position.Epoch);
                        menu = session.CreateHeldMenu(context, DvdMenuDisplay.Normal);
                        session.RefreshHeldSubpicture(menu);
                        status.Text += " Use arrow keys and Enter for authored held-menu buttons.";
                    }
                    if (state.Status is DvdNativeStatus.CompatibilityRequired or DvdNativeStatus.Stopped) timer.Stop();
                }
                catch (Exception error) { timer.Stop(); Log(new { Error = error.Message, HResult = $"0x{error.HResult:X8}" }); status.Text = error.Message; }
            };
            window.Loaded += (_, _) =>
            {
                try
                {
                    var probe = WindowsMediaEngine.ProbeDvdMpeg2(); Log(new { Mpeg2HResult = $"0x{probe:X8}", NativeDiscPlaybackVerified = false, VlcInitialized = false });
                    if (probe < 0) throw new COMException($"Windows MPEG-2 is unavailable (0x{probe:X8}). Keep using the main player's DVD compatibility backend.", probe);
                    var catalog = DvdNavigationCatalog.Read(Path.GetFullPath(args[1])); var first = new DvdDomainResolver(catalog).Start();
                    if (first.Status != DvdDomainStatus.Resolved) throw new NotSupportedException(first.Reason);
                    session = new(catalog, first.State, surface.Handle); Show(session.OpenCell()); timer.Start();
                }
                catch (Exception error) { Log(new { Error = error.Message, HResult = $"0x{error.HResult:X8}" }); status.Text = error.Message; }
            };
            window.KeyDown += (_, e) =>
            {
                if (session is null) return;
                try
                {
                    if (e.Key == Key.Space) { paused = !paused; Show(session.SetPaused(paused)); }
                    else if (e.Key == Key.S) Show(session.SkipStill());
                    else if (session.HasMovingMenu)
                    {
                        var input = e.Key == Key.Enter ? new DvdMenuInput(DvdMenuInputKind.Activate) :
                            e.Key is Key.Up or Key.Down or Key.Left or Key.Right ? new DvdMenuInput(DvdMenuInputKind.Move, e.Key switch {
                                Key.Up => DvdMenuDirection.Up, Key.Down => DvdMenuDirection.Down, Key.Left => DvdMenuDirection.Left, _ => DvdMenuDirection.Right }) :
                            e.Key >= Key.D1 && e.Key <= Key.D9 ? new DvdMenuInput(DvdMenuInputKind.Number, Number: (int)e.Key - (int)Key.D0) :
                            e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9 ? new DvdMenuInput(DvdMenuInputKind.Number, Number: (int)e.Key - (int)Key.NumPad0) : null;
                        if (input is not null) { Show(session.MovingMenuInput(input)); e.Handled = true; }
                    }
                    else if (menu is not null && held is not null && context is not null)
                    {
                        var action = e.Key == Key.Enter ? menu.Activate(held.End90Khz) :
                            e.Key is Key.Up or Key.Down or Key.Left or Key.Right ? menu.Move(e.Key switch {
                                Key.Up => DvdMenuDirection.Up, Key.Down => DvdMenuDirection.Down, Key.Left => DvdMenuDirection.Left, _ => DvdMenuDirection.Right }, held.End90Khz) : null;
                        if (action is not null) session.RefreshHeldSubpicture(menu);
                        if (action?.Command is not null) Show(session.MenuCommand(context, action));
                    }
                }
                catch (Exception error) { Log(new { Error = error.Message }); status.Text = error.Message; }
            };
            window.Closed += (_, _) => { timer.Stop(); session?.Dispose(); };
            app.Run(window); return 0;
        }
        catch (Exception error) { MessageBox.Show(error.Message, "NOIR own DVD preview"); return 1; }
    }
    private sealed class Surface : HwndHost
    {
        internal Action<DvdMenuInput>? MenuPointer;
        protected override HandleRef BuildWindowCore(HandleRef parent) => new(this,
            CreateWindowEx(0, "STATIC", "", 0x50000000, 0, 0, 1, 1, parent.Handle, 0, 0, 0));
        protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);
        protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (WindowsSoftwareVideoSurface.TryPaint(hwnd, message, wParam)) { handled = true; return 0; }
            if (message is 0x0200 or 0x0202)
            {
                MenuPointer?.Invoke(new(message == 0x0202 ? DvdMenuInputKind.Click : DvdMenuInputKind.Point,
                    X: unchecked((short)(lParam.ToInt64() & 0xffff)), Y: unchecked((short)((lParam.ToInt64() >> 16) & 0xffff))));
            }
            return base.WndProc(hwnd, message, wParam, lParam, ref handled);
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
}
