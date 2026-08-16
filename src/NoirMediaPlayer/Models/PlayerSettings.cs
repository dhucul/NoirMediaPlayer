using System.IO;

namespace NoirMediaPlayer.Models;

// ResumePositions uses OrderedDictionary so that "keep the newest N entries" is a documented
// guarantee of the collection rather than an accident of Dictionary's internal layout.

public sealed class PlayerSettings
{
    public int Volume { get; set; } = 82;
    public bool IsMuted { get; set; }
    public float PlaybackRate { get; set; } = 1f;
    public bool RememberPosition { get; set; } = true;
    public bool AutoPlayNext { get; set; } = true;
    public bool HardwareDecoding { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool Shuffle { get; set; }
    public string RepeatMode { get; set; } = "Off";
    public string AacsLibraryPath { get; set; } = string.Empty;
    public string LastFolder { get; set; } = string.Empty;
    public List<string> RecentFiles { get; set; } = [];
    public OrderedDictionary<string, long> ResumePositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string SnapshotFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Noir Snapshots");
}
