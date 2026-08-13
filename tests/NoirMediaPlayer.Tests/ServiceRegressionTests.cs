using System.Text.Json;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class ServiceRegressionTests
{
    [Theory]
    [InlineData("https://example.com/video.mp4")]
    [InlineData("rtsp://camera.example/live")]
    [InlineData("rtp://239.1.1.1:5004")]
    [InlineData("udp://239.1.1.1:1234")]
    public void NetworkLocation_AllowsSupportedSchemes(string location)
    {
        Assert.True(MediaSourceService.TryNormalizeNetworkLocation(location, out var normalized));
        Assert.False(string.IsNullOrWhiteSpace(normalized));
    }

    [Theory]
    [InlineData("file:///C:/private/video.mp4")]
    [InlineData("mailto:test@example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("smb://server/share/video.mp4")]
    [InlineData("C:\\private\\video.mp4")]
    public void NetworkLocation_RejectsFilesAndUnsupportedSchemes(string location)
    {
        Assert.False(MediaSourceService.TryNormalizeNetworkLocation(location, out _));
    }

    [Fact]
    public void PlaylistImport_SkipsUnsafeAndMalformedEntriesWithoutLosingValidItems()
    {
        using var temp = new TempDirectory();
        var mediaPath = Path.Combine(temp.Path, "valid.mp3");
        File.WriteAllBytes(mediaPath, []);
        var playlistPath = Path.Combine(temp.Path, "sources.m3u8");
        File.WriteAllLines(playlistPath,
        [
            "#EXTM3U",
            "valid.mp3",
            @"\\attacker.invalid\share\capture.mp3",
            "file:///C:/private/video.mp4",
            "mailto:test@example.com",
            "bad\0path.mp3",
            "https://example.com/live"
        ]);

        var sources = MediaSourceService.ExpandFiles([playlistPath]).ToArray();

        Assert.Equal(2, sources.Length);
        Assert.Contains(Path.GetFullPath(mediaPath), sources, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("https://example.com/live", sources);
    }

    [Fact]
    public void DiscFolder_RequiresDiscStructureAndEscapesItsMrl()
    {
        using var temp = new TempDirectory("disc folder");
        Assert.Null(DiscService.CreateFromFolder(temp.Path));

        Directory.CreateDirectory(Path.Combine(temp.Path, "VIDEO_TS"));
        var item = DiscService.CreateFromFolder(temp.Path);

        Assert.NotNull(item);
        Assert.True(item.IsDisc);
        Assert.StartsWith("dvd:///", item.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disc%20folder", item.Source, StringComparison.OrdinalIgnoreCase);

        Directory.CreateDirectory(Path.Combine(temp.Path, "BDMV"));
        var bluRayItem = DiscService.CreateFromFolder(temp.Path);
        Assert.NotNull(bluRayItem);
        Assert.StartsWith("bluray:///", bluRayItem.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsLoad_ClampsValuesAndCapsResumeHistory()
    {
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settings = new PlayerSettings
        {
            Volume = 500,
            ResumePositions = Enumerable.Range(0, 300)
                .ToDictionary(index => $"video-{index}.mp4", index => (long)index + 1)
        };
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));

        var loaded = new SettingsService(settingsPath).Load();

        Assert.Equal(100, loaded.Volume);
        Assert.Equal(250, loaded.ResumePositions.Count);
        Assert.DoesNotContain("video-0.mp4", loaded.ResumePositions.Keys);
        Assert.Contains("video-299.mp4", loaded.ResumePositions.Keys);
    }

    [Fact]
    public void SettingsLoad_RejectsOversizedFiles()
    {
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(settingsPath, new string('x', 1_048_577));

        var loaded = new SettingsService(settingsPath).Load();

        Assert.Equal(82, loaded.Volume);
        Assert.Empty(loaded.ResumePositions);
    }

    [Fact]
    public async Task SettingsSave_RoundTripsWithoutLeavingTemporaryFiles()
    {
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var service = new SettingsService(settingsPath);

        await service.SaveAsync(new PlayerSettings { Volume = 37 });

        Assert.Equal(37, service.Load().Volume);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string? childName = null)
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"NoirMediaPlayer.Tests-{Guid.NewGuid():N}");
            Path = childName is null ? root : System.IO.Path.Combine(root, childName);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var root = Directory.GetParent(Path)?.FullName;
            if (root is not null && System.IO.Path.GetFileName(root).StartsWith("NoirMediaPlayer.Tests-", StringComparison.Ordinal))
            {
                Directory.Delete(root, recursive: true);
            }
            else if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
