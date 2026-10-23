using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer.Controls;

/// <summary>First-party Win32 video window, shared by Windows playback and VLC fallback.</summary>
[ContentProperty(nameof(Overlay))]
public sealed class PlaybackView : ContentControl
{
    private readonly VideoWindow _video = new();
    private MediaPlayer? _player;
    private Window? _owner;
    private Window? _videoWindow;
    private Window? _overlayWindow;
    private Rect _overlayBounds = Rect.Empty;
    private DispatcherOperation? _overlayPositionOperation;

    public static readonly DependencyProperty OverlayProperty = DependencyProperty.Register(
        nameof(Overlay), typeof(UIElement), typeof(PlaybackView), new PropertyMetadata(null, OnOverlayChanged));

    public UIElement? Overlay { get => (UIElement?)GetValue(OverlayProperty); set => SetValue(OverlayProperty, value); }
    public nint WindowHandle => _video.Handle;
    public void RefreshVideoGeometry() => _video.RequestEmbeddedResize();

    public MediaPlayer? MediaPlayer
    {
        get => _player;
        set
        {
            if (ReferenceEquals(_player, value)) return;
            if (_player is not null && WindowHandle != 0) _player.Hwnd = 0;
            _player = value;
            AttachPlayer();
        }
    }

    public PlaybackView()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        // Keep the GPU child HWND out of the custom-chrome interface window.
        // Mixing their resize repaint cycles makes the whole interface flash.
        Content = new Border { Background = Brushes.Black };
        _video.Created += AttachPlayer;
        _video.Clicked += () =>
        {
            if (_owner is null) return;
            Keyboard.ClearFocus();
            _owner.Focus();
        };
        Loaded += (_, _) => ConnectOverlay();
        Unloaded += (_, _) => DisconnectOverlay();
        LayoutUpdated += (_, _) => QueueOverlayPosition();
        IsVisibleChanged += (_, _) => QueueOverlayPosition();
    }

    private void AttachPlayer()
    {
        if (_player is not null && WindowHandle != 0) _player.Hwnd = WindowHandle;
    }

    private static void OnOverlayChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (PlaybackView)sender;
        view.DisconnectOverlay();
        if (view.IsLoaded) view.ConnectOverlay();
    }

    private void ConnectOverlay()
    {
        if (_overlayWindow is not null || Overlay is null || Window.GetWindow(this) is not { } owner) return;
        _owner = owner;
        owner.LocationChanged += OwnerGeometryChanged;
        owner.SizeChanged += OwnerGeometryChanged;
        owner.StateChanged += OwnerGeometryChanged;
        owner.IsVisibleChanged += OwnerVisibilityChanged;
        _videoWindow = new Window
        {
            Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Black, ShowInTaskbar = false, ShowActivated = false,
            AllowDrop = owner.AllowDrop,
            Focusable = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Width = Math.Max(1, ActualWidth), Height = Math.Max(1, ActualHeight), Content = _video,
            Title = "NOIR video surface"
        };
        // Ownership does not create a WPF input route. Keep file drops and
        // keyboard shortcuts connected to the player interface.
        _videoWindow.DragOver += (_, args) => owner.RaiseEvent(args);
        _videoWindow.Drop += (_, args) => owner.RaiseEvent(args);
        _videoWindow.PreviewKeyDown += (_, args) => owner.RaiseEvent(args);
        _videoWindow.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(_videoWindow).Handle;
            var style = GetWindowLongPtr(handle, -20).ToInt64();
            SetWindowLongPtr(handle, -20, (nint)(style | 0x08000080)); // Non-activating owned tool window.
        };
        _overlayWindow = new Window
        {
            Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
            ShowActivated = false, Focusable = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Width = Math.Max(1, ActualWidth), Height = Math.Max(1, ActualHeight), Content = Overlay,
            Title = "NOIR video overlay"
        };
        _overlayWindow.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(_overlayWindow).Handle;
            // Layered, click-through, non-activating tool window. Its owner provides z-order
            // and minimization; no global topmost window is created over other applications.
            var style = GetWindowLongPtr(handle, -20).ToInt64();
            SetWindowLongPtr(handle, -20, (nint)(style | 0x080000A0));
        };
        PositionOverlay();
    }

    private void OwnerGeometryChanged(object? sender, EventArgs args) => QueueOverlayPosition();
    private void OwnerVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs args) => QueueOverlayPosition();

    private void QueueOverlayPosition()
    {
        if (_overlayWindow is null || _owner is null) return;
        if (!IsLoaded || !IsVisible || !_owner.IsVisible || _owner.WindowState == WindowState.Minimized)
        {
            _overlayPositionOperation?.Abort();
            _overlayPositionOperation = null;
            PositionOverlay();
            return;
        }
        if (_overlayPositionOperation is not null) return;
        // Window movement, size changes and child layout can all describe one
        // drag step. Move the layered overlay once with the final video bounds.
        _overlayPositionOperation = Dispatcher.InvokeAsync(() =>
        {
            _overlayPositionOperation = null;
            PositionOverlay();
        }, DispatcherPriority.Render);
    }

    private void PositionOverlay()
    {
        if (_overlayWindow is null || _videoWindow is null || _owner is null) return;
        if (!IsLoaded || !IsVisible || !_owner.IsVisible || _owner.WindowState == WindowState.Minimized ||
            ActualWidth <= 0 || ActualHeight <= 0)
        {
            _overlayWindow.Hide();
            _videoWindow.Hide();
            _overlayBounds = Rect.Empty;
            return;
        }
        var start = PointToScreen(new Point());
        var end = PointToScreen(new Point(ActualWidth, ActualHeight));
        var rectangle = new WindowRect { Left = (int)Math.Round(start.X), Top = (int)Math.Round(start.Y),
            Right = (int)Math.Round(end.X), Bottom = (int)Math.Round(end.Y) };
        if (!_videoWindow.IsVisible)
        {
            _videoWindow.Show();
            AttachPlayer();
        }
        if (!_overlayWindow.IsVisible) _overlayWindow.Show();
        var bounds = new Rect(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
        if (bounds == _overlayBounds) return;
        var handle = new WindowInteropHelper(_overlayWindow).Handle;
        var videoHandle = new WindowInteropHelper(_videoWindow).Handle;
        if (!SetWindowPos(videoHandle, 0, rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top, 0x14) ||
            !SetWindowPos(handle, 0, rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top, 0x14))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _overlayBounds = bounds;
    }

    private void DisconnectOverlay()
    {
        _overlayPositionOperation?.Abort();
        _overlayPositionOperation = null;
        if (_owner is not null)
        {
            _owner.LocationChanged -= OwnerGeometryChanged;
            _owner.SizeChanged -= OwnerGeometryChanged;
            _owner.StateChanged -= OwnerGeometryChanged;
            _owner.IsVisibleChanged -= OwnerVisibilityChanged;
        }
        if (_overlayWindow is not null)
        {
            _overlayWindow.Content = null;
            _overlayWindow.Close();
        }
        if (_videoWindow is not null)
        {
            _videoWindow.Content = null;
            _videoWindow.Close();
        }
        _videoWindow = null;
        _overlayWindow = _owner = null;
        _overlayBounds = Rect.Empty;
    }

    private sealed class VideoWindow : HwndHost
    {
        // Stock VLC ignores this registered message. Our engine handles it on
        // its window thread, so UI resizing never waits for a decoded frame.
        private static readonly uint EmbeddedResizeMessage = RegisterWindowMessage("NOIR.VLC.EmbeddedResize.v1");
        internal event Action? Created;
        internal event Action? Clicked;
        internal void RequestEmbeddedResize()
        {
            if (Handle == 0 || EmbeddedResizeMessage == 0) return;
            var child = GetWindow(Handle, 5); // GW_CHILD: VLC's event-thread window, when present.
            if (child != 0) PostMessage(child, EmbeddedResizeMessage, Handle, 0);
        }
        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            var handle = CreateWindowEx(0, "STATIC", "NOIR video", 0x56000004, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            // HwndHost assigns Handle after this method returns.
            Dispatcher.BeginInvoke(() => Created?.Invoke());
            return new HandleRef(this, handle);
        }
        protected override void DestroyWindowCore(HandleRef window) => DestroyWindow(window.Handle);
        protected override nint WndProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == 0x0047) RequestEmbeddedResize(); // WM_WINDOWPOSCHANGED
            if (message == 0x0201) Clicked?.Invoke();
            if (WindowsSoftwareVideoSurface.TryPaint(window, message, wParam))
            {
                handled = true;
                return message == 0x0014 ? 1 : 0;
            }
            return base.WndProc(window, message, wParam, lParam, ref handled);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out WindowRect rectangle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
