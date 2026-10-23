using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class SubtitleDiscoveryTests
{
    internal const string Caption = "1\n00:00:00,000 --> 00:00:25,000\n<b>Selected caption</b>\n";
    internal const string WebCaption = "WEBVTT\n\n00:00.000 --> 00:25.000\n<i>Other caption</i>\n";

    [Theory]
    [InlineData("film.en.srt")]
    [InlineData("FILM.FRA.srt")]
    [InlineData("film.pt-BR.vtt")]
    [InlineData("film.es-419.srt")]
    [InlineData("subs/film.srt")]
    [InlineData("Subtitles/film.en.vtt")]
    [InlineData("film.en.forced.srt")]
    [InlineData("film.en_US.vtt")]
    [InlineData("film.English.srt")]
    [InlineData("film alternate.srt")]
    [InlineData("film_en.srt")]
    [InlineData("film - French.srt")]
    [InlineData("film (English SDH).vtt")]
    [InlineData("film[eng].srt")]
    [InlineData("subtitles/film.en.forced.vtt")]
    [InlineData("film.webvtt")]
    [InlineData("subs/film.en.webvtt")]
    public void SingleSupportedQualifiedOrFolderSidecarIsSelected(string name) => InDirectory(root =>
    {
        var path = Write(root, name);
        var plan = WindowsSubtitleDiscovery.Inspect(Path.Combine(root, "film.avi"));
        Assert.NotNull(plan);
        Assert.Equal(path, Assert.Single(plan.AutomaticSubtitles).Path, ignoreCase: true);
        Assert.Equal(path, plan.SelectedSubtitlePath, ignoreCase: true);
        Assert.NotEmpty(plan.AutomaticSubtitles[0].Cues[0].Styles);
    });

    [Theory]
    [InlineData("film.srt", "film.en.vtt", "film.fr.srt")]
    [InlineData("subs/film.vtt", "film.en.srt", "subtitles/film.fr.vtt")]
    [InlineData("film.srt", "subs/film.en.srt", "Subtitles/film.en.srt")]
    [InlineData("film.webvtt", "film.en.forced.vtt", "subtitles/film.fr_FR.srt")]
    [InlineData("subs/film.srt", "film (English SDH).webvtt", "subtitles/film_es-419.srt")]
    public void MultipleSidecarsKeepEveryTrackAndPreferTheUniqueExactName(string exact, string second, string third) => InDirectory(root =>
    {
        var selected = Write(root, exact);
        var expected = new[] { selected, Write(root, second), Write(root, third) };
        var plan = WindowsSubtitleDiscovery.Inspect(Path.Combine(root, "film.avi"));
        Assert.NotNull(plan);
        Assert.Equal(3, plan.AutomaticSubtitles.Count);
        Assert.True(expected.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(plan.AutomaticSubtitles.Select(track => track.Path)));
        Assert.Equal(selected, plan.SelectedSubtitlePath, ignoreCase: true);
        Assert.Equal(selected, plan.AutomaticSubtitles[0].Path, ignoreCase: true);
    });

    [Theory]
    [InlineData("film.en.srt", "film.fr.srt")]
    [InlineData("film.srt", "film.vtt")]
    [InlineData("film.srt", "subs/film.srt")]
    [InlineData("subs/film.srt", "subtitles/film.srt")]
    [InlineData("film.srt", "film.en.ass")]
    [InlineData("film.srt", "film.en.mpl2")]
    [InlineData("film.srt", "film.en.tt")]
    [InlineData("film.srt", "film.cdg")]
    [InlineData("film.en.forced.srt", "film.fr_FR.webvtt")]
    [InlineData("film.srt", "film.en.forced.ass")]
    [InlineData("film.srt", "filmography.srt")]
    [InlineData("film.srt", "other film.en.srt")]
    [InlineData("film.srt", "film....srt")]
    [InlineData("film.srt", "film.日本語.srt")]
    [InlineData("film.srt", "film@en.srt")]
    [InlineData("film.srt", "sub/film.srt")]
    public void AmbiguousOrUnmigratedTracksRetainCompatibility(string first, string second) => InDirectory(root =>
    {
        Write(root, first);
        Write(root, second);
        Assert.Null(WindowsSubtitleDiscovery.Inspect(Path.Combine(root, "film.avi")));
    });

    [Fact]
    public void UnsupportedContentInASecondaryTrackCannotBeDropped() => InDirectory(root =>
    {
        Write(root, "film.srt");
        var path = Write(root, "subs/film.fr.srt");
        File.WriteAllText(path, Caption.Replace("<b>Selected caption</b>", "{\\an8\\pos(10,20)}Positioned"));
        Assert.Null(WindowsSubtitleDiscovery.Inspect(Path.Combine(root, "film.avi")));
    });

    [Fact]
    public void UnsupportedWebVttAliasCannotBeDroppedFromDiscovery() => InDirectory(root =>
    {
        Write(root, "film.srt");
        var path = Write(root, "subtitles/film.en.forced.webvtt");
        File.WriteAllText(path, "WEBVTT\n\nSTYLE\n::cue { color: red; }\n\n00:00.000 --> 00:25.000\nCSS caption\n");
        Assert.Null(WindowsSubtitleDiscovery.Inspect(Path.Combine(root, "film.avi")));
    });

    [Fact]
    public void UnrelatedAndDotFilesDoNotBlockSupportedSidecars() => InDirectory(root =>
    {
        var selected = Write(root, "film.srt");
        Write(root, "unrelated.ass");
        Write(root, "subs/another.en.ass");
        Write(root, ".film.fr.srt");
        Write(root, "subs/.film.srt");
        var plan = WindowsSubtitleDiscovery.Inspect(Path.Combine(root, "film.avi"));
        Assert.NotNull(plan);
        Assert.Equal(selected, Assert.Single(plan.AutomaticSubtitles).Path);
    });

    [Fact]
    public void NonAsciiSourceKeepsExactSupportAndRetainsUncertainDiscovery() => InDirectory(root =>
    {
        var selected = Write(root, "映画.srt");
        var source = Path.Combine(root, "映画.avi");
        Assert.Equal(selected, WindowsSubtitleDiscovery.Inspect(source)?.SelectedSubtitlePath);
        Write(root, "other.srt");
        Assert.Null(WindowsSubtitleDiscovery.Inspect(source));
    });

    [Fact]
    public void CandidateLimitRetainsCompatibilityInsteadOfTruncatingTracks() => InDirectory(root =>
    {
        Write(root, "film.srt");
        for (var index = 0; index < 32; index++) Write(root, $"film.{(char)('a' + index / 26)}{(char)('a' + index % 26)}.srt");
        Assert.Null(WindowsSubtitleDiscovery.Inspect(Path.Combine(root, "film.avi")));
    });

    private static string Write(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Path.GetExtension(path).ToLowerInvariant() is ".vtt" or ".webvtt" ? WebCaption : Caption);
        return path;
    }

    private static void InDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "NoirDiscovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            Assert.StartsWith("NoirDiscovery-", Path.GetFileName(root));
            Directory.Delete(root, recursive: true);
        }
    }
}
