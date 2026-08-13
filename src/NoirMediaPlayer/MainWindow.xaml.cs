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
    private readonly SettingsService _settingsService = new();
    private readonly SemaphoreSlim _sourceImportGate = new(1, 1);
    private readonly SemaphoreSlim _discScanGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Stopwatch _positionPersistClock = Stopwatch.StartNew();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _noticeTimer;
    private readonly Random _random = new();
    private readonly HashSet<string> _playlistSources = new(StringComparer.OrdinalIgnoreCase);
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
    private bool _inspectorVisible = true;
    private bool _sidebarVisible = true;
    private int _currentIndex = -1;
    private int _rotation;
    private long _pendingResumePosition;
    private long _playbackGeneration;
    private Action? _detachPlaybackEvents;
    private CancellationTokenSource? _sourceImportCancellation;
    private CancellationTokenSource? _discScanCancellation;
    private CancellationTokenSource? _settingsSaveCancellation;
    private CancellationTokenSource? _networkStartupCancellation;
    private Task? _sourceImportWorker;
    private Task? _discScanWorker;
    private Task? _settingsSaveTask;
    private Task? _playlistSaveTask;
    private Rect _restoreBounds;
    private WindowState _restoreWindowState;

    private const int ImportBatchSize = 100;
    private const int ImportChannelCapacity = 256;
    private const int MaxPlaylistItems = 10_000;
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DiscScanTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NetworkStartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FileWriteTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

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
        _settings = _settingsService.Load();

        Core.Initialize();
        var engineOptions = new List<string>
        {
            "--no-video-title-show",
            "--quiet",
            "--file-caching=350",
            "--network-caching=1200",
            "--disc-caching=700",
            _settings.HardwareDecoding ? "--avcodec-hw=any" : "--avcodec-hw=none"
        };

        _libVlc = new LibVLC(engineOptions.ToArray());
        _mediaPlayer = new VlcMediaPlayer(_libVlc);

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

        void Playing(object? sender, EventArgs args) => Dispatch(() =>
        {
            if (_mediaPlayer.State is not (VLCState.Playing or VLCState.Buffering))
            {
                return;
            }

            CancelNetworkStartupWatchdog();
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
        });

        void Paused(object? sender, EventArgs args) => Dispatch(() =>
        {
            if (_mediaPlayer.State != VLCState.Paused)
            {
                return;
            }

            CancelNetworkStartupWatchdog();
            _isPlaying = false;
            PlayPauseButton.Content = "\uE768";
            EngineStatusText.Text = "PAUSED";
            StatusText.Text = "Paused";
        });

        void Stopped(object? sender, EventArgs args) => Dispatch(() =>
        {
            if (_mediaPlayer.State is not (VLCState.Stopped or VLCState.NothingSpecial))
            {
                return;
            }

            CancelNetworkStartupWatchdog();
            _isPlaying = false;
            PlayPauseButton.Content = "\uE768";
            EngineStatusText.Text = "STOPPED";
            EngineStatusDot.Fill = new SolidColorBrush(Color.FromRgb(94, 102, 114));
            SetVideoSurfaceActive(false);
        });

        void EndReached(object? sender, EventArgs args) => Dispatch(() =>
        {
            if (_mediaPlayer.State == VLCState.Ended)
            {
                HandleMediaEnded(generation);
            }
        });

        void EncounteredError(object? sender, EventArgs args) => Dispatch(() =>
        {
            if (_mediaPlayer.State != VLCState.Error)
            {
                return;
            }

            CancelNetworkStartupWatchdog();
            _isPlaying = false;
            PlayPauseButton.Content = "\uE768";
            EngineStatusText.Text = "PLAYBACK ERROR";
            EngineStatusDot.Fill = Brushes.OrangeRed;
            StatusText.Text = "This source could not be played";
            StatusDot.Fill = Brushes.OrangeRed;
            SetVideoSurfaceActive(false);
            ShowNotice("Playback error - check the source or disc");
        });

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

        var launchFiles = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (launchFiles.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            StatusText.Text = "Startup smoke test passed";
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
        IsEnabled = false;
        PersistCurrentPosition(force: true);
        _settings.Volume = (int)VolumeSlider.Value;
        _settings.IsMuted = _mediaPlayer.Mute;

        _lifetimeCancellation.Cancel();
        _sourceImportCancellation?.Cancel();
        _discScanCancellation?.Cancel();
        _settingsSaveCancellation?.Cancel();
        CancelNetworkStartupWatchdog();
        _uiTimer.Stop();
        _noticeTimer.Stop();
        DisposePlaybackResources();

        var activeOperations = new[]
        {
            _sourceImportWorker,
            _discScanWorker,
            _settingsSaveTask,
            _playlistSaveTask
        }.Where(task => task is not null).Cast<Task>().ToArray();

        if (activeOperations.Length > 0)
        {
            try
            {
                var completion = Task.WhenAll(activeOperations);
                if (await Task.WhenAny(completion, Task.Delay(ShutdownTimeout)) == completion)
                {
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

        _shutdownComplete = true;
        _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.ApplicationIdle);
    }

    private void DisposePlaybackResources()
    {
        if (_playbackDisposed)
        {
            return;
        }

        _playbackDisposed = true;
        ReleaseCurrentMedia();
        VideoView.MediaPlayer = null;
        _mediaPlayer.Dispose();
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
            AddItem(item, playNow: true);
            ShowNotice($"Opening {discs[0].DisplayName}");
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
        if (sourceList.Length == 0)
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

            if (showResultNotice)
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
        CancellationToken cancellationToken)
    {
        Exception? completionError = null;
        try
        {
            foreach (var item in EnumerateImportItems(sources, existingSources, maximumItems, cancellationToken))
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
        foreach (var source in MediaSourceService.ExpandFiles(sources, cancellationToken))
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

    private void AddItem(PlaylistItem item, bool playNow)
    {
        var existing = _playlist.FirstOrDefault(candidate =>
            string.Equals(candidate.Source, item.Source, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            if (_playlist.Count >= MaxPlaylistItems)
            {
                ShowNotice($"Queue limit reached ({MaxPlaylistItems:N0} items)");
                return;
            }

            _playlistSources.Add(item.Source);
            _playlist.Add(item);
            existing = item;
        }

        RefreshQueueState();
        if (playNow)
        {
            PlayItem(existing);
        }
    }

    private void PlayItem(PlaylistItem item, bool allowResume = true, long requestedPosition = -1)
    {
        if (_isClosing || _playbackDisposed)
        {
            return;
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
            return;
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
        EngineStatusText.Text = item.IsDisc ? "READING DISC" : item.IsNetwork ? "CONNECTING" : "OPENING";
        EngineStatusDot.Fill = Brushes.Goldenrod;
        StatusText.Text = $"Opening · {item.Title}";
        try
        {
            if (!_mediaPlayer.Play(_currentMedia))
            {
                throw new InvalidOperationException("LibVLC rejected the media source.");
            }

            StartNetworkStartupWatchdog(item, generation);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            item.IsPlaying = false;
            ReleaseCurrentMedia();
            EngineStatusText.Text = "PLAYBACK ERROR";
            StatusText.Text = "The selected source could not be played";
            ShowNotice("The selected source could not be played");
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

    private void ReleaseCurrentMedia()
    {
        Interlocked.Increment(ref _playbackGeneration);
        try
        {
            DetachPlaybackEvents();
        }
        catch (ObjectDisposedException)
        {
            // The native player is already gone during a repeated shutdown path.
        }

        CancelNetworkStartupWatchdog();
        try
        {
            _mediaPlayer.Stop();
            _mediaPlayer.Media = null;
        }
        catch (ObjectDisposedException)
        {
            // Disposal is idempotent from the window's perspective.
        }

        _currentMedia?.Dispose();
        _currentMedia = null;
        _pendingResumePosition = 0;
        _isPlaying = false;
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

    private void StartNetworkStartupWatchdog(PlaylistItem item, long generation)
    {
        CancelNetworkStartupWatchdog();
        if (!item.IsNetwork)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _networkStartupCancellation = cancellation;
        _ = MonitorNetworkStartupAsync(item, generation, cancellation.Token);
    }

    private async Task MonitorNetworkStartupAsync(
        PlaylistItem item,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(NetworkStartupTimeout, cancellationToken).ConfigureAwait(false);
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

                item.IsPlaying = false;
                ReleaseCurrentMedia();
                EngineStatusText.Text = "CONNECTION TIMEOUT";
                EngineStatusDot.Fill = Brushes.OrangeRed;
                StatusText.Text = "The network stream did not respond";
                ShowNotice("Network connection timed out");
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

    private void CancelNetworkStartupWatchdog()
    {
        var cancellation = _networkStartupCancellation;
        _networkStartupCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
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

        PlayItem(_playlist[nextIndex]);
        return true;
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
        if (_currentItem is null)
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
        if (!_settings.RememberPosition || _currentItem is null || _currentItem.IsNetwork)
        {
            return;
        }

        var time = _mediaPlayer.Time;
        var length = _mediaPlayer.Length;
        if (time >= 10_000 && (length <= 0 || time < length - 10_000))
        {
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

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private void AudioTrack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || AudioTrackComboBox.SelectedItem is not ComboBoxItem item || item.Tag is null)
        {
            return;
        }

        _mediaPlayer.SetAudioTrack(Convert.ToInt32(item.Tag, CultureInfo.InvariantCulture));
        ShowNotice($"Audio · {item.Content}");
    }

    private void SubtitleTrack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || SubtitleTrackComboBox.SelectedItem is not ComboBoxItem item || item.Tag is null)
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
        _playlistView?.Refresh();
    }

    private bool FilterPlaylist(object item)
    {
        if (item is not PlaylistItem playlistItem || string.IsNullOrWhiteSpace(PlaylistSearchBox.Text))
        {
            return true;
        }

        var query = PlaylistSearchBox.Text.Trim();
        return playlistItem.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               playlistItem.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase);
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
            ReleaseCurrentMedia();
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
        ReleaseCurrentMedia();
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
                    await writer.WriteLineAsync(item.Source.AsMemory(), cancellationToken);
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

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = !SettingsPopup.IsOpen;

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
        if (_isInitializing || _isClosing)
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
        if (_settings.RecentFiles.Count > 20)
        {
            _settings.RecentFiles.RemoveRange(20, _settings.RecentFiles.Count - 20);
        }
    }

    private void TrimResumeHistory()
    {
        if (_settings.ResumePositions.Count <= 250)
        {
            return;
        }

        var keep = _settings.RecentFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _settings.ResumePositions.Keys.Where(key => !keep.Contains(key)).Take(_settings.ResumePositions.Count - 250).ToArray())
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
