using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace NoirMediaPlayer.Models;

public sealed class PlaylistItem : INotifyPropertyChanged
{
    private bool _isPlaying;
    private long _durationMilliseconds;

    public required string Source { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = string.Empty;
    public bool IsDisc { get; init; }
    public bool IsNetwork { get; init; }

    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetField(ref _isPlaying, value);
    }

    public long DurationMilliseconds
    {
        get => _durationMilliseconds;
        set
        {
            if (SetField(ref _durationMilliseconds, value))
            {
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    public string DurationText => DurationMilliseconds > 0
        ? TimeSpan.FromMilliseconds(DurationMilliseconds).ToString(DurationMilliseconds >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss")
        : "—";

    public string KindText => IsDisc ? "DISC" : IsNetwork ? "STREAM" : Path.GetExtension(Source).TrimStart('.').ToUpperInvariant();

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
