using System.IO.Compression;
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
        Assert.False(DiscService.IsAacsProtectedSource(bluRayItem.Source));

        Directory.CreateDirectory(Path.Combine(temp.Path, "AACS"));
        Assert.True(DiscService.IsAacsProtectedSource(bluRayItem.Source));
    }

    [Theory]
    [InlineData("https://example.com/disc", false)]
    [InlineData("bluray://server/share", false)]
    [InlineData("bluray:not-a-rooted-path", false)]
    public void DiscProtection_RejectsNonLocalDiscSources(string source, bool expected)
    {
        Assert.Equal(expected, DiscService.IsAacsProtectedSource(source));
    }

    [Fact]
    public void AacsLibraryPath_RequiresTheExpectedDllNameAndNormalizesIt()
    {
        using var temp = new TempDirectory();
        var libraryPath = Path.Combine(temp.Path, "libaacs.dll");
        File.WriteAllBytes(libraryPath, []);

        Assert.True(AacsService.TryNormalizeLibraryPath(libraryPath, out var normalizedPath));
        Assert.Equal(Path.GetFullPath(libraryPath), normalizedPath);
        Assert.False(AacsService.TryNormalizeLibraryPath(Path.Combine(temp.Path, "other.dll"), out _));
        Assert.False(AacsService.TryNormalizeLibraryPath("libaacs.dll", out _));
    }

    [Fact]
    public void AacsLibraryChange_RequiresRestartOnlyWhenTheSelectedRuntimeDiffers()
    {
        using var activeDirectory = new TempDirectory();
        var activePath = Path.Combine(activeDirectory.Path, "libaacs.dll");
        var selectedDirectory = Path.Combine(activeDirectory.Path, "alternate");
        Directory.CreateDirectory(selectedDirectory);
        var selectedPath = Path.Combine(selectedDirectory, "libaacs.dll");
        var currentStatus = new AacsRuntimeStatus(
            AacsRuntimeState.Ready,
            "ready",
            activePath,
            KeyDatabaseFound: false);

        Assert.False(AacsService.LibraryChangeRequiresRestart(activePath, currentStatus));
        Assert.True(AacsService.LibraryChangeRequiresRestart(selectedPath, currentStatus));
        Assert.False(AacsService.LibraryChangeRequiresRestart(string.Empty, currentStatus));
    }

    [Fact]
    public async Task AacsKeyDatabaseInstall_ExtractsDownloadedZipInsteadOfInstallingItAsCfg()
    {
        using var temp = new TempDirectory();
        var downloadPath = Path.Combine(temp.Path, "keydb-download");
        var destinationPath = Path.Combine(temp.Path, "KEYDB.cfg");

        using (var archive = ZipFile.Open(downloadPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("keydb.cfg", CompressionLevel.SmallestSize);
            await using var stream = entry.Open();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync("; KEYDB test data\n| DK | DEVICE_KEY 0x0011223344556677 | DEVICE_NODE 0x0011 |\n");
        }

        var result = await AacsService.InstallDownloadedKeyDatabaseAsync(downloadPath, destinationPath);

        Assert.True(result.Success, result.Message);
        Assert.True(AacsService.IsValidKeyDatabaseContent(destinationPath));
        Assert.Equal((byte)';', File.ReadAllBytes(destinationPath)[0]);
    }

    [Fact]
    public async Task AacsKeyDatabaseInstall_RejectsArchiveWithoutKeyDatabaseAndPreservesExistingFile()
    {
        using var temp = new TempDirectory();
        var downloadPath = Path.Combine(temp.Path, "keydb-download");
        var destinationPath = Path.Combine(temp.Path, "KEYDB.cfg");
        const string existingKeyDatabase = "0x00112233445566778899AABBCCDDEEFF = Test disc | V | 0x0011 |\n";
        await File.WriteAllTextAsync(destinationPath, existingKeyDatabase);

        using (var archive = ZipFile.Open(downloadPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("error.html");
            await using var stream = entry.Open();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync("<html>server error</html>");
        }

        var result = await AacsService.InstallDownloadedKeyDatabaseAsync(downloadPath, destinationPath);

        Assert.False(result.Success);
        Assert.Equal(existingKeyDatabase, await File.ReadAllTextAsync(destinationPath));
    }

    [Theory]
    [InlineData("<html>server error</html>", false)]
    [InlineData("; comments only\n# still comments\n", false)]
    [InlineData("; comment\n| DK | DEVICE_KEY 0x0011223344556677 | DEVICE_NODE 0x0011 |", true)]
    [InlineData("0x00112233445566778899AABBCCDDEEFF = Test disc | V | 0x0011 |", true)]
    public void AacsKeyDatabaseValidation_RequiresPlaintextKeyDatabaseSyntax(string content, bool expected)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "KEYDB.cfg");
        File.WriteAllText(path, content);

        Assert.Equal(expected, AacsService.IsValidKeyDatabaseContent(path));
    }

    [Fact]
    public void PlaybackStartupPolicy_WatchesDiscsAndExplainsUnverifiedAacsKeys()
    {
        Assert.Equal(PlaybackStartupPolicy.DiscTimeout, PlaybackStartupPolicy.GetTimeout(false, true));
        Assert.Equal(PlaybackStartupPolicy.NetworkTimeout, PlaybackStartupPolicy.GetTimeout(true, false));
        Assert.Null(PlaybackStartupPolicy.GetTimeout(false, false));

        var failure = PlaybackStartupPolicy.DescribeDiscFailure(
            isAacsProtected: true,
            keyDatabaseFound: true,
            timedOut: true);

        Assert.Equal("AACS UNLOCK FAILED", failure.EngineStatus);
        Assert.Contains("matching key", failure.DialogMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("licensed Blu-ray", failure.DialogMessage, StringComparison.OrdinalIgnoreCase);
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
