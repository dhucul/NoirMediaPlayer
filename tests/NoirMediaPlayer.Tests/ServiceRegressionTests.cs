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
    public void DiscEjectTarget_PrefersCurrentDiscDriveAndRejectsAmbiguousFallback()
    {
        var opticalDrives = new[] { @"H:\", @"D:\" };

        var currentDrive = DiscService.SelectEjectTarget(
            opticalDrives,
            "bluray:///H:/",
            "dvd:///D:/");
        var ambiguousDrive = DiscService.SelectEjectTarget(
            opticalDrives,
            "bluray:///C:/disc-folder/");
        var soleDrive = DiscService.SelectEjectTarget(
            [@"D:\"],
            "bluray:///C:/disc-folder/");

        Assert.Equal(@"H:\", currentDrive, ignoreCase: true);
        Assert.Null(ambiguousDrive);
        Assert.Equal(@"D:\", soleDrive, ignoreCase: true);
        Assert.True(DiscService.IsDiscSourceOnDrive("dvd:///D:/", @"D:\"));
        Assert.False(DiscService.IsDiscSourceOnDrive("bluray:///C:/disc-folder/", @"D:\"));
    }

    [Theory]
    [InlineData("https://example.com/disc")]
    [InlineData("bluray://server/share")]
    [InlineData("not-a-disc-source")]
    public void DiscEjectTarget_RejectsNonLocalDiscSources(string source)
    {
        Assert.False(DiscService.TryGetDiscRoot(source, out _));
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

    [Fact]
    public async Task AacsKeyDatabaseStartupRepair_ExtractsArchiveInPlaceAndKeepsBackup()
    {
        using var temp = new TempDirectory();
        var keyDatabasePath = Path.Combine(temp.Path, "KEYDB.cfg");

        using (var archive = ZipFile.Open(keyDatabasePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("keydb.cfg", CompressionLevel.SmallestSize);
            await using var stream = entry.Open();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync("; KEYDB test data\n| DK | DEVICE_KEY 0x0011223344556677 | DEVICE_NODE 0x0011 |\n");
        }

        var result = await AacsService.RepairCompressedKeyDatabaseIfNeededAsync(keyDatabasePath);

        Assert.NotNull(result);
        Assert.True(result.Success, result.Message);
        Assert.True(AacsService.IsValidKeyDatabaseContent(keyDatabasePath));
        Assert.True(File.Exists(keyDatabasePath + ".backup"));
        Assert.Equal((byte)'P', File.ReadAllBytes(keyDatabasePath + ".backup")[0]);
    }

    [Fact]
    public async Task AacsKeyDatabaseStartupRepair_PreservesUnusableArchive()
    {
        using var temp = new TempDirectory();
        var keyDatabasePath = Path.Combine(temp.Path, "KEYDB.cfg");

        using (var archive = ZipFile.Open(keyDatabasePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("error.html");
            await using var stream = entry.Open();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync("<html>server error</html>");
        }

        var originalLength = new FileInfo(keyDatabasePath).Length;
        var result = await AacsService.RepairCompressedKeyDatabaseIfNeededAsync(keyDatabasePath);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.True(File.Exists(keyDatabasePath));
        Assert.Equal(originalLength, new FileInfo(keyDatabasePath).Length);
        using var preservedArchive = ZipFile.OpenRead(keyDatabasePath);
        Assert.Single(preservedArchive.Entries);
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
    public void AacsKeyDatabaseValidation_RejectsAnOversizedLine()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "KEYDB.cfg");
        File.WriteAllText(path, new string('A', 32 * 1024) + " = value");

        Assert.False(AacsService.IsValidKeyDatabaseContent(path));
    }

    [Fact]
    public async Task AacsKeyDatabaseInstall_AcceptsExistingPlaintextInPlace()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "KEYDB.cfg");
        const string content = "0x00112233445566778899AABBCCDDEEFF = Test disc | V | 0x0011 |\n";
        await File.WriteAllTextAsync(path, content);

        var result = await AacsService.InstallDownloadedKeyDatabaseAsync(path, path);

        Assert.True(result.Success, result.Message);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".backup"));
    }

    [Fact]
    public void AacsKeyDatabaseDownload_UsesAuthenticatedTransport()
    {
        Assert.Equal(Uri.UriSchemeHttps, AacsService.KeyDatabaseDownloadUri.Scheme);
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
        var resumePositions = new OrderedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in Enumerable.Range(0, 300))
        {
            resumePositions[$"video-{index}.mp4"] = index + 1;
        }

        var settings = new PlayerSettings
        {
            Volume = 500,
            ResumePositions = resumePositions
        };
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));

        var loaded = new SettingsService(settingsPath).Load();

        Assert.Equal(100, loaded.Volume);
        Assert.Equal(250, loaded.ResumePositions.Count);
        Assert.DoesNotContain("video-0.mp4", loaded.ResumePositions.Keys);
        Assert.Contains("video-299.mp4", loaded.ResumePositions.Keys);

        // The trim must keep the newest entries in order, not an arbitrary 250 of them.
        Assert.Equal("video-50.mp4", loaded.ResumePositions.Keys.First());
        Assert.Equal("video-299.mp4", loaded.ResumePositions.Keys.Last());
    }

    [Fact]
    public void SettingsLoad_MatchesKeysCaseInsensitivelyAfterARoundTrip()
    {
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var resumePositions = new OrderedDictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Media\Film.mkv"] = 42_000
        };
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new PlayerSettings
        {
            ResumePositions = resumePositions
        }));

        var loaded = new SettingsService(settingsPath).Load();

        // System.Text.Json replaces the property with a default-comparer instance, so Normalize
        // has to rebuild it or resume lookups start missing on a differently cased path.
        Assert.True(loaded.ResumePositions.TryGetValue(@"c:\media\film.mkv", out var position));
        Assert.Equal(42_000, position);
    }

    [Fact]
    public async Task SettingsSave_IsSuppressedWhenTheExistingFileCouldNotBeRead()
    {
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        const string original = """{"Volume":11}""";
        await File.WriteAllTextAsync(settingsPath, original);

        var service = new SettingsService(settingsPath);
        PlayerSettings loaded;
        using (File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            loaded = service.Load();
        }

        Assert.True(service.LoadFailed);
        Assert.Equal(82, loaded.Volume);

        await service.SaveAsync(loaded);

        // The intact file must survive: writing the fallback defaults over it would silently
        // destroy the user's real settings.
        Assert.Equal(original, await File.ReadAllTextAsync(settingsPath));
    }

    [Fact]
    public void SettingsLoad_OverwritesGenuinelyCorruptContent()
    {
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(settingsPath, "{ this is not json");

        var service = new SettingsService(settingsPath);
        var loaded = service.Load();

        Assert.False(service.LoadFailed);
        Assert.Equal(82, loaded.Volume);
    }

    [Theory]
    [InlineData("rtsp://user:secret@camera.example/live", "rtsp://camera.example/live")]
    [InlineData("https://user:secret@example.com/a.mp4?token=x", "https://example.com/a.mp4?token=x")]
    [InlineData("https://example.com/a.mp4", "https://example.com/a.mp4")]
    [InlineData(@"C:\Media\Film.mkv", @"C:\Media\Film.mkv")]
    public void RedactCredentials_RemovesUserInfoAndLeavesEverythingElsePlayable(string source, string expected)
    {
        Assert.Equal(expected, MediaSourceService.RedactCredentials(source));
    }

    [Fact]
    public void NetworkItem_KeepsCredentialsForPlaybackButNeverShowsThem()
    {
        var item = MediaSourceService.CreateNetworkItem("rtsp://user:secret@camera.example/live");

        Assert.Contains("secret", item.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", item.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", item.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderScan_ReportsATruncatedWalkInsteadOfLookingComplete()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "valid.mp3"), []);

        var healthy = new MediaScanDiagnostics();
        var found = MediaSourceService.ExpandFiles([temp.Path], CancellationToken.None, healthy).ToArray();

        Assert.Single(found);
        Assert.False(healthy.Faulted);

        var faulted = new MediaScanDiagnostics();
        var missing = MediaSourceService
            .EnumerateFolder(Path.Combine(temp.Path, "does-not-exist"), CancellationToken.None, faulted)
            .ToArray();

        Assert.Empty(missing);
        Assert.True(faulted.Faulted);
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
