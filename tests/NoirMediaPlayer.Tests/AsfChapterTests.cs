using System.IO;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class AsfChapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsMarkers_ReadEncodedAudioAndVideoWithoutChangingTheFile(bool video)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirAsf-" + Guid.NewGuid() + (video ? ".wmv" : ".wma"));
        try
        {
            AsfFixture.Write(path, video, 0, 4000, 8000);
            var original = File.ReadAllBytes(path);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            var chapters = WindowsAsfChapters.TryRead(path);
            Assert.NotNull(chapters);
            Assert.Equal(new long[] { 0, 4000, 8000 }, chapters.Starts);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("long-name")]
    [InlineData("too-many")]
    public void WindowsMarkers_UnsupportedMetadataKeepsCompatibilityNavigation(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirAsf-" + Guid.NewGuid() + ".wma");
        try
        {
            AsfFixture.Write(path, false, 0, 4000, 8000);
            AsfFixture.Edit(path, header =>
            {
                if (kind == "duplicate") AsfFixture.Marker(header, 40_000_000);
                if (kind == "long-name") AsfFixture.Marker(header, 60_000_000, new string('A', 4097));
                if (kind == "too-many") for (var index = 0; index < 4097; index++) AsfFixture.Marker(header, 100_000_000);
            });
            var original = File.ReadAllBytes(path);
            Assert.Null(WindowsAsfChapters.TryRead(path));
            var plan = WindowsPlaybackPolicy.CreatePlan(new PlaylistItem { Source = path, Title = "Unsupported markers" }, new PlayerSettings(), 0, null);
            Assert.NotNull(plan); // Basic playback can still use Windows.
            Assert.Null(plan.Chapters);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void WindowsMarkers_UseTheCanonicalTimelineReturnedByWindows()
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirAsf-" + Guid.NewGuid() + ".wma");
        try
        {
            AsfFixture.Write(path, false, 0, 4000, 8000);
            AsfFixture.Edit(path, header => AsfFixture.Marker(header, 50_000_001));
            var chapters = WindowsAsfChapters.TryRead(path);
            Assert.NotNull(chapters);
            // Windows normalizes the written marker to whole milliseconds.
            Assert.Equal(new long[] { 0, 4000, 5000, 8000 }, chapters.Starts);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(".wmv")]
    [InlineData(".wma")]
    [InlineData(".asf")]
    public void WindowsMarkers_RoutingKeepsAValidatedChapterlessMap(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirAsf-" + Guid.NewGuid() + extension);
        try
        {
            AsfFixture.Write(path);
            var plan = WindowsPlaybackPolicy.CreatePlan(new PlaylistItem { Source = path, Title = "Chapterless" }, new PlayerSettings(), 0, null);
            Assert.NotNull(plan?.Chapters);
            Assert.Empty(plan.Chapters.Starts);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsMarkers_UnavailableOrInvalidFileDoesNotAdmitAChapterMap(bool exists)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirAsf-" + Guid.NewGuid() + ".wmv");
        try
        {
            if (exists) File.WriteAllBytes(path, "Not an ASF header"u8.ToArray());
            Assert.Null(WindowsAsfChapters.TryRead(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
