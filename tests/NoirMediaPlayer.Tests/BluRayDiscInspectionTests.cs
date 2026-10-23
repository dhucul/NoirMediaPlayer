using System.IO;
using NoirMediaPlayer.Services;
using Xunit;
using static NoirMediaPlayer.Tests.BluRayFixture;

namespace NoirMediaPlayer.Tests;

public sealed class BluRayDiscInspectionTests
{
    [Fact]
    public void MetadataExport_ReportsEveryPlaylistWithoutInferringMainTitleOrPlayback()
    {
        WithFolder(root =>
        {
            var folder = Path.Combine(root, "BDMV", "PLAYLIST");
            Directory.CreateDirectory(folder);
            var shorter = Playlist();
            var longer = Playlist([Item(output: 900000)]);
            File.WriteAllBytes(Path.Combine(folder, "00001.mpls"), shorter);
            File.WriteAllBytes(Path.Combine(folder, "00000.mpls"), longer);
            File.WriteAllBytes(Path.Combine(folder, "00002.mpls"), "not MPLS"u8.ToArray());
            File.WriteAllText(Path.Combine(folder, "ignored.txt"), "Do not inspect this.");
            var report = BluRayDiscInspection.Read(root);
            Assert.False(report.PlaybackVerified);
            Assert.False(report.MainTitleSelected);
            Assert.Equal(new[] { "00000.mpls", "00001.mpls", "00002.mpls" }, report.Playlists.Select(item => item.File));
            Assert.NotNull(report.Playlists[0].Playlist);
            Assert.NotNull(report.Playlists[1].Playlist);
            Assert.Null(report.Playlists[2].Playlist);
            Assert.NotEmpty(report.Playlists[2].Error!);
            Assert.Equal(3, BluRayDiscInspection.Read(Path.Combine(root, "BDMV")).Playlists.Count);
            Assert.Single(BluRayDiscInspection.Read(Path.Combine(folder, "00001.mpls")).Playlists);
            Assert.Equal(shorter, File.ReadAllBytes(Path.Combine(folder, "00001.mpls")));
            Assert.Equal(longer, File.ReadAllBytes(Path.Combine(folder, "00000.mpls")));
            Assert.False(Directory.Exists(Path.Combine(root, "BDMV", "STREAM")));
            Assert.False(Directory.Exists(Path.Combine(root, "AACS")));
        });
    }

    [Fact]
    public void Cancellation_IsObservedBeforeReadingPlaylists()
    {
        WithFolder(root =>
        {
            var folder = Path.Combine(root, "BDMV", "PLAYLIST");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "00001.mpls"), Playlist());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => BluRayDiscInspection.Read(root, cancellation.Token));
        });
    }

    [Fact]
    public void EqualDurationPlaylists_KeepTheirDistinctChapterInventories()
    {
        // The supplied real-disc export has this layout: two playlists use the
        // same clip and duration, with different chapter lists. Neither can be
        // discarded or designated the main title using duration alone.
        WithFolder(root =>
        {
            var folder = Path.Combine(root, "BDMV", "PLAYLIST");
            Directory.CreateDirectory(folder);
            var item = Item("00002", 90000, 2790000, table: Table(
                video: [Stream(0x1011, 0x1b, [0x64])],
                audio: [Stream(0x1100, 0x86, Join([0x61], Text("eng")))],
                pg: [Stream(0x1200, 0x90, Text("eng"))]));
            File.WriteAllBytes(Path.Combine(folder, "00002.mpls"), Playlist([item], [Mark(time: 90000)]));
            var chapters = Enumerable.Range(0, 8).Select(index => Mark(time: (uint)(90000 + index * 315000))).ToArray();
            File.WriteAllBytes(Path.Combine(folder, "01002.mpls"), Playlist([item], chapters));
            var report = BluRayDiscInspection.Read(root);
            Assert.False(report.MainTitleSelected);
            var first = report.Playlists[0].Playlist!;
            var second = report.Playlists[1].Playlist!;
            Assert.Equal(first.SequentialDurationTicks, second.SequentialDurationTicks);
            Assert.Equal(first.Items[0].Clips[0].Id, second.Items[0].Clips[0].Id);
            Assert.Single(first.LinearChapterStartsMilliseconds!);
            Assert.Equal(8, second.LinearChapterStartsMilliseconds!.Count);
            Assert.Equal((byte)0x86, second.Items[0].Streams[1].CodingType);
            Assert.Equal("eng", second.Items[0].Streams[2].Language);
        });
    }

    [Fact]
    public void EmptyAndMissingInventories_AreExplicitFailures()
    {
        WithFolder(root =>
        {
            Assert.Throws<DirectoryNotFoundException>(() => BluRayDiscInspection.Read(root));
            Directory.CreateDirectory(Path.Combine(root, "BDMV", "PLAYLIST"));
            Assert.Throws<InvalidDataException>(() => BluRayDiscInspection.Read(root));
        });
    }

    [Fact]
    public void OversizedMetadata_IsRejectedBeforeItIsRead()
    {
        WithFolder(root =>
        {
            var folder = Path.Combine(root, "BDMV", "PLAYLIST");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "00001.mpls");
            using (var output = File.Create(file)) output.SetLength(BluRayPlaylistReader.MaximumFileBytes + 1);
            Assert.NotNull(Assert.Single(BluRayDiscInspection.Read(root).Playlists).Error);
            using (var output = File.Create(file)) output.SetLength(32 * 1024 * 1024 + 1);
            Assert.Throws<InvalidDataException>(() => BluRayDiscInspection.Read(root));
        });
    }

    private static void WithFolder(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "NoirDiscInspection-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try { test(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
