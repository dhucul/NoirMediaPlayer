using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace NoirMediaPlayer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PlaylistItem> _playlist = [];
    private readonly SettingsService _settingsService;
    private readonly SemaphoreSlim _sourceImportGate = new(1, 1);
    private readonly SemaphoreSlim _discScanGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Stopwatch _positionPersistClock = Stopwatch.StartNew();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _noticeTimer;
    private readonly DispatcherTimer _searchDebounceTimer;
    private readonly Random _random = new();
    private readonly HashSet<string> _playlistSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _engineLogSync = new();

    // Serializes every mutation of the native player. Background teardown and the synchronous
    // track-change path must never run libvlc_media_player_stop concurrently.
    private readonly object _playbackEngineSync = new();
    private readonly Queue<string> _recentDiscEngineLogs = new();
    private readonly PlayerSettings _settings;
    private readonly LibVLC _libVlc;
    private readonly VlcMediaPlayer _mediaPlayer;
    private ICollectionView? _playlistView;
    private Media? _currentMedia;
    private PlaylistItem? _currentItem;
    private bool _isPlaying;
    private bool _isScrubbing;
    private bool _isInitializing = true;
    private volatile bool _isClosing;
    private bool _isFullscreen;
    private bool _isCompact;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _playbackDisposed;
    private bool _aacsLibraryRestartRequired;
    private bool _inspectorVisible = true;
    private bool _sidebarVisible = true;
    private bool _isRefreshingTracks;
    private int _currentIndex = -1;
    private int _rotation;
    private long _pendingResumePosition;
    private long _playbackGeneration;
    private Action? _detachPlaybackEvents;
    private CancellationTokenSource? _sourceImportCancellation;
    private CancellationTokenSource? _discScanCancellation;
    private CancellationTokenSource? _settingsSaveCancellation;
    private CancellationTokenSource? _playbackStartupCancellation;
    private CancellationTokenSource? _ejectCancellation;
    private Task? _sourceImportWorker;
    private Task? _discScanWorker;
    private Task? _ejectTask;
    private Task<AacsKeyDownloadResult?>? _aacsRepairTask;
    private Task? _settingsSaveTask;
    private Task? _playlistSaveTask;
    private Task _playbackTeardownTask = Task.CompletedTask;
    private string _playlistFilterQuery = string.Empty;
    private Rect _restoreBounds;
    private WindowState _restoreWindowState;

    private const int ImportBatchSize = 100;
    private const int ImportChannelCapacity = 256;
    private const int MaxPlaylistItems = 10_000;
    private const int MaxResumePositions = 250;
    private const int MaxRecentFiles = 20;
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DiscScanTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FileWriteTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
    private const int MaxRecentDiscEngineLogs = 24;

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    public MainWindow()
    {
        // Settings, the AACS runtime and the engine were all prewarmed from App.OnStartup while
        // WPF was loading itself, so this normally only collects the finished result.
        _settingsService = StartupPrewarm.SettingsService;
        _settings = StartupPrewarm.Settings;

        try
        {
            var engine = StartupPrewarm.TakeEngine();
            _libVlc = engine.LibVlc;
            _mediaPlayer = engine.Player;
        }
        catch (Exception exception)
        {
            // A missing, blocked or mismatched native runtime would otherwise escape the
            // constructor as a raw fault dialog with nothing to act on.
            Debug.WriteLine(exception);
            MessageBox.Show(
                $"The VideoLAN playback engine could not be started.\n\n{exception.Message}\n\n" +
                "Reinstall NOIR, or check that your antivirus software is not blocking libvlc.",
                "NOIR cannot start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.Exit(2);
            throw;
        }

        _libVlc.Log += LibVlc_Log;

        InitializeComponent();
        VideoView.MediaPlayer = _mediaPlayer;
        PlaylistView.ItemsSource = _playlist;
        _playlistView = CollectionViewSource.GetDefaultView(_playlist);
        _playlistView.Filter = FilterPlaylist;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _uiTimer.Tick += UiTimer_Tick;

        _noticeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.4)
        };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            PlaybackNotice.Visibility = Visibility.Collapsed;
        };

        // Re-filtering the queue is O(playlist), so keystrokes are coalesced instead of each one
        // walking up to MaxPlaylistItems entries.
        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplyPlaylistFilter();
        };

        ApplySettingsToControls();
        _isInitializing = false;
    }

    private void AttachPlaybackEvents(long generation)
    {
        DetachPlaybackEvents();

        void Dispatch(Action action)
        {
            if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration))
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration))
                {
                    return;
                }

                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(exception);
                }
            });
        }

        void Playing(object? sender, EventArgs args)
        {
            // The native state has to be read here, on the callback thread. Re-reading it inside
            // the dispatched continuation samples a state that has already moved on, which
            // silently drops the update the event was raised for.
            if (!TryReadPlayerState(out var state))
            {
                return;
            }

            Dispatch(() => OnPlaying(state, generation));
        }

        void Paused(object? sender, EventArgs args)
        {
            if (!TryReadPlayerState(out var state))
            {
                return;
            }

            Dispatch(() => OnPaused(state));
        }

        void Stopped(object? sender, EventArgs args)
        {
            if (!TryReadPlayerState(out var state))
            {
                return;
            }

            Dispatch(() => OnStopped(state));
        }

        void EndReached(object? sender, EventArgs args)
        {
            if (!TryReadPlayerState(out var state))
            {
                return;
            }

            Dispatch(() =>
            {
                if (state == VLCState.Ended)
                {
                    HandleMediaEnded(generation);
                }
            });
        }

        void EncounteredError(object? sender, EventArgs args)
        {
            if (!TryReadPlayerState(out var state))
            {
                return;
            }

            Dispatch(() => OnEncounteredError(state));
        }

        void Buffering(object? sender, MediaPlayerBufferingEventArgs args)
        {
            var cache = args.Cache;
            Dispatch(() =>
            {
                if (cache < 100f)
                {
                    EngineStatusText.Text = $"BUFFERING {cache:0}%";
                }
            });
        }

        void LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs args)
        {
            var length = args.Length;
            Dispatch(() => TryApplyPendingResume(generation, length));
        }

        _mediaPlayer.Playing += Playing;
        _mediaPlayer.Paused += Paused;
        _mediaPlayer.Stopped += Stopped;
        _mediaPlayer.EndReached += EndReached;
        _mediaPlayer.EncounteredError += EncounteredError;
        _mediaPlayer.Buffering += Buffering;
        _mediaPlayer.LengthChanged += LengthChanged;

        _detachPlaybackEvents = () =>
        {
            _mediaPlayer.Playing -= Playing;
            _mediaPlayer.Paused -= Paused;
            _mediaPlayer.Stopped -= Stopped;
            _mediaPlayer.EndReached -= EndReached;
            _mediaPlayer.EncounteredError -= EncounteredError;
            _mediaPlayer.Buffering -= Buffering;
            _mediaPlayer.LengthChanged -= LengthChanged;
        };
    }

    /// <summary>
    /// Reads the native player state from a LibVLC callback thread, tolerating a player that is
    /// being torn down concurrently.
    /// </summary>
    private bool TryReadPlayerState(out VLCState state)
    {
        state = VLCState.NothingSpecial;
        if (_isClosing || _playbackDisposed)
        {
            return false;
        }

        try
        {
            state = _mediaPlayer.State;
            return true;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or VLCException)
        {
            return false;
        }
    }

    private void OnPlaying(VLCState state, long generation)
    {
        if (state is not (VLCState.Playing or VLCState.Buffering))
        {
            return;
        }

        CancelPlaybackStartupWatchdog();
        _isPlaying = true;
        PlayPauseButton.Content = "\uE769";
        EngineStatusText.Text = "PLAYING";
        EngineStatusDot.Fill = FindBrush("AccentBrush", Brushes.DarkSeaGreen);
        StatusText.Text = _currentItem is null ? "Playing" : $"Playing - {_currentItem.Title}";
        StatusDot.Fill = FindBrush("AccentBrush", Brushes.DarkSeaGreen);
        EmptyPlayerPanel.Visibility = Visibility.Collapsed;
        SetVideoSurfaceActive(true);
        _mediaPlayer.SetRate(_settings.PlaybackRate);
        TryApplyPendingResume(generation, _mediaPlayer.Length);
        RefreshTrackSelectors();
        RefreshVideoInfo();
    }

    private void OnPaused(VLCState state)
    {
        if (state != VLCState.Paused)
        {
            return;
        }

        CancelPlaybackStartupWatchdog();
        _isPlaying = false;
        PlayPauseButton.Content = "\uE768";
        EngineStatusText.Text = "PAUSED";
        StatusText.Text = "Paused";
    }

    private void OnStopped(VLCState state)
    {
        if (state is not (VLCState.Stopped or VLCState.NothingSpecial))
        {
            return;
        }

        CancelPlaybackStartupWatchdog();
        _isPlaying = false;
        PlayPauseButton.Content = "\uE768";
        EngineStatusText.Text = "STOPPED";
        EngineStatusDot.Fill = new SolidColorBrush(Color.FromRgb(94, 102, 114));
        SetVideoSurfaceActive(false);
    }

    private void OnEncounteredError(VLCState state)
    {
        if (state != VLCState.Error)
        {
            return;
        }

        CancelPlaybackStartupWatchdog();
        _isPlaying = false;
        PlayPauseButton.Content = "\uE768";
        if (_currentItem?.IsDisc == true)
        {
            HandleDiscPlaybackFailure(_currentItem, timedOut: false);
            return;
        }

        EngineStatusText.Text = "PLAYBACK ERROR";
        EngineStatusDot.Fill = Brushes.OrangeRed;
        StatusText.Text = "This source could not be played";
        StatusDot.Fill = Brushes.OrangeRed;
        SetVideoSurfaceActive(false);
        ShowNotice("Playback error - check the source or disc");
    }

    private void LibVlc_Log(object? sender, LogEventArgs args)
    {
        Debug.WriteLine(args.FormattedLog);
        var module = args.Module ?? string.Empty;
        var message = args.Message ?? string.Empty;
        if (!module.Contains("bluray", StringComparison.OrdinalIgnoreCase) &&
            !module.Contains("aacs", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("bluray", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("blu-ray", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("aacs", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("processing key", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("volume key", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("host certificate", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var entry = string.IsNullOrWhiteSpace(module) ? message : $"{module}: {message}";
        entry = string.Join(' ', entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (entry.Length > 320)
        {
            entry = entry[..320];
        }

        lock (_engineLogSync)
        {
            _recentDiscEngineLogs.Enqueue(entry);
            while (_recentDiscEngineLogs.Count > MaxRecentDiscEngineLogs)
            {
                _recentDiscEngineLogs.Dequeue();
            }
        }
    }

    private void ClearDiscEngineLogs()
    {
        lock (_engineLogSync)
        {
            _recentDiscEngineLogs.Clear();
        }
    }

    private string GetLatestDiscEngineLog()
    {
        lock (_engineLogSync)
        {
            return _recentDiscEngineLogs.LastOrDefault() ?? string.Empty;
        }
    }

    private void DetachPlaybackEvents()
    {
        _detachPlaybackEvents?.Invoke();
        _detachPlaybackEvents = null;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _uiTimer.Start();
        StatusText.Text = "LibVLC ready · Drop media anywhere";
        Topmost = _settings.AlwaysOnTop;

        if (_settingsService.LoadFailed)
        {
            // Saving stays disabled for this session so the unreadable-but-intact file on disk
            // is not replaced with the defaults the player fell back to.
            StatusText.Text = "Your settings could not be read · changes will not be saved";
            ShowNotice("Settings could not be read · running with defaults, nothing will be saved");
        }

        _aacsRepairTask = Task.Run(
            () => AacsService.RepairCompressedKeyDatabaseIfNeededAsync(
                AacsService.KeyDatabasePath,
                _lifetimeCancellation.Token),
            _lifetimeCancellation.Token);
        UpdateAacsStatus();
        var repairTask = _aacsRepairTask;
        try
        {
            var repairResult = await repairTask;
            if (!_isClosing)
            {
                UpdateAacsStatus();
                if (repairResult is { Success: false })
                {
                    ShowNotice(repairResult.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Window shutdown canceled startup repair.
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            if (!_isClosing)
            {
                ShowNotice("The compressed AACS key database could not be repaired");
                UpdateAacsStatus();
            }
        }

        if (_isClosing)
        {
            return;
        }

        var launchFiles = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (launchFiles.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            StatusText.Text = "Startup smoke test passed";
            Environment.ExitCode = 0;
            _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.ApplicationIdle);
            return;
        }

        if (launchFiles.Contains("--aacs-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            var aacsStatus = AacsService.CurrentStatus;
            StatusText.Text = aacsStatus.IsReady ? "AACS smoke test passed" : aacsStatus.Message;
            Environment.ExitCode = aacsStatus.IsReady ? 0 : 3;
            _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.ApplicationIdle);
            return;
        }

        if (launchFiles.Length > 0)
        {
            await ImportSourcesAsync(launchFiles, playFirst: true);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        _isClosing = true;

        // Everything below must be exception-proof: the close was already cancelled, so a throw
        // that escaped before _shutdownComplete is set would leave a window that can never be
        // closed again - and would take the process down from an `async void` handler.
        try
        {
            IsEnabled = false;
            PersistCurrentPosition(force: true);
            _settings.Volume = (int)VolumeSlider.Value;
            _settings.IsMuted = _mediaPlayer.Mute;

            _lifetimeCancellation.Cancel();
            _sourceImportCancellation?.Cancel();
            _discScanCancellation?.Cancel();
            _ejectCancellation?.Cancel();
            _settingsSaveCancellation?.Cancel();
            CancelPlaybackStartupWatchdog();
            _uiTimer.Stop();
            _noticeTimer.Stop();
            _searchDebounceTimer.Stop();

            // Any background teardown still holds the native player, so it has to finish before
            // the engine is disposed.
            await WaitForPlaybackTeardownAsync();
            DisposePlaybackResources();

            var activeOperations = new[]
            {
                _sourceImportWorker,
                _discScanWorker,
                _ejectTask,
                _aacsRepairTask,
                _settingsSaveTask,
                _playlistSaveTask
            }.Where(task => task is not null).Cast<Task>().ToArray();

            if (activeOperations.Length > 0)
            {
                using var drainTimeout = new CancellationTokenSource();
                try
                {
                    var completion = Task.WhenAll(activeOperations);
                    if (await Task.WhenAny(completion, Task.Delay(ShutdownTimeout, drainTimeout.Token)) == completion)
                    {
                        // Release the timer rather than leaving it to fire into a closed window.
                        await drainTimeout.CancelAsync();
                        await completion;
                    }
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(exception);
                }
            }

            using var saveTimeout = new CancellationTokenSource(ShutdownTimeout);
            try
            {
                await _settingsService.SaveAsync(_settings, saveTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Settings save timed out during shutdown.");
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
        finally
        {
            _shutdownComplete = true;
            _lifetimeCancellation.Dispose();
            _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>
    /// Awaits any in-flight background release of the native player, bounded so that a wedged
    /// libvlc teardown cannot block shutdown indefinitely.
    /// </summary>
    private async Task WaitForPlaybackTeardownAsync()
    {
        var teardown = _playbackTeardownTask;
        if (teardown.IsCompleted)
        {
            return;
        }

        try
        {
            await Task.WhenAny(teardown, Task.Delay(ShutdownTimeout));
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private void DisposePlaybackResources()
    {
        if (_playbackDisposed)
        {
            return;
        }

        // Stop before flipping the flag: ReleaseCurrentMedia treats a disposed engine as nothing
        // left to release, and the player must not be disposed while it still holds media.
        ReleaseCurrentMedia();
        _playbackDisposed = true;
        VideoView.MediaPlayer = null;
        _mediaPlayer.Dispose();
        _libVlc.Log -= LibVlc_Log;
        _libVlc.Dispose();
    }

    private void ApplySettingsToControls()
    {
        VolumeSlider.Value = Math.Clamp(_settings.Volume, 0, 100);
        _mediaPlayer.Volume = (int)VolumeSlider.Value;
        _mediaPlayer.Mute = _settings.IsMuted;
        UpdateMuteVisual();

        RememberPositionCheckBox.IsChecked = _settings.RememberPosition;
        AutoPlayNextCheckBox.IsChecked = _settings.AutoPlayNext;
        HardwareDecodingCheckBox.IsChecked = _settings.HardwareDecoding;
        AlwaysOnTopCheckBox.IsChecked = _settings.AlwaysOnTop;
        UpdateAacsStatus();

        ShuffleButton.Foreground = _settings.Shuffle
            ? FindBrush("AccentBrush", Brushes.DarkSeaGreen)
            : FindBrush("ProminentTextBrush", Brushes.Gainsboro);
        UpdateRepeatVisual();
        SelectSpeed(_settings.PlaybackRate);
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open media",
            Filter = MediaSourceService.OpenFileFilter,
            Multiselect = true,
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(_settings.LastFolder) ? _settings.LastFolder : null
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _settings.LastFolder = Path.GetDirectoryName(dialog.FileName) ?? _settings.LastFolder;
        ScheduleSettingsSave();
        await ImportSourcesAsync(dialog.FileNames, playFirst: true);
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a media or DVD folder",
            Multiselect = false,
            InitialDirectory = Directory.Exists(_settings.LastFolder) ? _settings.LastFolder : null
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _settings.LastFolder = dialog.FolderName;
        ScheduleSettingsSave();
        await ImportSourcesAsync([dialog.FolderName], playFirst: true, showResultNotice: true);
    }

    private async void OpenDisc_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        cancellation.CancelAfter(DiscScanTimeout);
        var previousCancellation = _discScanCancellation;
        _discScanCancellation = cancellation;
        previousCancellation?.Cancel();
        var gateEntered = false;
        Task<IReadOnlyList<DiscInfo>>? scanTask = null;
        StatusText.Text = "Looking for optical discs…";

        try
        {
            await _discScanGate.WaitAsync(cancellation.Token);
            gateEntered = true;
            scanTask = Task.Run(() => DiscService.FindVideoDiscs(cancellation.Token));
            _discScanWorker = scanTask;
            var discs = await scanTask;
            cancellation.Token.ThrowIfCancellationRequested();
            if (discs.Count == 0)
            {
                ShowNotice("No ready optical disc found — you can open a VIDEO_TS folder instead");
                StatusText.Text = "No DVD or Blu-ray disc detected";
                return;
            }

            var item = DiscService.CreateItem(discs[0]);
            if (AddItem(item, playNow: true))
            {
                ShowNotice($"Opening {discs[0].DisplayName}");
            }
        }
        catch (OperationCanceledException)
        {
            // A newer scan or window shutdown superseded this request.
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            if (!_isClosing && ReferenceEquals(_discScanCancellation, cancellation))
            {
                StatusText.Text = "Optical disc scan failed";
                ShowNotice("Optical drives could not be checked");
            }
        }
        finally
        {
            if (ReferenceEquals(_discScanWorker, scanTask))
            {
                _discScanWorker = null;
            }

            if (ReferenceEquals(_discScanCancellation, cancellation))
            {
                _discScanCancellation = null;
            }

            if (gateEntered)
            {
                _discScanGate.Release();
            }

            cancellation.Dispose();
        }
    }

    private async void EjectDisc_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || !EjectDiscButton.IsEnabled)
        {
            return;
        }

        EjectDiscButton.IsEnabled = false;
        var selectedSource = (PlaylistView.SelectedItem as PlaylistItem)?.Source;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var previousCancellation = _ejectCancellation;
        _ejectCancellation = cancellation;
        previousCancellation?.Cancel();
        var ejectTask = EjectDiscAsync(selectedSource, cancellation.Token);
        _ejectTask = ejectTask;
        try
        {
            await ejectTask;
        }
        catch (OperationCanceledException)
        {
            // Window shutdown or a superseding optical operation canceled ejection.
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            if (!_isClosing)
            {
                StatusText.Text = "The disc could not be ejected";
                ShowNotice("Disc eject failed");
            }
        }
        finally
        {
            if (ReferenceEquals(_ejectTask, ejectTask))
            {
                _ejectTask = null;
            }

            if (ReferenceEquals(_ejectCancellation, cancellation))
            {
                _ejectCancellation = null;
            }

            cancellation.Dispose();
            if (!_isClosing)
            {
                EjectDiscButton.IsEnabled = true;
            }
        }
    }

    private async Task EjectDiscAsync(string? selectedSource, CancellationToken cancellationToken)
    {
        _discScanCancellation?.Cancel();
        var gateEntered = false;
        try
        {
            await _discScanGate.WaitAsync(cancellationToken);
            gateEntered = true;
            cancellationToken.ThrowIfCancellationRequested();

            var driveRoots = await Task.Run(
                () => DiscService.FindOpticalDriveRoots(cancellationToken),
                cancellationToken);
            var driveRoot = DiscService.SelectEjectTarget(
                driveRoots,
                _currentItem?.Source,
                selectedSource);
            if (driveRoot is null)
            {
                if (driveRoots.Count == 0)
                {
                    StatusText.Text = "No optical drive is available";
                    ShowNotice("No optical drive found");
                }
                else
                {
                    StatusText.Text = "Choose a disc from the drive to eject";
                    ShowNotice("Multiple optical drives found · Select a disc first");
                }

                return;
            }

            var currentDiscItem = _currentItem?.IsDisc == true &&
                                  DiscService.IsDiscSourceOnDrive(_currentItem.Source, driveRoot)
                ? _currentItem
                : null;
            if (currentDiscItem is not null)
            {
                PersistCurrentPosition(force: true);
                currentDiscItem.IsPlaying = false;
                // The drive stays locked until libvlc lets the disc go, so the eject has to wait
                // for the teardown - off the dispatcher rather than blocking it.
                await ReleaseCurrentMedia(synchronous: false);
                PlayPauseButton.Content = "\uE768";
            }

            StatusText.Text = $"Ejecting {driveRoot}…";
            var result = await DiscService.EjectAsync(driveRoot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                EngineStatusText.Text = "EJECT FAILED";
                EngineStatusDot.Fill = Brushes.OrangeRed;
                StatusText.Text = "The disc could not be ejected";
                ShowNotice(result.Message);
                return;
            }

            var staleDiscItems = _playlist
                .Where(item => item.IsDisc && DiscService.IsDiscSourceOnDrive(item.Source, driveRoot))
                .ToArray();
            foreach (var item in staleDiscItems)
            {
                item.IsPlaying = false;
                _playlist.Remove(item);
                _playlistSources.Remove(item.Source);
            }

            if (currentDiscItem is not null && ReferenceEquals(_currentItem, currentDiscItem))
            {
                _currentItem = null;
                _currentIndex = -1;
                ResetNowPlaying();
            }
            else if (_currentItem is not null)
            {
                _currentIndex = _playlist.IndexOf(_currentItem);
            }

            RefreshQueueState();
            StatusText.Text = result.Message;
            ShowNotice(result.Message);
        }
        finally
        {
            if (gateEntered)
            {
                _discScanGate.Release();
            }
        }
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenLocationWindow { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.MediaLocation))
        {
            return;
        }

        AddItem(MediaSourceService.CreateNetworkItem(dialog.MediaLocation.Trim()), playNow: true);
    }

    private async Task<int> ImportSourcesAsync(
        IEnumerable<string> sources,
        bool playFirst,
        bool showResultNotice = false)
    {
        var sourceList = sources.Where(source => !string.IsNullOrWhiteSpace(source)).ToArray();
        if (_isClosing || sourceList.Length == 0)
        {
            return 0;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        cancellation.CancelAfter(ImportTimeout);
        var previousCancellation = _sourceImportCancellation;
        _sourceImportCancellation = cancellation;
        previousCancellation?.Cancel();

        PlaylistItem? firstAdded = null;
        var added = 0;
        var gateEntered = false;
        Task? producer = null;
        var diagnostics = new MediaScanDiagnostics();
        StatusText.Text = "Scanning media…";

        try
        {
            await _sourceImportGate.WaitAsync(cancellation.Token);
            gateEntered = true;
            cancellation.Token.ThrowIfCancellationRequested();

            var existingSources = new HashSet<string>(_playlistSources, StringComparer.OrdinalIgnoreCase);
            var maximumNewItems = Math.Max(0, MaxPlaylistItems - existingSources.Count);
            var channel = Channel.CreateBounded<PlaylistItem>(new BoundedChannelOptions(ImportChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            producer = Task.Run(() => ProduceImportItemsAsync(
                channel.Writer,
                sourceList,
                existingSources,
                maximumNewItems,
                diagnostics,
                cancellation.Token));
            _sourceImportWorker = producer;

            var batchCount = 0;
            await foreach (var item in channel.Reader.ReadAllAsync(cancellation.Token))
            {
                if (_playlist.Count >= MaxPlaylistItems || !_playlistSources.Add(item.Source))
                {
                    continue;
                }

                _playlist.Add(item);
                firstAdded ??= item;
                added++;
                batchCount++;
                if (batchCount >= ImportBatchSize)
                {
                    batchCount = 0;
                    RefreshQueueState();
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }

            await producer;

            RefreshQueueState();
            if (firstAdded is not null && playFirst)
            {
                PlayItem(firstAdded);
            }
            else if (added > 0)
            {
                StatusText.Text = $"Added {added:N0} item{(added == 1 ? string.Empty : "s")} to the queue";
            }
            else
            {
                StatusText.Text = "Ready";
            }

            if (diagnostics.Faulted)
            {
                // A folder walk or playlist read was cut short by an I/O error. Reporting only
                // the count would present a truncated import as a complete one.
                StatusText.Text = added > 0
                    ? $"Added {added:N0} item{(added == 1 ? string.Empty : "s")} · some locations could not be read"
                    : "No media could be read from those locations";
                ShowNotice("Some folders or playlists could not be fully scanned");
            }
            else if (showResultNotice)
            {
                ShowNotice(added > 0
                    ? $"Added {added:N0} item{(added == 1 ? string.Empty : "s")}"
                    : "No new supported media found");
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing && ReferenceEquals(_sourceImportCancellation, cancellation))
            {
                StatusText.Text = "Ready";
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            if (!_isClosing && ReferenceEquals(_sourceImportCancellation, cancellation))
            {
                StatusText.Text = $"Media scan failed · {exception.Message}";
                ShowNotice("Some sources could not be scanned");
            }
        }
        finally
        {
            cancellation.Cancel();
            if (producer is not null)
            {
                try
                {
                    await producer;
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is the normal completion path for a superseded scan.
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(exception);
                }
            }

            if (ReferenceEquals(_sourceImportWorker, producer))
            {
                _sourceImportWorker = null;
            }

            if (added > 0 && !_isClosing)
            {
                RefreshQueueState();
            }

            if (ReferenceEquals(_sourceImportCancellation, cancellation))
            {
                _sourceImportCancellation = null;
            }

            if (gateEntered)
            {
                _sourceImportGate.Release();
            }

            cancellation.Dispose();
        }

        return added;
    }

    private static async Task ProduceImportItemsAsync(
        ChannelWriter<PlaylistItem> writer,
        IReadOnlyList<string> sources,
        HashSet<string> existingSources,
        int maximumItems,
        MediaScanDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        Exception? completionError = null;
        try
        {
            foreach (var item in EnumerateImportItems(
                         sources, existingSources, maximumItems, diagnostics, cancellationToken))
            {
                await writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            completionError = exception;
            throw;
        }
        finally
        {
            writer.TryComplete(completionError);
        }
    }

    private static IEnumerable<PlaylistItem> EnumerateImportItems(
        IReadOnlyList<string> sources,
        HashSet<string> existingSources,
        int maximumItems,
        MediaScanDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        if (maximumItems <= 0)
        {
            yield break;
        }

        if (sources.Count == 1 && Directory.Exists(sources[0]) &&
            DiscService.CreateFromFolder(sources[0]) is { } discItem)
        {
            if (existingSources.Add(discItem.Source))
            {
                yield return discItem;
            }

            yield break;
        }

        var yielded = 0;
        foreach (var source in MediaSourceService.ExpandFiles(sources, cancellationToken, diagnostics))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = MediaSourceService.TryNormalizeNetworkLocation(source, out var networkLocation)
                ? MediaSourceService.CreateNetworkItem(networkLocation)
                : MediaSourceService.CreateFileItem(source);

            if (existingSources.Add(item.Source))
            {
                yield return item;
                yielded++;
                if (yielded >= maximumItems)
                {
                    yield break;
                }
            }
        }
    }

    private bool AddItem(PlaylistItem item, bool playNow)
    {
        var existing = _playlist.FirstOrDefault(candidate =>
            string.Equals(candidate.Source, item.Source, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            if (_playlist.Count >= MaxPlaylistItems)
            {
                ShowNotice($"Queue limit reached ({MaxPlaylistItems:N0} items)");
                return false;
            }

            _playlistSources.Add(item.Source);
            _playlist.Add(item);
            existing = item;
        }

        RefreshQueueState();
        if (playNow)
        {
            return PlayItem(existing);
        }

        return true;
    }

    private bool PlayItem(PlaylistItem item, bool allowResume = true, long requestedPosition = -1)
    {
        if (_isClosing || _playbackDisposed)
        {
            return false;
        }

        if (item.IsDisc &&
            _aacsRepairTask is { IsCompleted: false } &&
            DiscService.IsAacsProtectedSource(item.Source))
        {
            StatusText.Text = "Preparing the AACS key database…";
            ShowNotice("AACS key database repair is still running · Try again shortly");
            return false;
        }

        PersistCurrentPosition(force: true);

        if (_currentItem is not null)
        {
            _currentItem.IsPlaying = false;
        }

        ReleaseCurrentMedia();

        _currentItem = item;
        _currentIndex = _playlist.IndexOf(item);
        _currentItem.IsPlaying = true;
        PlaylistView.SelectedItem = item;
        PlaylistView.ScrollIntoView(item);
        UpdateNowPlaying(item);
        SetVideoSurfaceActive(false);

        if (item.IsDisc && DiscService.IsAacsProtectedSource(item.Source))
        {
            var aacsStatus = AacsService.CurrentStatus;
            if (!aacsStatus.IsReady)
            {
                item.IsPlaying = false;
                EngineStatusText.Text = "AACS REQUIRED";
                EngineStatusDot.Fill = Brushes.OrangeRed;
                StatusText.Text = aacsStatus.Message;
                StatusDot.Fill = Brushes.OrangeRed;
                ShowNotice("Protected Blu-ray · Configure AACS in Quick Settings");
                MessageBox.Show(
                    this,
                    $"NOIR cannot load its AACS runtime.\n\n{aacsStatus.Message}\n\nYou can choose another compatible runtime in Quick Settings or use licensed Blu-ray playback software.",
                    "AACS component required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                UpdateAacsStatus();
                SettingsPopup.IsOpen = true;
                return false;
            }

            if (!aacsStatus.KeyDatabaseFound)
            {
                item.IsPlaying = false;
                EngineStatusText.Text = "AACS KEY REQUIRED";
                EngineStatusDot.Fill = Brushes.OrangeRed;
                StatusText.Text = aacsStatus.Message;
                StatusDot.Fill = Brushes.OrangeRed;
                ShowNotice("Protected Blu-ray · a valid plaintext KEYDB.cfg is required");
                MessageBox.Show(
                    this,
                    $"NOIR cannot unlock this protected Blu-ray.\n\n{aacsStatus.Message}\n\nUse Download AACS keys to install and extract the current database, or place a lawfully obtained plaintext KEYDB.cfg in:\n{AacsService.KeyDatabasePath}\n\nUse Quick Settings → Blu-ray AACS → Open key folder.",
                    "Protected Blu-ray credentials required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                UpdateAacsStatus();
                SettingsPopup.IsOpen = true;
                return false;
            }
        }

        try
        {
            _currentMedia = CreateMedia(item);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            item.IsPlaying = false;
            EngineStatusText.Text = "SOURCE ERROR";
            StatusText.Text = "The selected source is unavailable";
            ShowNotice("The selected source could not be opened");
            return false;
        }
        if (_rotation != 0)
        {
            _currentMedia.AddOption(":video-filter=transform");
            _currentMedia.AddOption($":transform-type={_rotation}");
        }

        _pendingResumePosition = 0;
        if (requestedPosition >= 0)
        {
            _pendingResumePosition = requestedPosition;
        }
        else if (allowResume && _settings.RememberPosition &&
            _settings.ResumePositions.TryGetValue(item.Source, out var savedPosition) && savedPosition >= 10_000)
        {
            _pendingResumePosition = savedPosition;
        }

        var generation = Interlocked.Increment(ref _playbackGeneration);
        AttachPlaybackEvents(generation);
        _positionPersistClock.Restart();
        AddRecent(item.Source);
        ScheduleSettingsSave();
        ClearDiscEngineLogs();
        EngineStatusText.Text = item.IsDisc ? "READING DISC" : item.IsNetwork ? "CONNECTING" : "OPENING";
        EngineStatusDot.Fill = Brushes.Goldenrod;
        StatusText.Text = $"Opening · {item.Title}";
        try
        {
            // Same monitor as the teardown path, so a start can never interleave with a stop.
            bool started;
            lock (_playbackEngineSync)
            {
                started = _mediaPlayer.Play(_currentMedia);
            }

            if (!started)
            {
                throw new InvalidOperationException("LibVLC rejected the media source.");
            }

            StartPlaybackStartupWatchdog(item, generation);
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            item.IsPlaying = false;
            ReleaseCurrentMedia();
            EngineStatusText.Text = "PLAYBACK ERROR";
            StatusText.Text = "The selected source could not be played";
            ShowNotice("The selected source could not be played");
            return false;
        }
    }

    private Media CreateMedia(PlaylistItem item)
    {
        if (item.IsDisc || item.IsNetwork)
        {
            return new Media(_libVlc, item.Source, FromType.FromLocation);
        }

        if (!File.Exists(item.Source))
        {
            throw new FileNotFoundException("The media file no longer exists.", item.Source);
        }

        return new Media(_libVlc, item.Source, FromType.FromPath);
    }

    /// <summary>
    /// Releases the media currently loaded into the engine.
    /// </summary>
    /// <param name="synchronous">
    /// When true the native stop runs inline. This is required on the track-change path, where
    /// the very next statement hands new media to the same player and the two calls must not
    /// interleave. Every other caller passes false: LibVLC 3's stop is a blocking teardown that
    /// takes hundreds of milliseconds on discs and network streams, and running it on the
    /// dispatcher freezes the window.
    /// </param>
    /// <returns>The background teardown, or a completed task when it ran inline.</returns>
    private Task ReleaseCurrentMedia(bool synchronous = true)
    {
        var generation = Interlocked.Increment(ref _playbackGeneration);
        try
        {
            DetachPlaybackEvents();
        }
        catch (ObjectDisposedException)
        {
            // The native player is already gone during a repeated shutdown path.
        }

        CancelPlaybackStartupWatchdog();

        var player = _mediaPlayer;
        var media = _currentMedia;
        _currentMedia = null;
        _pendingResumePosition = 0;
        _isPlaying = false;

        if (_playbackDisposed)
        {
            // Shutdown already tore the engine down; the media handle went with it.
            return Task.CompletedTask;
        }

        if (synchronous)
        {
            StopPlaybackEngine(player, media, generation);
            return Task.CompletedTask;
        }

        var previous = _playbackTeardownTask;
        var teardown = Task.Run(() => StopPlaybackEngine(player, media, generation));
        _playbackTeardownTask = previous.IsCompleted ? teardown : Task.WhenAll(previous, teardown);
        return teardown;
    }

    /// <summary>
    /// Stops the native player and disposes the media it was holding. Serialized so that a
    /// background teardown and a foreground track change can never both be inside libvlc.
    /// </summary>
    private void StopPlaybackEngine(VlcMediaPlayer player, Media? media, long generation)
    {
        try
        {
            lock (_playbackEngineSync)
            {
                // A queued teardown may reach the engine after a newer PlayItem already took it.
                // Stopping then would kill the playback that just started.
                if (generation == Interlocked.Read(ref _playbackGeneration))
                {
                    player.Stop();
                    player.Media = null;
                }
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException or VLCException)
        {
            // Disposal is idempotent from the window's perspective.
            Debug.WriteLine(exception);
        }
        finally
        {
            // The media object must outlive the stop that is still reading from it.
            media?.Dispose();
        }
    }

    private void TryApplyPendingResume(long generation, long length)
    {
        if (_pendingResumePosition <= 0 ||
            generation != Interlocked.Read(ref _playbackGeneration) ||
            length <= 0)
        {
            return;
        }

        var resumeAt = _pendingResumePosition;
        _pendingResumePosition = 0;
        if (length > resumeAt + 10_000)
        {
            _mediaPlayer.Time = resumeAt;
            ShowNotice($"Resumed at {FormatTime(resumeAt)}");
        }
        else if (_currentItem is not null)
        {
            _settings.ResumePositions.Remove(_currentItem.Source);
            ScheduleSettingsSave();
        }
    }

    private void StartPlaybackStartupWatchdog(PlaylistItem item, long generation)
    {
        CancelPlaybackStartupWatchdog();
        var timeout = PlaybackStartupPolicy.GetTimeout(item.IsNetwork, item.IsDisc);
        if (timeout is null)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _playbackStartupCancellation = cancellation;
        _ = MonitorPlaybackStartupAsync(item, generation, timeout.Value, cancellation.Token);
    }

    private async Task MonitorPlaybackStartupAsync(
        PlaylistItem item,
        long generation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (_isClosing ||
                    generation != Interlocked.Read(ref _playbackGeneration) ||
                    !ReferenceEquals(_currentItem, item) ||
                    _isPlaying)
                {
                    return;
                }

                if (item.IsDisc)
                {
                    HandleDiscPlaybackFailure(item, timedOut: true);
                }
                else
                {
                    item.IsPlaying = false;
                    ReleaseCurrentMedia(synchronous: false);
                    EngineStatusText.Text = "CONNECTION TIMEOUT";
                    EngineStatusDot.Fill = Brushes.OrangeRed;
                    StatusText.Text = "The network stream did not respond";
                    StatusDot.Fill = Brushes.OrangeRed;
                    ShowNotice("Network connection timed out");
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Playback started, changed, or the application is shutting down.
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private void CancelPlaybackStartupWatchdog()
    {
        var cancellation = _playbackStartupCancellation;
        _playbackStartupCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void HandleDiscPlaybackFailure(PlaylistItem item, bool timedOut)
    {
        var isAacsProtected = DiscService.IsAacsProtectedSource(item.Source);
        var aacsStatus = AacsService.CurrentStatus;
        var engineDetail = GetLatestDiscEngineLog();
        var failure = PlaybackStartupPolicy.DescribeDiscFailure(
            isAacsProtected,
            aacsStatus.KeyDatabaseFound,
            timedOut);

        item.IsPlaying = false;
        ReleaseCurrentMedia(synchronous: false);
        PlayPauseButton.Content = "\uE768";
        EngineStatusText.Text = failure.EngineStatus;
        EngineStatusDot.Fill = Brushes.OrangeRed;
        StatusText.Text = failure.StatusText;
        StatusDot.Fill = Brushes.OrangeRed;
        SetVideoSurfaceActive(false);
        ShowNotice(failure.Notice);

        var diagnostic = string.IsNullOrWhiteSpace(engineDetail)
            ? string.Empty
            : $"\n\nEngine detail: {engineDetail}";
        MessageBox.Show(
            this,
            failure.DialogMessage + diagnostic,
            failure.DialogTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        if (isAacsProtected)
        {
            UpdateAacsStatus();
            SettingsPopup.IsOpen = true;
        }
    }

    private void UpdateNowPlaying(PlaylistItem item)
    {
        NowPlayingTitle.Text = item.Title;
        NowPlayingSource.Text = item.Detail;
        WindowTitleText.Text = item.Title;
        Title = $"{item.Title} — NOIR";
        FormatBadge.Text = item.IsDisc ? "DISC" : item.IsNetwork ? "LIVE" : item.KindText;
        ResolutionBadge.Text = "ANALYZING";
        ArtworkIcon.Text = IsAudioFile(item.Source) ? "\uE8D6" : "\uE8B2";
        EmptyPlayerPanel.Visibility = Visibility.Collapsed;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void TogglePlayPause()
    {
        if (_currentItem is null)
        {
            if (_playlist.Count > 0)
            {
                PlayItem(_playlist[0]);
            }
            else
            {
                OpenFile_Click(this, new RoutedEventArgs());
            }

            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }
        else if (_mediaPlayer.Media is null)
        {
            PlayItem(_currentItem);
        }
        else
        {
            _mediaPlayer.Play();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        PersistCurrentPosition(force: true);
        if (_currentItem is not null)
        {
            // The Stopped event clears _isPlaying but never the item's own flag, which is what
            // draws the playing marker in the queue.
            _currentItem.IsPlaying = false;
        }

        _mediaPlayer.Stop();
        TimelineSlider.Value = 0;
        ElapsedText.Text = "0:00";
        RemainingText.Text = "−0:00";
        StatusText.Text = "Stopped";
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => PlayPrevious();

    private void Next_Click(object sender, RoutedEventArgs e) => PlayNext(userInitiated: true);

    private void PlayPrevious()
    {
        if (_mediaPlayer.Time > 5_000)
        {
            _mediaPlayer.Time = 0;
            return;
        }

        if (_playlist.Count == 0)
        {
            return;
        }

        var nextIndex = _currentIndex <= 0 ? _playlist.Count - 1 : _currentIndex - 1;
        PlayItem(_playlist[nextIndex]);
    }

    private bool PlayNext(bool userInitiated)
    {
        if (_playlist.Count == 0)
        {
            return false;
        }

        int nextIndex;
        if (_settings.Shuffle && _playlist.Count > 1)
        {
            do
            {
                nextIndex = _random.Next(_playlist.Count);
            } while (nextIndex == _currentIndex);
        }
        else
        {
            nextIndex = _currentIndex + 1;
            if (nextIndex >= _playlist.Count)
            {
                if (_settings.RepeatMode == "All" || userInitiated)
                {
                    nextIndex = 0;
                }
                else
                {
                    return false;
                }
            }
        }

        return PlayItem(_playlist[nextIndex]);
    }

    private void HandleMediaEnded(long generation)
    {
        if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration))
        {
            return;
        }

        _isPlaying = false;
        if (_currentItem is not null)
        {
            _settings.ResumePositions.Remove(_currentItem.Source);
            ScheduleSettingsSave();
        }

        if (_settings.RepeatMode == "One" && _currentItem is not null)
        {
            PlayItem(_currentItem, allowResume: false);
            return;
        }

        if ((_settings.AutoPlayNext || _settings.RepeatMode == "All") &&
            PlayNext(userInitiated: false))
        {
            return;
        }

        PlayPauseButton.Content = "\uE768";
        EngineStatusText.Text = "FINISHED";
        StatusText.Text = "Playback finished";
        SetVideoSurfaceActive(false);
    }

    private void Timeline_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isScrubbing = true;

    private void Timeline_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mediaPlayer.Length > 0)
        {
            _mediaPlayer.Time = (long)TimelineSlider.Value;
        }

        _isScrubbing = false;
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        // DispatcherTimer.Stop does not remove a tick that is already queued, and shutdown keeps
        // pumping the dispatcher while it awaits. Without this guard that stale tick reaches a
        // disposed native player.
        if (_isClosing || _playbackDisposed || _currentItem is null)
        {
            return;
        }

        var length = Math.Max(0, _mediaPlayer.Length);
        var time = Math.Max(0, _mediaPlayer.Time);
        TryApplyPendingResume(Interlocked.Read(ref _playbackGeneration), length);
        if (!_isScrubbing && length > 0)
        {
            TimelineSlider.Maximum = length;
            TimelineSlider.Value = Math.Min(time, length);
        }

        ElapsedText.Text = FormatTime(time);
        RemainingText.Text = $"−{FormatTime(Math.Max(0, length - time))}";

        if (length > 0 && _currentItem.DurationMilliseconds != length)
        {
            _currentItem.DurationMilliseconds = length;
        }

        if (_isPlaying && _settings.RememberPosition &&
            _positionPersistClock.Elapsed >= TimeSpan.FromSeconds(5))
        {
            PersistCurrentPosition(force: false);
        }

        if (ResolutionBadge.Text == "ANALYZING" && time > 500)
        {
            RefreshVideoInfo();
        }
    }

    private void PersistCurrentPosition(bool force)
    {
        if (_playbackDisposed || !_settings.RememberPosition || _currentItem is null || _currentItem.IsNetwork)
        {
            return;
        }

        long time;
        long length;
        try
        {
            time = _mediaPlayer.Time;
            length = _mediaPlayer.Length;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or VLCException)
        {
            Debug.WriteLine(exception);
            return;
        }

        if (time >= 10_000 && (length <= 0 || time < length - 10_000))
        {
            // Re-insert so the entry moves to the end: ResumePositions is trimmed oldest-first.
            _settings.ResumePositions.Remove(_currentItem.Source);
            _settings.ResumePositions[_currentItem.Source] = time;
        }
        else if (length > 0 && time >= length - 10_000)
        {
            _settings.ResumePositions.Remove(_currentItem.Source);
        }

        _positionPersistClock.Restart();
        if (force)
        {
            TrimResumeHistory();
            ScheduleSettingsSave();
        }
    }

    private void RefreshVideoInfo()
    {
        try
        {
            uint width = 0;
            uint height = 0;
            if (_mediaPlayer.Size(0, ref width, ref height) && width > 0)
            {
                ResolutionBadge.Text = $"{width}×{height}";
            }
            else if (_currentItem is not null && IsAudioFile(_currentItem.Source))
            {
                ResolutionBadge.Text = "AUDIO";
            }
            else
            {
                ResolutionBadge.Text = "VIDEO";
            }
        }
        catch
        {
            ResolutionBadge.Text = "MEDIA";
        }
    }

    private void RefreshTrackSelectors()
    {
        // Repopulating the combos raises SelectionChanged, which would otherwise be taken for a
        // user choice: it re-issues SetAudioTrack/SetSpu against the engine and pops a notice on
        // every playback start.
        _isRefreshingTracks = true;
        try
        {
            AudioTrackComboBox.Items.Clear();
            foreach (var track in _mediaPlayer.AudioTrackDescription)
            {
                AudioTrackComboBox.Items.Add(new ComboBoxItem { Content = track.Name, Tag = track.Id });
            }

            SelectTrack(AudioTrackComboBox, _mediaPlayer.AudioTrack);

            SubtitleTrackComboBox.Items.Clear();
            SubtitleTrackComboBox.Items.Add(new ComboBoxItem { Content = "Subtitles off", Tag = -1 });
            foreach (var track in _mediaPlayer.SpuDescription.Where(track => track.Id >= 0))
            {
                SubtitleTrackComboBox.Items.Add(new ComboBoxItem { Content = track.Name, Tag = track.Id });
            }

            SelectTrack(SubtitleTrackComboBox, _mediaPlayer.Spu);
        }
        catch
        {
            // Track metadata is not available for every stream type.
        }
        finally
        {
            _isRefreshingTracks = false;
        }
    }

    private static void SelectTrack(ComboBox comboBox, int trackId)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (Convert.ToInt32(item.Tag, CultureInfo.InvariantCulture) == trackId)
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        // Only fall back to the first entry when the engine really has no track selected. On a
        // disc the descriptions can lag behind the active id, and for subtitles entry zero is
        // "Subtitles off" - selecting it would misreport subtitles that are actually on.
        if (trackId < 0 && comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private void AudioTrack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isRefreshingTracks ||
            AudioTrackComboBox.SelectedItem is not ComboBoxItem item || item.Tag is null)
        {
            return;
        }

        _mediaPlayer.SetAudioTrack(Convert.ToInt32(item.Tag, CultureInfo.InvariantCulture));
        ShowNotice($"Audio · {item.Content}");
    }

    private void SubtitleTrack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isRefreshingTracks ||
            SubtitleTrackComboBox.SelectedItem is not ComboBoxItem item || item.Tag is null)
        {
            return;
        }

        _mediaPlayer.SetSpu(Convert.ToInt32(item.Tag, CultureInfo.InvariantCulture));
        ShowNotice(item.Tag.ToString() == "-1" ? "Subtitles off" : $"Subtitles · {item.Content}");
    }

    private void AddSubtitle_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is null)
        {
            ShowNotice("Open media before loading subtitles");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Load subtitles",
            Filter = "Subtitle files|*.srt;*.ass;*.ssa;*.sub;*.vtt;*.idx|All files|*.*",
            Multiselect = false,
            InitialDirectory = File.Exists(_currentItem.Source) ? Path.GetDirectoryName(_currentItem.Source) : null
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var success = _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, new Uri(dialog.FileName).AbsoluteUri, true);
        ShowNotice(success ? $"Loaded subtitles · {Path.GetFileName(dialog.FileName)}" : "Could not load that subtitle file");
        if (success)
        {
            RefreshTrackSelectors();
        }
    }

    private void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is null)
        {
            ShowNotice("Nothing to capture yet");
            return;
        }

        try
        {
            Directory.CreateDirectory(_settings.SnapshotFolder);
            var safeTitle = string.Concat(_currentItem.Title.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            var fileName = $"{safeTitle} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png";
            var path = Path.Combine(_settings.SnapshotFolder, fileName);
            var success = _mediaPlayer.TakeSnapshot(0, path, 0, 0);
            ShowNotice(success ? $"Snapshot saved · {fileName}" : "Snapshot is not available for this source");
        }
        catch
        {
            ShowNotice("The snapshot could not be saved");
        }
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mediaPlayer is null || _isInitializing)
        {
            return;
        }

        _mediaPlayer.Volume = (int)e.NewValue;
        if (e.NewValue > 0 && _mediaPlayer.Mute)
        {
            _mediaPlayer.Mute = false;
        }

        _settings.Volume = (int)e.NewValue;
        _settings.IsMuted = _mediaPlayer.Mute;
        UpdateMuteVisual();
        ScheduleSettingsSave();
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        _mediaPlayer.Mute = !_mediaPlayer.Mute;
        _settings.IsMuted = _mediaPlayer.Mute;
        UpdateMuteVisual();
        ScheduleSettingsSave();
        ShowNotice(_mediaPlayer.Mute ? "Muted" : $"Volume · {(int)VolumeSlider.Value}%");
    }

    private void UpdateMuteVisual()
    {
        MuteButton.Content = _mediaPlayer.Mute || VolumeSlider.Value <= 0 ? "\uE74F" : "\uE767";
        MuteButton.Foreground = _mediaPlayer.Mute
            ? FindBrush("AccentBrush", Brushes.DarkSeaGreen)
            : FindBrush("ProminentTextBrush", Brushes.Gainsboro);
    }

    private void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedComboBox.SelectedItem is not ComboBoxItem item || item.Tag is null ||
            !float.TryParse(item.Tag.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
        {
            return;
        }

        _settings.PlaybackRate = rate;
        ScheduleSettingsSave();
        if (RateBadge is not null)
        {
            RateBadge.Text = $"{rate:0.00}×";
        }
        if (_mediaPlayer.Media is not null)
        {
            _mediaPlayer.SetRate(rate);
        }
    }

    private void SelectSpeed(float rate)
    {
        var selected = SpeedComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            float.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var itemRate) &&
            Math.Abs(itemRate - rate) < 0.01f);
        SpeedComboBox.SelectedItem = selected ?? SpeedComboBox.Items[3];
        RateBadge.Text = $"{rate:0.00}×";
    }

    private void AspectRatio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mediaPlayer is null || AspectRatioComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var ratio = item.Tag?.ToString();
        _mediaPlayer.AspectRatio = string.IsNullOrWhiteSpace(ratio) ? null : ratio;
        if (!_isInitializing)
        {
            ShowNotice($"Aspect ratio · {item.Content}");
        }
    }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is null)
        {
            return;
        }

        var position = _mediaPlayer.Time;
        _rotation = (_rotation + 90) % 360;
        PlayItem(_currentItem, allowResume: false, requestedPosition: position);
        ShowNotice(_rotation == 0 ? "Rotation reset" : $"Rotated {_rotation}°");
    }

    private void PreviousChapter_Click(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.PreviousChapter();
        ShowNotice("Previous chapter");
    }

    private void NextChapter_Click(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.NextChapter();
        ShowNotice("Next chapter");
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _settings.Shuffle = !_settings.Shuffle;
        ShuffleButton.Foreground = _settings.Shuffle
            ? FindBrush("AccentBrush", Brushes.DarkSeaGreen)
            : FindBrush("ProminentTextBrush", Brushes.Gainsboro);
        ScheduleSettingsSave();
        ShowNotice(_settings.Shuffle ? "Shuffle on" : "Shuffle off");
    }

    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _settings.RepeatMode = _settings.RepeatMode switch
        {
            "Off" => "All",
            "All" => "One",
            _ => "Off"
        };
        UpdateRepeatVisual();
        ScheduleSettingsSave();
        ShowNotice($"Repeat · {_settings.RepeatMode}");
    }

    private void UpdateRepeatVisual()
    {
        RepeatButton.Foreground = _settings.RepeatMode == "Off"
            ? FindBrush("ProminentTextBrush", Brushes.Gainsboro)
            : FindBrush("AccentBrush", Brushes.DarkSeaGreen);
        RepeatButton.ToolTip = $"Repeat: {_settings.RepeatMode} (R)";
    }

    private void PlaylistView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistView.SelectedItem is PlaylistItem item)
        {
            PlayItem(item);
        }
    }

    private void PlaylistView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection is intentionally separate from playback; double-click or Enter starts it.
    }

    private void PlaylistSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is not null)
        {
            SearchHint.Visibility = string.IsNullOrEmpty(PlaylistSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void ApplyPlaylistFilter()
    {
        if (_isClosing)
        {
            return;
        }

        // The query is normalised once per filter pass rather than once per item.
        _playlistFilterQuery = PlaylistSearchBox.Text?.Trim() ?? string.Empty;
        _playlistView?.Refresh();
    }

    private bool FilterPlaylist(object item)
    {
        if (_playlistFilterQuery.Length == 0)
        {
            return true;
        }

        // Ordinal comparison: culture-sensitive matching costs several times more per item and
        // buys nothing for file names and paths.
        return item is PlaylistItem playlistItem &&
               (playlistItem.Title.Contains(_playlistFilterQuery, StringComparison.OrdinalIgnoreCase) ||
                playlistItem.Detail.Contains(_playlistFilterQuery, StringComparison.OrdinalIgnoreCase));
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistView.SelectedItem is not PlaylistItem item)
        {
            return;
        }

        var wasCurrent = ReferenceEquals(item, _currentItem);
        if (wasCurrent)
        {
            PersistCurrentPosition(force: true);
        }

        item.IsPlaying = false;
        _playlist.Remove(item);
        _playlistSources.Remove(item.Source);
        if (wasCurrent)
        {
            ReleaseCurrentMedia(synchronous: false);
            _currentItem = null;
            _currentIndex = -1;
            ResetNowPlaying();
        }
        else if (_currentItem is not null)
        {
            _currentIndex = _playlist.IndexOf(_currentItem);
        }

        RefreshQueueState();
    }

    private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
    {
        _sourceImportCancellation?.Cancel();
        PersistCurrentPosition(force: true);
        ReleaseCurrentMedia(synchronous: false);
        foreach (var item in _playlist)
        {
            item.IsPlaying = false;
        }
        _playlist.Clear();
        _playlistSources.Clear();
        _currentItem = null;
        _currentIndex = -1;
        ResetNowPlaying();
        RefreshQueueState();
        ShowNotice("Queue cleared");
    }

    private async void SavePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        if (_playlist.Count == 0)
        {
            ShowNotice("Add media before saving a playlist");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save playlist",
            Filter = "M3U8 playlist|*.m3u8|M3U playlist|*.m3u",
            FileName = "Noir playlist.m3u8",
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var items = _playlist.ToArray();
        using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        writeCancellation.CancelAfter(FileWriteTimeout);
        SavePlaylistButton.IsEnabled = false;
        StatusText.Text = "Saving playlist…";
        try
        {
            var saveTask = WritePlaylistAsync(dialog.FileName, items, writeCancellation.Token);
            _playlistSaveTask = saveTask;
            await saveTask;
            if (_isClosing)
            {
                return;
            }
            ShowNotice($"Playlist saved · {Path.GetFileName(dialog.FileName)}");
            StatusText.Text = "Playlist saved";
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
            {
                ShowNotice("Playlist saving timed out");
                StatusText.Text = "Playlist save timed out";
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            if (!_isClosing)
            {
                ShowNotice("The playlist could not be saved");
                StatusText.Text = "Playlist save failed";
            }
        }
        finally
        {
            _playlistSaveTask = null;
            if (!_isClosing)
            {
                SavePlaylistButton.IsEnabled = true;
            }
        }
    }

    private static async Task WritePlaylistAsync(
        string destinationPath,
        IReadOnlyList<PlaylistItem> items,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteLineAsync("#EXTM3U".AsMemory(), cancellationToken);
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var duration = item.DurationMilliseconds > 0 ? item.DurationMilliseconds / 1000 : -1;
                    await writer.WriteLineAsync($"#EXTINF:{duration},{item.Title}".AsMemory(), cancellationToken);
                    // An exported playlist is a shareable file: never write a stream password
                    // into it. The rest of the URI is preserved so the entry stays playable.
                    await writer.WriteLineAsync(
                        MediaSourceService.RedactCredentials(item.Source).AsMemory(),
                        cancellationToken);
                }

                await writer.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, true);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup after a failed or canceled save.
                }
            }
        }
    }

    private void RefreshQueueState()
    {
        QueueCountText.Text = $"{_playlist.Count:N0} {(_playlist.Count == 1 ? "ITEM" : "ITEMS")}";
        EmptyQueuePanel.Visibility = _playlist.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetNowPlaying()
    {
        _isPlaying = false;
        PlayPauseButton.Content = "\uE768";
        EngineStatusText.Text = "STOPPED";
        EngineStatusDot.Fill = new SolidColorBrush(Color.FromRgb(94, 102, 114));
        NowPlayingTitle.Text = "Nothing queued";
        NowPlayingSource.Text = "Choose a source to begin";
        WindowTitleText.Text = "Ready for a film";
        Title = "NOIR — Cinema without clutter";
        FormatBadge.Text = "READY";
        ResolutionBadge.Text = "—";
        EmptyPlayerPanel.Visibility = Visibility.Visible;
        SetVideoSurfaceActive(false);
        TimelineSlider.Value = 0;
        ElapsedText.Text = "0:00";
        RemainingText.Text = "−0:00";
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        await ImportSourcesAsync(paths, playFirst: true, showResultNotice: true);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var focusedElement = Keyboard.FocusedElement;
        if (focusedElement is TextBoxBase or ComboBox or ComboBoxItem)
        {
            return;
        }

        if (focusedElement is ButtonBase && e.Key is Key.Space or Key.Enter)
        {
            // Let focused buttons and check boxes keep their native keyboard activation.
            return;
        }

        if (_currentItem?.IsDisc == true && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            var navigationKey = e.Key == Key.System ? e.SystemKey : e.Key;
            var navigation = navigationKey switch
            {
                Key.Up => 1u,
                Key.Down => 2u,
                Key.Left => 3u,
                Key.Right => 4u,
                _ => uint.MaxValue
            };
            if (navigation != uint.MaxValue)
            {
                _mediaPlayer.Navigate(navigation);
                e.Handled = true;
                return;
            }
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.O when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                    OpenFolder_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.O:
                    OpenFile_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.D:
                    OpenDisc_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.E:
                    EjectDisc_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.L:
                    OpenLocation_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
            }
        }

        if (HandleFocusedSliderKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (PlaylistView.IsKeyboardFocusWithin && e.Key is
            Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            // Preserve native list selection and scrolling when the queue owns focus.
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
            case Key.MediaPlayPause:
                TogglePlayPause();
                e.Handled = true;
                break;
            case Key.MediaNextTrack:
            case Key.PageDown:
                PlayNext(userInitiated: true);
                e.Handled = true;
                break;
            case Key.MediaPreviousTrack:
            case Key.PageUp:
                PlayPrevious();
                e.Handled = true;
                break;
            case Key.Left:
                SeekBy(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -30_000 : -10_000);
                e.Handled = true;
                break;
            case Key.Right:
                SeekBy(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 30_000 : 10_000);
                e.Handled = true;
                break;
            case Key.Up:
                SetVolume(VolumeSlider.Value + 5);
                e.Handled = true;
                break;
            case Key.Down:
                SetVolume(VolumeSlider.Value - 5);
                e.Handled = true;
                break;
            case Key.M:
                ToggleMute();
                e.Handled = true;
                break;
            case Key.F:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape when _isFullscreen:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.S:
                Snapshot_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.V:
                AddSubtitle_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.R:
                Repeat_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.H:
                Shuffle_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.P:
                ToggleCompact();
                e.Handled = true;
                break;
            case Key.I:
                ToggleInspector();
                e.Handled = true;
                break;
            case Key.B:
                SetSidebarVisibility(!_sidebarVisible);
                e.Handled = true;
                break;
            case Key.Enter when _currentItem?.IsDisc == true:
                _mediaPlayer.Navigate(0);
                e.Handled = true;
                break;
            case Key.Enter when PlaylistView.SelectedItem is PlaylistItem item:
                PlayItem(item);
                e.Handled = true;
                break;
            case Key.Delete:
                RemoveSelected_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private bool HandleFocusedSliderKey(Key key)
    {
        if (TimelineSlider.IsKeyboardFocusWithin)
        {
            switch (key)
            {
                case Key.Left:
                case Key.Down:
                    SeekBy(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -30_000 : -10_000);
                    return true;
                case Key.Right:
                case Key.Up:
                    SeekBy(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 30_000 : 10_000);
                    return true;
                case Key.PageDown:
                    SeekBy(-30_000);
                    return true;
                case Key.PageUp:
                    SeekBy(30_000);
                    return true;
                case Key.Home when _mediaPlayer.Length > 0:
                    _mediaPlayer.Time = 0;
                    return true;
                case Key.End when _mediaPlayer.Length > 0:
                    _mediaPlayer.Time = _mediaPlayer.Length;
                    return true;
            }
        }

        if (VolumeSlider.IsKeyboardFocusWithin)
        {
            switch (key)
            {
                case Key.Left:
                case Key.Down:
                    SetVolume(VolumeSlider.Value - 1);
                    return true;
                case Key.Right:
                case Key.Up:
                    SetVolume(VolumeSlider.Value + 1);
                    return true;
                case Key.PageDown:
                    SetVolume(VolumeSlider.Value - 5);
                    return true;
                case Key.PageUp:
                    SetVolume(VolumeSlider.Value + 5);
                    return true;
                case Key.Home:
                    SetVolume(0);
                    return true;
                case Key.End:
                    SetVolume(100);
                    return true;
            }
        }

        return false;
    }

    private void TransportLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var useTwoRows = e.NewSize.Width < 730;
        if (useTwoRows)
        {
            Grid.SetRow(PrimaryTransportPanel, 0);
            Grid.SetColumn(PrimaryTransportPanel, 0);
            Grid.SetColumnSpan(PrimaryTransportPanel, 6);
            PrimaryTransportPanel.Margin = new Thickness(0);

            Grid.SetRow(TransportToolsPanel, 1);
            Grid.SetColumn(TransportToolsPanel, 0);
            Grid.SetColumnSpan(TransportToolsPanel, 3);
            TransportToolsPanel.Margin = new Thickness(0, 8, 0, 0);

            Grid.SetRow(VolumeControlsPanel, 1);
            Grid.SetColumn(VolumeControlsPanel, 3);
            Grid.SetColumnSpan(VolumeControlsPanel, 3);
            VolumeControlsPanel.Margin = new Thickness(0, 8, 0, 0);
            return;
        }

        Grid.SetRow(TransportToolsPanel, 0);
        Grid.SetColumn(TransportToolsPanel, 0);
        Grid.SetColumnSpan(TransportToolsPanel, 2);
        TransportToolsPanel.Margin = new Thickness(0);

        Grid.SetRow(PrimaryTransportPanel, 0);
        Grid.SetColumn(PrimaryTransportPanel, 2);
        Grid.SetColumnSpan(PrimaryTransportPanel, 2);
        PrimaryTransportPanel.Margin = new Thickness(0);

        Grid.SetRow(VolumeControlsPanel, 0);
        Grid.SetColumn(VolumeControlsPanel, 4);
        Grid.SetColumnSpan(VolumeControlsPanel, 2);
        VolumeControlsPanel.Margin = new Thickness(0);
    }

    private void SeekBy(long milliseconds)
    {
        if (_mediaPlayer.Length <= 0)
        {
            return;
        }

        _mediaPlayer.Time = Math.Clamp(_mediaPlayer.Time + milliseconds, 0, _mediaPlayer.Length);
        ShowNotice(milliseconds > 0 ? $"+{milliseconds / 1000}s" : $"{milliseconds / 1000}s");
    }

    private void SetVolume(double volume)
    {
        VolumeSlider.Value = Math.Clamp(volume, 0, 100);
        ShowNotice($"Volume · {(int)VolumeSlider.Value}%");
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (_isCompact)
        {
            ToggleCompact();
        }

        if (!_isFullscreen)
        {
            _restoreWindowState = WindowState;
            _restoreBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight)
                : RestoreBounds;

            var workArea = GetCurrentMonitorWorkArea();
            _isFullscreen = true;
            SetSidebarVisibility(false);
            SetInspectorVisibility(false);
            TitleBarRow.Height = new GridLength(0);
            StatusBarRow.Height = new GridLength(0);
            WindowFrame.BorderThickness = new Thickness(0);
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            Left = workArea.Left;
            Top = workArea.Top;
            Width = workArea.Width;
            Height = workArea.Height;
        }
        else
        {
            _isFullscreen = false;
            TitleBarRow.Height = new GridLength(46);
            StatusBarRow.Height = new GridLength(28);
            ResizeMode = ResizeMode.CanResize;
            WindowFrame.BorderThickness = new Thickness(1);
            WindowState = WindowState.Normal;
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = Math.Max(MinWidth, _restoreBounds.Width);
            Height = Math.Max(MinHeight, _restoreBounds.Height);
            SetSidebarVisibility(_sidebarVisible, force: true);
            SetInspectorVisibility(_inspectorVisible, force: true);

            if (_restoreWindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
    }

    private Rect GetCurrentMonitorWorkArea()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

        if (monitorHandle == IntPtr.Zero || !GetMonitorInfo(monitorHandle, ref monitorInfo) ||
            PresentationSource.FromVisual(this)?.CompositionTarget is not { } compositionTarget)
        {
            return SystemParameters.WorkArea;
        }

        var transform = compositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(new Point(monitorInfo.WorkArea.Left, monitorInfo.WorkArea.Top));
        var bottomRight = transform.Transform(new Point(monitorInfo.WorkArea.Right, monitorInfo.WorkArea.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void ToggleCompact_Click(object sender, RoutedEventArgs e) => ToggleCompact();

    private void ToggleCompact()
    {
        if (_isFullscreen)
        {
            ToggleFullscreen();
        }

        _isCompact = !_isCompact;
        if (_isCompact)
        {
            _restoreBounds = RestoreBounds;
            _restoreWindowState = WindowState;
            WindowState = WindowState.Normal;
            MinWidth = 560;
            MinHeight = 320;
            SetSidebarVisibility(false);
            SetInspectorVisibility(false);
            Width = 560;
            Height = 390;
            Left = SystemParameters.WorkArea.Right - Width - 20;
            Top = SystemParameters.WorkArea.Bottom - Height - 20;
            Topmost = true;
            ShowNotice("Mini player · Press P to restore");
        }
        else
        {
            MinWidth = 1080;
            MinHeight = 680;
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = Math.Max(MinWidth, _restoreBounds.Width);
            Height = Math.Max(MinHeight, _restoreBounds.Height);
            WindowState = _restoreWindowState;
            Topmost = _settings.AlwaysOnTop;
            SetSidebarVisibility(_sidebarVisible, force: true);
            SetInspectorVisibility(_inspectorVisible, force: true);
        }
    }

    private void ToggleInspector_Click(object sender, RoutedEventArgs e) => ToggleInspector();

    private void ToggleInspector()
    {
        var restoringFullLayout = _isFullscreen || _isCompact;

        if (_isFullscreen)
        {
            ToggleFullscreen();
        }

        if (_isCompact)
        {
            ToggleCompact();
        }

        SetInspectorVisibility(restoringFullLayout || !_inspectorVisible);
    }

    private void SetInspectorVisibility(bool visible, bool force = false)
    {
        if (force)
        {
            visible = _inspectorVisible;
        }
        else if (!_isFullscreen && !_isCompact)
        {
            _inspectorVisible = visible;
        }
        InspectorColumn.Width = visible ? new GridLength(296) : new GridLength(0);
        InspectorPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InspectorToggleText.Text = visible ? "Hide inspector" : "Show inspector";
    }

    private void SetSidebarVisibility(bool visible, bool force = false)
    {
        if (force)
        {
            visible = _sidebarVisible;
        }
        else if (!_isFullscreen && !_isCompact)
        {
            _sidebarVisible = visible;
        }
        SidebarColumn.Width = visible ? new GridLength(264) : new GridLength(0);
        SidebarPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        UpdateAacsStatus();
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    private void ChooseAacsLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a 64-bit libaacs library",
            Filter = "libaacs library|libaacs.dll|Dynamic-link libraries|*.dll",
            FileName = "libaacs.dll",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!AacsService.TryNormalizeLibraryPath(dialog.FileName, out var libraryPath))
        {
            ShowNotice("Choose a 64-bit file named libaacs.dll");
            return;
        }

        if (!File.Exists(libraryPath))
        {
            ShowNotice("Selected libaacs.dll was not found");
            return;
        }

        _settings.AacsLibraryPath = libraryPath;
        ScheduleSettingsSave();
        _aacsLibraryRestartRequired = AacsService.LibraryChangeRequiresRestart(
            libraryPath,
            AacsService.CurrentStatus);
        UpdateAacsStatus();
        ShowNotice(_aacsLibraryRestartRequired
            ? "Alternate AACS runtime selected · Restart NOIR to validate and apply"
            : "This AACS runtime is already active");
    }

    private void OpenAacsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AacsService.KeyDatabaseDirectory);
            Process.Start(new ProcessStartInfo(AacsService.KeyDatabaseDirectory) { UseShellExecute = true });
            UpdateAacsStatus();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            ShowNotice("The AACS key folder could not be opened");
        }
    }

    private async void DownloadKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || DownloadKeysButton is null)
        {
            return;
        }

        DownloadKeysButton.IsEnabled = false;
        StatusText.Text = "Downloading AACS key database…";
        ShowNotice("Connecting to key database server…");

        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(90));

            var result = await AacsService.DownloadKeyDatabaseAsync(cancellation.Token);

            if (_isClosing)
            {
                return;
            }

            if (result.Success)
            {
                ShowNotice("AACS key database installed · Ready to try protected Blu-rays");
                StatusText.Text = "AACS key database installed";
                MessageBox.Show(
                    this,
                    $"The AACS key database has been installed as plaintext successfully.\n\nLocation: {AacsService.KeyDatabasePath}\n\nNOIR can now try protected Blu-ray discs. A disc will still fail if this database has no matching key, or if it uses unsupported protection such as BD+ or UHD AACS 2.x.",
                    "AACS keys installed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                ShowNotice(result.Message);
                StatusText.Text = "AACS key download failed";
                MessageBox.Show(
                    this,
                    $"Could not download the AACS key database.\n\n{result.Message}\n\nYou can manually place a KEYDB.cfg file in:\n{AacsService.KeyDatabaseDirectory}",
                    "AACS key download failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            if (!_isClosing)
            {
                ShowNotice("AACS key download failed unexpectedly");
                StatusText.Text = "AACS key download failed";
            }
        }
        finally
        {
            if (!_isClosing)
            {
                DownloadKeysButton.IsEnabled = true;
                UpdateAacsStatus();
            }
        }
    }

    private void UpdateAacsStatus()
    {
        if (AacsStatusText is null)
        {
            return;
        }

        if (_aacsRepairTask is { IsCompleted: false })
        {
            AacsStatusText.Text = "Repairing compressed KEYDB.cfg…";
            AacsStatusText.Foreground = Brushes.Goldenrod;
            return;
        }

        var status = AacsService.CurrentStatus;
        if (_aacsLibraryRestartRequired)
        {
            AacsStatusText.Text = "Alternate AACS runtime selected · Restart NOIR to validate and apply";
            AacsStatusText.Foreground = Brushes.Goldenrod;
            return;
        }

        if (status.IsReady &&
            AacsService.TryNormalizeLibraryPath(_settings.AacsLibraryPath, out var selectedLibraryPath) &&
            !string.Equals(selectedLibraryPath, status.LibraryPath, StringComparison.OrdinalIgnoreCase))
        {
            AacsStatusText.Text = "Selected AACS runtime could not load · Using fallback runtime";
            AacsStatusText.Foreground = Brushes.Goldenrod;
            return;
        }

        AacsStatusText.Text = status.Message;
        AacsStatusText.Foreground = status.IsReady
            ? Brushes.Goldenrod
            : FindBrush("MutedTextBrush", Brushes.Gray);
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.RememberPosition = RememberPositionCheckBox.IsChecked == true;
        _settings.AutoPlayNext = AutoPlayNextCheckBox.IsChecked == true;
        var hardwareChanged = _settings.HardwareDecoding != (HardwareDecodingCheckBox.IsChecked == true);
        _settings.HardwareDecoding = HardwareDecodingCheckBox.IsChecked == true;
        _settings.AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;
        Topmost = _settings.AlwaysOnTop || _isCompact;
        ScheduleSettingsSave();

        if (hardwareChanged)
        {
            ShowNotice("Hardware decoding change applies after restart");
        }
    }

    private void ScheduleSettingsSave()
    {
        // LoadFailed means the file on disk is intact but was unreadable at startup; saving the
        // defaults we fell back to would destroy it.
        if (_isInitializing || _isClosing || _settingsService.LoadFailed)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var previousCancellation = _settingsSaveCancellation;
        _settingsSaveCancellation = cancellation;
        previousCancellation?.Cancel();
        var saveTask = SaveSettingsAfterDelayAsync(cancellation);
        _settingsSaveTask = saveTask;
        _ = saveTask;
    }

    private async Task SaveSettingsAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(500, cancellation.Token);
            await _settingsService.SaveAsync(_settings, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer settings snapshot or shutdown superseded this save.
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
        finally
        {
            if (ReferenceEquals(_settingsSaveCancellation, cancellation))
            {
                _settingsSaveCancellation = null;
                _settingsSaveTask = null;
            }

            cancellation.Dispose();
        }
    }

    private void OpenSnapshotFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.SnapshotFolder);
            Process.Start(new ProcessStartInfo(_settings.SnapshotFolder) { UseShellExecute = true });
        }
        catch
        {
            ShowNotice("The snapshot folder could not be opened");
        }
    }

    private void ShowNotice(string text)
    {
        PlaybackNoticeText.Text = text;
        PlaybackNotice.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private void AddRecent(string source)
    {
        if (MediaSourceService.TryNormalizeNetworkLocation(source, out var networkLocation))
        {
            var uri = new Uri(networkLocation, UriKind.Absolute);
            source = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            }.Uri.AbsoluteUri;
        }

        _settings.RecentFiles.RemoveAll(item => string.Equals(item, source, StringComparison.OrdinalIgnoreCase));
        _settings.RecentFiles.Insert(0, source);
        if (_settings.RecentFiles.Count > MaxRecentFiles)
        {
            _settings.RecentFiles.RemoveRange(MaxRecentFiles, _settings.RecentFiles.Count - MaxRecentFiles);
        }
    }

    private void TrimResumeHistory()
    {
        var excess = _settings.ResumePositions.Count - MaxResumePositions;
        if (excess <= 0)
        {
            return;
        }

        // Prefer dropping entries the user has not touched recently, oldest first. Taking only
        // from that set is not enough on its own: when most entries are also recents, too few
        // are removed and the history keeps growing past the cap for the rest of the session.
        var keep = _settings.RecentFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removable = _settings.ResumePositions.Keys.Where(key => !keep.Contains(key)).ToArray();
        foreach (var key in removable.Take(excess))
        {
            _settings.ResumePositions.Remove(key);
        }

        excess -= Math.Min(excess, removable.Length);
        if (excess <= 0)
        {
            return;
        }

        foreach (var key in _settings.ResumePositions.Keys.Take(excess).ToArray())
        {
            _settings.ResumePositions.Remove(key);
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        WindowFrame.BorderThickness = WindowState == WindowState.Maximized || _isFullscreen ? new Thickness(0) : new Thickness(1);
    }

    private void SetVideoSurfaceActive(bool active) =>
        VideoOverlay.Background = active ? Brushes.Transparent : Brushes.Black;

    private Brush FindBrush(string resourceName, Brush fallback) => TryFindResource(resourceName) as Brush ?? fallback;

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private static bool IsAudioFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ac3", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".aiff", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dts", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mka", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".opus", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".wma", StringComparison.OrdinalIgnoreCase);
    }
}
