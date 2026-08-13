using System.IO;
using System.Text.Json;
using NoirMediaPlayer.Models;

namespace NoirMediaPlayer.Services;

public sealed class SettingsService
{
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

            return JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                   ?? new PlayerSettings();
        }
        catch
        {
            return new PlayerSettings();
        }
    }

    public void Save(PlayerSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _settingsPath, true);
        }
        catch
        {
            // Settings persistence should never interrupt playback or shutdown.
        }
    }
}
