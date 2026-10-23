using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Xunit;

namespace NoirMediaPlayer.Tests;

[Collection("Player UI")]
public sealed class DvdCompatibilityRuntimeTests
{
    [Theory]
    [InlineData("libdvdnav_plugin.dll")]
    [InlineData("libdvdread_plugin.dll")]
    [InlineData("liblibbluray_plugin.dll")]
    public void PackagedDiscPlugin_MatchesCurrentRuntimeAndLoads(string name)
    {
        var runtime = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        var plugin = Path.Combine(runtime, "plugins", "access", name);
        Assert.True(File.Exists(plugin), $"The disc plug-in was not packaged: {plugin}");
        var expectedVersion = FileVersionInfo.GetVersionInfo(Path.Combine(runtime, "libvlc.dll"));
        var actualVersion = FileVersionInfo.GetVersionInfo(plugin);
        Assert.Equal((expectedVersion.FileMajorPart, expectedVersion.FileMinorPart, expectedVersion.FileBuildPart),
            (actualVersion.FileMajorPart, actualVersion.FileMinorPart, actualVersion.FileBuildPart));

        // Activation and ABI compatibility only: no drive is scanned or opened.
        Core.Initialize(runtime);
        using var engine = new LibVLC("--quiet", "--no-video-title-show");
        var module = NativeLibrary.Load(plugin);
        try { Assert.True(NativeLibrary.TryGetExport(module, "vlc_entry__3_0_0f", out _)); }
        finally { NativeLibrary.Free(module); }
    }

    [Theory]
    [InlineData("libaacs.dll", "aacs_open")]
    [InlineData("libgcrypt-20.dll", "gcry_check_version")]
    [InlineData("libgpg-error-0.dll", "gpg_strerror")]
    public void PackagedAacsComponents_LoadAndExposeTheirApi(string name, string export)
    {
        var runtime = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        Core.Initialize(runtime);
        var module = NativeLibrary.Load(Path.Combine(runtime, name));
        try { Assert.True(NativeLibrary.TryGetExport(module, export, out _), $"Missing {export} in {name}"); }
        finally { NativeLibrary.Free(module); }
    }

    [Fact]
    public void DvdCompatibilityNotices_AreCopiedAlongsideTheRuntime()
    {
        var notices = Path.Combine(AppContext.BaseDirectory, "licenses", "dvd");
        Assert.Contains("GPL-2.0-or-later", File.ReadAllText(Path.Combine(notices, "README.md")));
        Assert.Contains("GNU GENERAL PUBLIC LICENSE", File.ReadAllText(Path.Combine(notices, "GPL-2.0.txt")));
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", File.ReadAllText(Path.Combine(notices, "LGPL-2.1.txt")));
    }
}
