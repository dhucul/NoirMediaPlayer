using System.IO;
using System.Text.Json;
using NoirMediaPlayer.Models;

namespace NoirMediaPlayer.Services;

public sealed class SettingsService
{
    private const long MaxSettingsFileBytes = 1_048_576;
    private const int MaxResumePositions = 250;

    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsPath;

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NoirMediaPlayer",
            "settings.json");
    }

    public PlayerSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new PlayerSettings();
            }

            var fileInfo = new FileInfo(_settingsPath);
            if (fileInfo.Length is <= 0 or > MaxSettingsFileBytes)
            {
                return new PlayerSettings();
            }

            using var stream = new FileStream(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            var settings = JsonSerializer.Deserialize<PlayerSettings>(stream, JsonOptions) ?? new PlayerSettings();
            return Normalize(settings);
        }
        catch
        {
            return new PlayerSettings();
        }
    }

    public async Task SaveAsync(PlayerSettings settings, CancellationToken cancellationToken = default)
    {
        string content;
        try
        {
            // Capture mutable settings before yielding away from the UI thread.
            content = JsonSerializer.Serialize(settings, JsonOptions);
        }
        catch
        {
            return;
        }

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = $"{_settingsPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _settingsPath, true);
            temporaryPath = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Settings persistence should never interrupt playback.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // A later cleanup pass or the OS can remove an abandoned temp file.
                }
            }

            _saveGate.Release();
        }
    }

    private static PlayerSettings Normalize(PlayerSettings settings)
    {
        settings.Volume = Math.Clamp(settings.Volume, 0, 100);
        settings.PlaybackRate = settings.PlaybackRate is 0.5f or 0.75f or 0.9f or 1f or 1.25f or 1.5f or 2f
            ? settings.PlaybackRate
            : 1f;
        settings.RepeatMode = settings.RepeatMode is "Off" or "All" or "One" ? settings.RepeatMode : "Off";
        settings.LastFolder ??= string.Empty;
        settings.SnapshotFolder = string.IsNullOrWhiteSpace(settings.SnapshotFolder)
            ? new PlayerSettings().SnapshotFolder
            : settings.SnapshotFolder;
        settings.RecentFiles = (settings.RecentFiles ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        var resumePositions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (settings.ResumePositions ?? [])
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value > 0)
                     .TakeLast(MaxResumePositions))
        {
            resumePositions[entry.Key] = entry.Value;
        }

        settings.ResumePositions = resumePositions;
        return settings;
    }
}
