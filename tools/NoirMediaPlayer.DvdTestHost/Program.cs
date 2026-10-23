using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer.DvdTestHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test") return SelfTest(args[1]);
        if (args.Length != 4 || args[0] != "--source" || args[2] != "--report")
        {
            MessageBox.Show("Use Test-WindowsDvdPlayback.ps1 with an explicit DVD drive or VIDEO_TS folder.", "NOIR native DVD test");
            return 2;
        }
        try
        {
            using var report = new StreamWriter(new FileStream(Path.GetFullPath(args[3]), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            new Application().Run(new DvdWindow(args[1], report));
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "NOIR native DVD test", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static int SelfTest(string reportPath)
    {
        using var output = new StreamWriter(new FileStream(Path.GetFullPath(reportPath), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        var application = new Application();
        var surface = new DvdSurface();
        var window = new Window
        {
            Title = "NOIR DVD harness self-test", Width = 160, Height = 90,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
            Opacity = 0, Content = surface
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await using var worker = new DvdWorker(surface.Handle);
                var filters = await worker.Query(session => session.GetFilterClasses());
                var capabilities = await worker.Query(session => session.GetVideoDecoderCapabilities());
                var codecs = await worker.Query(session => session.GetOptionalCodecAvailability());
                var diagnostic = await worker.Query(session => session.InspectGraph());
                if (diagnostic.InspectionError is not null || diagnostic.Filters.Count != filters.Count)
                    throw new InvalidOperationException("The native graph connection diagnostics failed.");
                try
                {
                    await worker.Run(session => session.Open(Path.Combine(Path.GetTempPath(), "NoirDvdMissing-" + Guid.NewGuid())));
                    throw new InvalidOperationException("An unavailable DVD source was incorrectly accepted.");
                }
                catch (DirectoryNotFoundException) { }
                if ((await worker.Query(session => session.GetFilterClasses())).Count != filters.Count)
                    throw new InvalidOperationException("Source validation changed the configured native graph.");
                await worker.DisposeAsync();
                try { await worker.Query(session => session.GetFilterClasses()); throw new InvalidOperationException("A closed worker accepted a command."); }
                catch (ObjectDisposedException) { }
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    Passed = true, NativeComponents = filters, DecoderCapabilities = capabilities, OptionalCodecs = codecs, GraphDiagnostic = diagnostic,
                    WorkerAndWindowLifecycleVerified = true,
                    MissingSourceRejected = true, PlaybackVerified = false
                }));
                application.Shutdown(0);
            }
            catch (Exception exception)
            {
                output.WriteLine(JsonSerializer.Serialize(new { Passed = false, exception.Message, Error = $"0x{exception.HResult:X8}" }));
                application.Shutdown(1);
            }
        };
        return application.Run(window);
    }
}

internal sealed class DvdWorker : IAsyncDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WindowsDvdSession? _session;
    private int _closing;
    internal DvdWorker(nint window)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var session = new WindowsDvdSession(window);
                _session = session;
                _ready.TrySetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception) { _ready.TrySetException(exception); }
            finally { _session = null; _stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "NOIR Windows DVD playback" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    internal async Task Run(Action<WindowsDvdSession> action)
    {
        var dispatcher = await _ready.Task.ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, this);
        await dispatcher.InvokeAsync(() => action(_session!));
    }
    internal async Task<T> Query<T>(Func<WindowsDvdSession, T> action)
    {
        var dispatcher = await _ready.Task.ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, this);
        return await dispatcher.InvokeAsync(() => action(_session!));
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 0)
        {
            try { (await _ready.Task.ConfigureAwait(false)).BeginInvokeShutdown(DispatcherPriority.Send); }
            catch { /* Constructor failure already disposed its native resources. */ }
        }
        await _stopped.Task.ConfigureAwait(false);
    }
}

internal sealed class DvdWindow : Window
{
    private readonly string _source;
    private readonly StreamWriter _report;
    private readonly DvdSurface _surface = new();
    private readonly TextBox _status = new()
    {
        Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap, IsReadOnly = true,
        BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly ComboBox _audio = new() { MinWidth = 130, Margin = new Thickness(4) };
    private readonly ComboBox _subtitles = new() { MinWidth = 130, Margin = new Thickness(4) };
    private readonly ComboBox _titles = new() { MinWidth = 70, Margin = new Thickness(4) };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1000, Margin = new Thickness(8) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private DvdWorker? _worker;
    private WindowsDvdState? _state;
    private bool _ready, _polling, _updating, _closing, _closed, _refreshQueued;
    private string? _lastState;
    private string? _lastError;

    internal DvdWindow(string source, StreamWriter report)
    {
        _source = source; _report = report;
        Title = "NOIR · Windows DVD playback test"; Width = 1080; Height = 760;
        MinWidth = 650; MinHeight = 450;
        var root = new DockPanel();
        var controls = new StackPanel(); DockPanel.SetDock(controls, Dock.Bottom); root.Children.Add(controls);
        controls.Children.Add(_status); controls.Children.Add(_seek);
        var buttons = new WrapPanel(); controls.Children.Add(buttons);
        AddButton(buttons, "Play", session => session.Play()); AddButton(buttons, "Pause", session => session.Pause());
        AddButton(buttons, "Stop", session => session.Stop()); AddButton(buttons, "Root menu", session => session.ShowMenu());
        AddButton(buttons, "Title menu", session => session.ShowMenu(true)); AddButton(buttons, "Resume", session => session.Resume());
        AddButton(buttons, "Previous chapter", session => session.PreviousChapter()); AddButton(buttons, "Next chapter", session => session.NextChapter());
        AddButton(buttons, "Continue still", session => session.ExitStill());
        var copyStatus = new Button { Content = "Copy status", Margin = new Thickness(3), Padding = new Thickness(8, 4, 8, 4) };
        copyStatus.Click += (_, _) =>
        {
            try { Clipboard.SetText(_status.Text); }
            catch (COMException exception) { Error("Copy status", exception); }
        };
        buttons.Children.Add(copyStatus);
        var navigation = new WrapPanel(); controls.Children.Add(navigation);
        AddButton(navigation, "↑", session => session.Navigate(1)); AddButton(navigation, "↓", session => session.Navigate(2));
        AddButton(navigation, "←", session => session.Navigate(3)); AddButton(navigation, "→", session => session.Navigate(4));
        AddButton(navigation, "Select", session => session.ActivateButton());
        navigation.Children.Add(new TextBlock { Text = "Title", VerticalAlignment = VerticalAlignment.Center }); navigation.Children.Add(_titles);
        navigation.Children.Add(new TextBlock { Text = "Audio", VerticalAlignment = VerticalAlignment.Center }); navigation.Children.Add(_audio);
        navigation.Children.Add(new TextBlock { Text = "Subtitles", VerticalAlignment = VerticalAlignment.Center }); navigation.Children.Add(_subtitles);
        var volume = new Slider { Minimum = 0, Maximum = 100, Value = 75, Width = 110, Margin = new Thickness(8) };
        navigation.Children.Add(new TextBlock { Text = "Volume", VerticalAlignment = VerticalAlignment.Center }); navigation.Children.Add(volume);
        volume.ValueChanged += async (_, _) => { if (_ready) await Command("Volume", session => session.SetVolume((int)volume.Value, false)); };
        _audio.SelectionChanged += async (_, _) =>
        {
            if (!_updating && _audio.SelectedItem is TrackChoice choice && choice.Id is { } id)
                await Command("Audio", session => session.SelectAudio(id));
        };
        _subtitles.SelectionChanged += async (_, _) =>
        {
            if (!_updating && _subtitles.SelectedItem is TrackChoice choice)
                await Command("Subtitles", session =>
                {
                    if (choice.Id is { } id) session.SelectSubtitle(id);
                    session.EnableSubtitles(choice.Id is not null);
                });
        };
        _titles.SelectionChanged += async (_, _) =>
        {
            if (!_updating && _titles.SelectedItem is uint title) await Command("Title", session => session.PlayTitle(title));
        };
        _seek.PreviewMouseLeftButtonUp += async (_, _) =>
        {
            if (_state?.DurationMilliseconds is { } length && _state.Domain == 4)
                await Command("Seek", session => session.Seek((long)(_seek.Value * length / 1000)));
        };
        _surface.Changed += QueueRefresh;
        _surface.RepaintRequested += QueueRefresh;
        _surface.DisplayChanged += async () => { if (_ready) await Command("Display changed", session => session.DisplayChanged()); };
        root.Children.Add(_surface); Content = root;
        Loaded += async (_, _) => await Open();
        _timer.Tick += async (_, _) => await Poll();
        PreviewKeyDown += async (_, e) =>
        {
            if (Keyboard.FocusedElement is ComboBox or TextBox) return;
            uint direction = e.Key switch { Key.Up => 1, Key.Down => 2, Key.Left => 3, Key.Right => 4, _ => 0 };
            if (direction != 0) { e.Handled = true; await Command("Menu direction", session => session.Navigate(direction)); }
            else if (e.Key == Key.Enter) { e.Handled = true; await Command("Menu select", session => session.ActivateButton()); }
        };
        Closing += async (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true; _ready = false; _timer.Stop();
            if (_worker is { } worker) await worker.DisposeAsync();
            Log("Closed", new { PlaybackVerified = false, Note = "Picture, sound and feature behavior require human confirmation." });
            _closed = true; Close();
        };
    }
    private async Task Open()
    {
        Log("Starting", new { Source = _source, Backend = "Windows DVD Navigator / Microsoft decoders / VMR9", PlaybackVerified = false });
        _status.Text = "Building Windows DVD graph…";
        try
        {
            _worker = new DvdWorker(_surface.Handle);
            Log("DecoderCapabilities", await _worker.Query(session => session.GetVideoDecoderCapabilities()));
            Log("OptionalCodecs", await _worker.Query(session => session.GetOptionalCodecAvailability()));
            try { await _worker.Run(session => session.Open(_source)); }
            finally { Log("GraphBuild", await _worker.Query(session => (object?)session.LastBuildDiagnostic ?? new { Note = "No graph build was attempted." })); }
            var filters = await _worker.Query(session => session.GetFilterClasses());
            Log("GraphReady", filters);
            await _worker.Run(session => session.Play());
            if (_closing) return;
            _ready = true; _timer.Start(); await Poll();
        }
        catch (Exception exception) { Error("Open", exception); }
    }
    private async Task Command(string name, Action<WindowsDvdSession> command)
    {
        if (!_ready || _closing || _worker is null) return;
        try
        {
            await _worker.Run(command);
            if (name is not ("Resize/repaint" or "Display changed")) _lastError = null;
            Log("Command", name);
        }
        catch (Exception exception) { Error(name, exception); }
    }
    private async Task Poll()
    {
        if (!_ready || _closing || _polling || _worker is null) return;
        _polling = true;
        try
        {
            var data = await _worker.Query(session =>
            {
                var events = session.DrainEvents();
                return (State: events.Any(value => value.Code is 3 or 0x10c) ? null : session.GetState(), Events: events);
            });
            if (_closing) return;
            foreach (var mediaEvent in data.Events) Log("MediaEvent", mediaEvent);
            if (data.State is null)
            {
                var failure = data.Events.First(value => value.Code is 3 or 0x10c);
                await Command("Stop after native error", session => session.Stop());
                _ready = false; _timer.Stop();
                _status.Text = $"Windows DVD playback failed: event 0x{failure.Code:X}, detail 0x{failure.Parameter1:X}. " +
                    "Close this test and use the main player's compatibility playback. The diagnostic report contains the error.";
                return;
            }
            _state = data.State;
            var serialized = JsonSerializer.Serialize(_state);
            if (serialized != _lastState) { Log("State", _state); _lastState = serialized; }
            _status.Text = (_lastError is null ? "" : _lastError + "\n") +
                $"Windows DVD · graph {_state.GraphState} · domain {_state.Domain} · title {_state.Title}/{_state.TitleCount}" +
                $" · chapter {_state.Chapter}/{_state.ChapterCount} · {FormatTime(_state.PositionMilliseconds)} / {FormatTime(_state.DurationMilliseconds)}" +
                "\nOriginal aspect ratio · arrows and Enter navigate DVD menus · close this window when finished.";
            _updating = true;
            try
            {
                RefreshTracks(_audio, _state.Audio, _state.AudioId, false);
                RefreshTracks(_subtitles, _state.Subtitles, _state.SubtitlesDisabled ? null : _state.SubtitleId, true);
                if (_titles.Items.Count != _state.TitleCount)
                {
                    _titles.Items.Clear(); for (uint title = 1; title <= _state.TitleCount; title++) _titles.Items.Add(title);
                }
                _titles.SelectedItem = _state.Title;
                if (!_seek.IsMouseCaptureWithin && _state.DurationMilliseconds is > 0)
                    _seek.Value = Math.Clamp(1000d * (_state.PositionMilliseconds ?? 0) / _state.DurationMilliseconds.Value, 0, 1000);
                _seek.IsEnabled = _state.Domain == 4 && _state.DurationMilliseconds is > 0;
            }
            finally { _updating = false; }
        }
        catch (Exception exception) { Error("Playback status", exception); }
        finally { _polling = false; }
    }
    private void QueueRefresh()
    {
        if (!_ready || _closing || _refreshQueued) return;
        _refreshQueued = true;
        _ = Dispatcher.BeginInvoke(async () =>
        {
            _refreshQueued = false;
            await Command("Resize/repaint", session => { session.UpdateVideo(); session.Repaint(); });
        }, DispatcherPriority.Background);
    }
    private void AddButton(Panel panel, string label, Action<WindowsDvdSession> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(8, 4, 8, 4) };
        button.Click += async (_, _) => await Command(label, action); panel.Children.Add(button);
    }
    private static void RefreshTracks(ComboBox box, IReadOnlyList<WindowsDvdTrack> tracks, uint? selected, bool off)
    {
        var choices = tracks.Select(track => new TrackChoice(track.Id, $"{track.Id + 1} · {track.Language}")).ToList();
        if (off) choices.Insert(0, new TrackChoice(null, "Off"));
        if (!box.Items.Cast<TrackChoice>().SequenceEqual(choices)) { box.Items.Clear(); foreach (var choice in choices) box.Items.Add(choice); }
        box.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selected);
    }
    private static string FormatTime(long? value) => value is { } time ? TimeSpan.FromMilliseconds(time).ToString(@"hh\:mm\:ss") : "--:--:--";
    private void Error(string action, Exception exception)
    {
        if (_closing) return;
        _lastError = $"{action}: 0x{exception.HResult:X8} · {exception.Message}";
        _status.Text = _lastError;
        Log("Error", new { Action = action, HResult = $"0x{exception.HResult:X8}", exception.Message });
    }
    private void Log(string kind, object data) => _report.WriteLine(JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Kind = kind, Data = data }));
    private sealed record TrackChoice(uint? Id, string Label) { public override string ToString() => Label; }
}

internal sealed class DvdSurface : HwndHost
{
    internal event Action? Changed, RepaintRequested, DisplayChanged;
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        var window = CreateWindowEx(0, "static", "", 0x56000000, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
        if (window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new HandleRef(this, window);
    }
    protected override void DestroyWindowCore(HandleRef window) => _ = DestroyWindow(window.Handle);
    protected override nint WndProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 5) Changed?.Invoke();
        else if (message == 15) RepaintRequested?.Invoke();
        else if (message == 126) DisplayChanged?.Invoke();
        return base.WndProc(window, message, wParam, lParam, ref handled);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
}
