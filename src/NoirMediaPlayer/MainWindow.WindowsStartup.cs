using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer;

public partial class MainWindow
{
    private async Task StartWindowsPlaybackAsync(PlaylistItem item, nint handle, long generation, string? contentType = null,
        string? aspectRatio = null)
    {
        var volume = _settings.Volume;
        var muted = _settings.IsMuted;
        var rate = _settings.PlaybackRate;
        var softwareVideo = !_settings.HardwareDecoding;
        _windowsFallbackTask = null;
        // Only local Matroska captions need metadata-first selection. Keep the
        // established startup path for other files and network streams.
        var prepareCaptions = !item.IsNetwork && System.IO.Path.GetExtension(item.Source).Equals(".mkv", StringComparison.OrdinalIgnoreCase);
        _windowsStartupPending = prepareCaptions;
        _windowsFailureReason = null;
        EngineStatusText.ToolTip = "Windows Media Foundation";
        await _playbackOperations.Enqueue(() =>
        {
            if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration)) return;
            var player = new WindowsMediaEngine(handle, mediaEvent => DispatchWindowsPlaybackEvent(generation, mediaEvent), softwareVideo: softwareVideo);
            lock (_windowsSessionSync)
            {
                if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration))
                {
                    player.Dispose();
                    return;
                }
                _windowsPlayer = player;
            }
            // Inspection and construction can yield while the UI changes rotation.
            ApplyCurrentNativeRotation(player);
            player.SetAspectRatio(aspectRatio);
            player.Open(item.Source, volume, muted, rate, contentType, autoPlay: !prepareCaptions);
        }).WaitAsync(_lifetimeCancellation.Token);
        if (IsCurrentPlayback(generation, item)) StartPlaybackStartupWatchdog(item, generation);
    }
}
