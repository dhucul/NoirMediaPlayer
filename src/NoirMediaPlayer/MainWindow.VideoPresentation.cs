namespace NoirMediaPlayer;

public partial class MainWindow
{
    private void PrepareCompatibilityVideoPresentation(string? ratio, bool dvd)
    {
        // A newly embedded DVD output can retain its initial renderer viewport
        // even when the child HWND already fits. Prime a different aspect before
        // Play so the first Vout callback restores the selection through a real
        // geometry change. The selector and saved settings keep the user's ratio.
        ApplyCompatibilityAspectRatio(dvd ? ratio == "1:1" ? "4:3" : "1:1" : ratio);
    }

    private void ApplyCompatibilityAspectRatio(string? ratio)
    {
        // Zero selects fit-to-window scaling. An aspect override changes picture
        // proportions, while Original clears the override and keeps that fit.
        _mediaPlayer.Scale = 0;
        _mediaPlayer.AspectRatio = string.IsNullOrWhiteSpace(ratio) ? null : ratio;
    }

    private async Task RefreshCompatibilityVideoPresentationAsync(long generation, string? ratio, bool outputAvailable)
    {
        var media = _currentMedia;
        bool Current() => !_isClosing && generation == Interlocked.Read(ref _playbackGeneration) &&
            _windowsPlayer is null && media is not null && ReferenceEquals(media, _currentMedia);

        if (!outputAvailable || !Current()) return;
        await _playbackOperations.Enqueue(() =>
        {
            if (Current()) ApplyCompatibilityAspectRatio(ratio);
        });
        if (!Current()) return;
        // The host may have reached its final bounds before VLC created a child
        // window. Notify the new output even when no further WPF resize occurs.
        VideoView.RefreshVideoGeometry();
    }
}
