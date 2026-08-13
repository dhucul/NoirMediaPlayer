using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _noticeTimer;
    private readonly Random _random = new();
    private readonly PlayerSettings _settings;
    private readonly LibVLC _libVlc;
    private readonly VlcMediaPlayer _mediaPlayer;
    private ICollectionView? _playlistView;
    private Media? _currentMedia;
    private PlaylistItem? _currentItem;
    private bool _isPlaying;
    private bool _isScrubbing;
    private bool _isInitializing = true;
    private bool _isFullscreen;
    private bool _isCompact;
    private bool _inspectorVisible = true;
    private bool _sidebarVisible = true;
    private int _currentIndex = -1;
    private int _rotation;
    private long _pendingResumePosition;
    private long _lastPersistedAt;
    private Rect _restoreBounds;
    private WindowState _restoreWindowState;

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

        WirePlayerEvents();
        ApplySettingsToControls();
        _isInitializing = false;
    }

    private void WirePlayerEvents()
    {
        _mediaPlayer.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _isPlaying = true;
            PlayPauseButton.Content = "\uE769";
            EngineStatusText.Text = "PLAYING";
            EngineStatusDot.Fill = FindBrush("AccentBrush", Brushes.GreenYellow);
            StatusText.Text = _currentItem is null ? "Playing" : $"Playing · {_currentItem.Title}";
            StatusDot.Fill = FindBrush("AccentBrush", Brushes.GreenYellow);
            EmptyPlayerPanel.Visibility = Visibility.Collapsed;
            _mediaPlayer.SetRate(_settings.PlaybackRate);

            if (_pendingResumePosition > 0 && _mediaPlayer.Length > _pendingResumePosition + 10_000)
            {
                var resumeAt = _pendingResumePosition;
                _pendingResumePosition = 0;
                _mediaPlayer.Time = resumeAt;
                ShowNotice($"Resumed at {FormatTime(resumeAt)}");
            }

            RefreshTrackSelectors();
            RefreshVideoInfo();
        });

        _mediaPlayer.Paused += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _isPlaying = false;
            PlayPauseButton.Content = "\uE768";
            EngineStatusText.Text = "PAUSED";
            StatusText.Text = "Paused";
        });

        _mediaPlayer.Stopped += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _isPlaying = false;
            PlayPauseButton.Content = "\uE768";
            EngineStatusText.Text = "STOPPED";
            EngineStatusDot.Fill = new SolidColorBrush(Color.FromRgb(94, 102, 114));
        });

        _mediaPlayer.EndReached += (_, _) => Dispatcher.BeginInvoke(HandleMediaEnded);
        _mediaPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _isPlaying = false;
            PlayPauseButton.Content = "\uE768";
            EngineStatusText.Text = "PLAYBACK ERROR";
            EngineStatusDot.Fill = Brushes.OrangeRed;
            StatusText.Text = "This source could not be played";
            StatusDot.Fill = Brushes.OrangeRed;
            ShowNotice("Playback error — check the source or disc");
        });
        _mediaPlayer.Buffering += (_, args) => Dispatcher.BeginInvoke(() =>
        {
            if (args.Cache < 100f)
            {
                EngineStatusText.Text = $"BUFFERING {args.Cache:0}%";
            }
        });
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _uiTimer.Start();
        StatusText.Text = "LibVLC ready · Drop media anywhere";
        Topmost = _settings.AlwaysOnTop;

        var launchFiles = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (launchFiles.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            StatusText.Text = "Startup smoke test passed";
            Dispatcher.BeginInvoke(Close, DispatcherPriority.ApplicationIdle);
            return;
        }

        if (launchFiles.Length > 0)
        {
            AddSources(launchFiles, true);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        PersistCurrentPosition(force: true);
        _settings.Volume = (int)VolumeSlider.Value;
        _settings.IsMuted = _mediaPlayer.Mute;
        _settingsService.Save(_settings);
        _uiTimer.Stop();
        _mediaPlayer.Stop();
        VideoView.MediaPlayer = null;
        _currentMedia?.Dispose();
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

        ShuffleButton.Foreground = _settings.Shuffle ? FindBrush("AccentBrush", Brushes.GreenYellow) : FindBrush("TextBrush", Brushes.White);
        UpdateRepeatVisual();
        SelectSpeed(_settings.PlaybackRate);
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
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
        AddSources(dialog.FileNames, true);
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
        var discItem = DiscService.CreateFromFolder(dialog.FolderName);
        if (discItem is not null)
        {
            AddItem(discItem, playNow: true);
            return;
        }

        StatusText.Text = "Scanning folder…";
        var paths = await Task.Run(() => MediaSourceService.EnumerateFolder(dialog.FolderName).ToList());
        if (paths.Count == 0)
        {
            ShowNotice("No supported media found in that folder");
            StatusText.Text = "Ready";
            return;
        }

        AddSources(paths, true);
        ShowNotice($"Added {paths.Count:N0} item{(paths.Count == 1 ? string.Empty : "s")}");
    }

    private void OpenDisc_Click(object sender, RoutedEventArgs e)
    {
        var discs = DiscService.FindVideoDiscs();
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

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenLocationWindow { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.MediaLocation))
        {
            return;
        }

        AddItem(MediaSourceService.CreateNetworkItem(dialog.MediaLocation.Trim()), playNow: true);
    }

    private void AddSources(IEnumerable<string> sources, bool playFirst)
    {
        PlaylistItem? firstAdded = null;
        var added = 0;

        foreach (var source in MediaSourceService.ExpandFiles(sources))
        {
            var item = Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile
                ? MediaSourceService.CreateNetworkItem(source)
                : MediaSourceService.CreateFileItem(source);

            if (_playlist.Any(existing => string.Equals(existing.Source, item.Source, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _playlist.Add(item);
            firstAdded ??= item;
            added++;
        }

        RefreshQueueState();
        if (firstAdded is not null && playFirst)
        {
            PlayItem(firstAdded);
        }
        else if (added > 0)
        {
            StatusText.Text = $"Added {added:N0} item{(added == 1 ? string.Empty : "s")} to the queue";
        }
    }

    private void AddItem(PlaylistItem item, bool playNow)
    {
        var existing = _playlist.FirstOrDefault(candidate =>
            string.Equals(candidate.Source, item.Source, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            _playlist.Add(item);
            existing = item;
        }

        RefreshQueueState();
        if (playNow)
        {
            PlayItem(existing);
        }
    }

    private void PlayItem(PlaylistItem item, bool allowResume = true)
    {
        PersistCurrentPosition(force: true);

        if (_currentItem is not null)
        {
            _currentItem.IsPlaying = false;
        }

        _currentItem = item;
        _currentIndex = _playlist.IndexOf(item);
        _currentItem.IsPlaying = true;
        PlaylistView.SelectedItem = item;
        PlaylistView.ScrollIntoView(item);

        _currentMedia?.Dispose();
        _currentMedia = CreateMedia(item.Source);
        if (_rotation != 0)
        {
            _currentMedia.AddOption(":video-filter=transform");
            _currentMedia.AddOption($":transform-type={_rotation}");
        }

        _pendingResumePosition = 0;
        if (allowResume && _settings.RememberPosition &&
            _settings.ResumePositions.TryGetValue(item.Source, out var savedPosition) && savedPosition >= 10_000)
        {
            _pendingResumePosition = savedPosition;
        }

        UpdateNowPlaying(item);
        AddRecent(item.Source);
        EngineStatusText.Text = item.IsDisc ? "READING DISC" : item.IsNetwork ? "CONNECTING" : "OPENING";
        EngineStatusDot.Fill = Brushes.Goldenrod;
        StatusText.Text = $"Opening · {item.Title}";
        _mediaPlayer.Play(_currentMedia);
    }

    private Media CreateMedia(string source)
    {
        if (File.Exists(source))
        {
            return new Media(_libVlc, source, FromType.FromPath);
        }

        return new Media(_libVlc, source, FromType.FromLocation);
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

    private void PlayNext(bool userInitiated)
    {
        if (_playlist.Count == 0)
        {
            return;
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
                    return;
                }
            }
        }

        PlayItem(_playlist[nextIndex]);
    }

    private void HandleMediaEnded()
    {
        if (_currentItem is not null)
        {
            _settings.ResumePositions.Remove(_currentItem.Source);
        }

        if (_settings.RepeatMode == "One" && _currentItem is not null)
        {
            PlayItem(_currentItem, allowResume: false);
            return;
        }

        if (_settings.AutoPlayNext || _settings.RepeatMode == "All")
        {
            PlayNext(userInitiated: false);
        }
        else
        {
            _isPlaying = false;
            PlayPauseButton.Content = "\uE768";
            StatusText.Text = "Playback finished";
        }
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

        if (_isPlaying && _settings.RememberPosition && time - _lastPersistedAt >= 5_000)
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

        _lastPersistedAt = time;
        if (force)
        {
            TrimResumeHistory();
            _settingsService.Save(_settings);
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
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        _mediaPlayer.Mute = !_mediaPlayer.Mute;
        _settings.IsMuted = _mediaPlayer.Mute;
        UpdateMuteVisual();
        ShowNotice(_mediaPlayer.Mute ? "Muted" : $"Volume · {(int)VolumeSlider.Value}%");
    }

    private void UpdateMuteVisual()
    {
        MuteButton.Content = _mediaPlayer.Mute || VolumeSlider.Value <= 0 ? "\uE74F" : "\uE767";
        MuteButton.Foreground = _mediaPlayer.Mute ? FindBrush("AccentBrush", Brushes.GreenYellow) : FindBrush("TextBrush", Brushes.White);
    }

    private void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedComboBox.SelectedItem is not ComboBoxItem item || item.Tag is null ||
            !float.TryParse(item.Tag.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
        {
            return;
        }

        _settings.PlaybackRate = rate;
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
        PlayItem(_currentItem, allowResume: false);
        _pendingResumePosition = position;
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
        ShuffleButton.Foreground = _settings.Shuffle ? FindBrush("AccentBrush", Brushes.GreenYellow) : FindBrush("TextBrush", Brushes.White);
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
        ShowNotice($"Repeat · {_settings.RepeatMode}");
    }

    private void UpdateRepeatVisual()
    {
        RepeatButton.Foreground = _settings.RepeatMode == "Off"
            ? FindBrush("TextBrush", Brushes.White)
            : FindBrush("AccentBrush", Brushes.GreenYellow);
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
        _playlist.Remove(item);
        if (wasCurrent)
        {
            _mediaPlayer.Stop();
            _currentItem = null;
            _currentIndex = -1;
            ResetNowPlaying();
        }

        RefreshQueueState();
    }

    private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.Stop();
        foreach (var item in _playlist)
        {
            item.IsPlaying = false;
        }
        _playlist.Clear();
        _currentItem = null;
        _currentIndex = -1;
        ResetNowPlaying();
        RefreshQueueState();
        ShowNotice("Queue cleared");
    }

    private void SavePlaylist_Click(object sender, RoutedEventArgs e)
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

        var lines = new List<string> { "#EXTM3U" };
        foreach (var item in _playlist)
        {
            lines.Add($"#EXTINF:{(item.DurationMilliseconds > 0 ? item.DurationMilliseconds / 1000 : -1)},{item.Title}");
            lines.Add(item.Source);
        }
        File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(false));
        ShowNotice($"Playlist saved · {Path.GetFileName(dialog.FileName)}");
    }

    private void RefreshQueueState()
    {
        QueueCountText.Text = $"{_playlist.Count:N0} {(_playlist.Count == 1 ? "ITEM" : "ITEMS")}";
        EmptyQueuePanel.Visibility = _playlist.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetNowPlaying()
    {
        NowPlayingTitle.Text = "Nothing queued";
        NowPlayingSource.Text = "Choose a source to begin";
        WindowTitleText.Text = "Ready for a film";
        Title = "NOIR — Cinema without clutter";
        FormatBadge.Text = "READY";
        ResolutionBadge.Text = "—";
        EmptyPlayerPanel.Visibility = Visibility.Visible;
        TimelineSlider.Value = 0;
        ElapsedText.Text = "0:00";
        RemainingText.Text = "−0:00";
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        var discFolder = paths.Length == 1 && Directory.Exists(paths[0]) ? DiscService.CreateFromFolder(paths[0]) : null;
        if (discFolder is not null)
        {
            AddItem(discFolder, playNow: true);
        }
        else
        {
            AddSources(paths, true);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox)
        {
            return;
        }

        if (_currentItem?.IsDisc == true && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            var navigation = e.Key switch
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
                SetInspectorVisibility(!_inspectorVisible);
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

        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            _restoreWindowState = WindowState;
            SetSidebarVisibility(false);
            SetInspectorVisibility(false);
            TitleBarRow.Height = new GridLength(0);
            StatusBarRow.Height = new GridLength(0);
            WindowFrame.BorderThickness = new Thickness(0);
            WindowState = WindowState.Maximized;
        }
        else
        {
            TitleBarRow.Height = new GridLength(46);
            StatusBarRow.Height = new GridLength(28);
            WindowFrame.BorderThickness = new Thickness(1);
            WindowState = _restoreWindowState;
            SetSidebarVisibility(_sidebarVisible, force: true);
            SetInspectorVisibility(_inspectorVisible, force: true);
        }
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
            MinWidth = 480;
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

    private void ToggleInspector_Click(object sender, RoutedEventArgs e) => SetInspectorVisibility(false);

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
        _settingsService.Save(_settings);

        if (hardwareChanged)
        {
            ShowNotice("Hardware decoding change applies after restart");
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
