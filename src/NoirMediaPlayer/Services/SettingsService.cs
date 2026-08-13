using System.IO;
using System.Text.Json;
using NoirMediaPlayer.Models;

namespace NoirMediaPlayer.Services;

public sealed class SettingsService
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoirMediaPlayer",
        "settings.json");

    public PlayerSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new PlayerSettings();
            }

            var settings = JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                           ?? new PlayerSettings();
            return Normalize(settings);
        }
        catch
        {
            return new PlayerSettings();
        }
    }

    public void Save(PlayerSettings settings)
    {
        var entered = false;
        try
        {
            var content = JsonSerializer.Serialize(settings, JsonOptions);
            _saveGate.Wait();
            entered = true;
            Write(content);
        }
        catch
        {
            // Settings persistence should never interrupt playback or shutdown.
        }
        finally
        {
            if (entered)
            {
                _saveGate.Release();
            }
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
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _settingsPath, true);
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
            _saveGate.Release();
        }
    }

    private void Write(string content)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, _settingsPath, true);
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
        foreach (var entry in settings.ResumePositions ?? [])
        {
            if (!string.IsNullOrWhiteSpace(entry.Key) && entry.Value > 0)
            {
                resumePositions[entry.Key] = entry.Value;
            }
        }

        settings.ResumePositions = resumePositions;
        return settings;
    }
}
