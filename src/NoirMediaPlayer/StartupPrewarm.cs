using LibVLCSharp.Shared;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace NoirMediaPlayer;

/// <summary>
/// The playback engine paired with the player built on top of it.
/// </summary>
internal sealed record PlaybackEngine(LibVLC LibVlc, VlcMediaPlayer Player);

/// <summary>
/// Startup work hoisted out of the <see cref="MainWindow"/> constructor.
///
/// Loading the settings, initialising the AACS runtime and building the LibVLC engine all used
/// to run on the UI thread ahead of <c>InitializeComponent</c>, so no window could appear until
/// they finished. None of it touches WPF, so <see cref="Begin"/> starts it from
/// <see cref="App.OnStartup"/> on a worker thread while the UI thread is still loading
/// PresentationFramework and parsing XAML; the constructor then only waits for whatever is left.
///
/// The settings load stays here rather than in the constructor because the engine's
/// hardware-decoding option comes from it, and because the <see cref="Services.SettingsService"/>
/// instance has to be the same one the window keeps: it latches
/// <see cref="Services.SettingsService.LoadFailed"/> so an unreadable-but-intact settings file is
/// never overwritten with the defaults the player fell back to.
/// </summary>
internal static class StartupPrewarm
{
    private static readonly object SyncRoot = new();

    private static SettingsService? _settingsService;
    private static PlayerSettings? _settings;
    private static Task<PlaybackEngine>? _engineTask;

    /// <summary>
    /// Starts the prewarm. Safe to call more than once; only the first call does the work.
    /// </summary>
    internal static void Begin()
    {
        lock (SyncRoot)
        {
            StartEngine();
        }
    }

    /// <summary>
    /// The shared settings store. Loads synchronously if the prewarm never ran.
    /// </summary>
    internal static SettingsService SettingsService
    {
        get
        {
            lock (SyncRoot)
            {
                LoadSettings();
                return _settingsService!;
            }
        }
    }

    /// <summary>
    /// The settings the engine was built from. Loads synchronously if the prewarm never ran.
    /// </summary>
    internal static PlayerSettings Settings
    {
        get
        {
            lock (SyncRoot)
            {
                return LoadSettings();
            }
        }
    }

    /// <summary>
    /// Waits for the prewarmed engine and hands it over. A failure inside the worker is rethrown
    /// here, unwrapped, so the caller sees exactly the exception a synchronous build would have
    /// thrown.
    /// </summary>
    internal static PlaybackEngine TakeEngine()
    {
        Task<PlaybackEngine> engineTask;
        lock (SyncRoot)
        {
            engineTask = StartEngine();
        }

        var engine = engineTask.GetAwaiter().GetResult();

        // The caller owns the engine now and disposes it on shutdown, so the completed task is
        // dropped: handing the same instance out twice would hand out a disposed one.
        lock (SyncRoot)
        {
            if (ReferenceEquals(_engineTask, engineTask))
            {
                _engineTask = null;
            }
        }

        return engine;
    }

    private static PlayerSettings LoadSettings()
    {
        _settingsService ??= new SettingsService();
        return _settings ??= _settingsService.Load();
    }

    private static Task<PlaybackEngine> StartEngine()
    {
        if (_engineTask is not null)
        {
            return _engineTask;
        }

        // Read everything the worker needs before handing off: PlayerSettings is mutated by the
        // UI thread for the rest of the session.
        var settings = LoadSettings();
        var hardwareDecoding = settings.HardwareDecoding;
        var aacsLibraryPath = settings.AacsLibraryPath;

        return _engineTask = Task.Run(() => CreateEngine(hardwareDecoding, aacsLibraryPath));
    }

    private static PlaybackEngine CreateEngine(bool hardwareDecoding, string aacsLibraryPath)
    {
        // Still ahead of the engine, as it was in the constructor: libbluray picks the AACS
        // module up through the environment variables this sets.
        AacsService.Initialize(aacsLibraryPath);

        string[] engineOptions =
        [
            "--no-video-title-show",
            "--file-caching=350",
            "--network-caching=1200",
            "--disc-caching=700",
            hardwareDecoding ? "--avcodec-hw=any" : "--avcodec-hw=none"
        ];

        Core.Initialize();
        var libVlc = new LibVLC(engineOptions);
        return new PlaybackEngine(libVlc, new VlcMediaPlayer(libVlc));
    }
}
