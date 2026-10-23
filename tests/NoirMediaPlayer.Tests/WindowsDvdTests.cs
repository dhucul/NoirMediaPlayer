using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class WindowsDvdTests
{
    [Fact]
    public void AbiStructures_MatchTheWindowsSdk()
    {
        Assert.Equal(32, Marshal.SizeOf<WindowsDvdSession.RenderStatus>());
        Assert.Equal(16, Marshal.SizeOf<WindowsDvdSession.DvdLocation>());
        Assert.Equal(4, Marshal.SizeOf<WindowsDvdSession.DvdTimeCode>());
        Assert.Equal(88, Marshal.SizeOf<WindowsDvdSession.NativeMediaType>());
        Assert.Equal(64, Marshal.OffsetOf<WindowsDvdSession.NativeMediaType>("Unknown").ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<WindowsDvdSession.NativeMediaType>("Format").ToInt32());
    }

    [Fact]
    public void PartialOrEmptyGraphs_AreNotAcceptedAsPlaybackReady()
    {
        var complete = new WindowsDvdSession.RenderStatus { StreamCount = 3 };
        Assert.True(WindowsDvdSession.IsCompleteGraph(0, complete));
        Assert.False(WindowsDvdSession.IsCompleteGraph(1, complete));
        Assert.False(WindowsDvdSession.IsCompleteGraph(unchecked((int)0x80004005), complete));
        Assert.False(WindowsDvdSession.IsCompleteGraph(0, new WindowsDvdSession.RenderStatus()));
        foreach (var status in new[]
        {
            new WindowsDvdSession.RenderStatus { StreamCount = 3, FailedStreams = 1 },
            new WindowsDvdSession.RenderStatus { StreamCount = 3, FailedStreamFlags = 4 },
            new WindowsDvdSession.RenderStatus { StreamCount = 3, InvalidVolume = 1 },
            new WindowsDvdSession.RenderStatus { StreamCount = 3, UnknownVolume = 1 }
        }) Assert.False(WindowsDvdSession.IsCompleteGraph(0, status));
    }

    [Fact]
    public void BuildFailure_PreservesTheNativeErrorCode()
    {
        const int missingDecoder = unchecked((int)0x8004027B);
        var exception = WindowsDvdSession.CreateBuildException(missingDecoder,
            new WindowsDvdSession.RenderStatus { StreamCount = 3, FailedStreams = 3, FailedStreamFlags = 7 });
        Assert.Equal(missingDecoder, exception.HResult);
        Assert.Contains("suitable DVD decoders", exception.Message);
        Assert.Contains("0x8004027B", exception.Message);
        Assert.Contains("failed streams 0x7", exception.Message);
        Assert.True(WindowsDvdSession.CreateBuildException(1, new WindowsDvdSession.RenderStatus()).HResult < 0);
    }

    [Fact]
    public void ExplicitConnections_RequireTheObservedDisconnectedWindowsGraph()
    {
        var ids = new[]
        {
            new Guid("9B8C4620-2C1A-11D0-8493-00A02438AD48"), new Guid("212690FB-83E5-4526-8FD7-74478B7939CD"),
            new Guid("E1F1A0B8-BEEE-490D-BA7C-066C40B5E2B9"), new Guid("51B4ABF3-748F-4E3B-A276-C828330E926A")
        };
        var filters = ids.Select(id => new WindowsDvdFilterDiagnostic(id,
            new[] { new WindowsDvdPinDiagnostic("Unconnected pin", 0, false, "0x80040209") })).ToArray();
        var observed = new WindowsDvdBuildDiagnostic("0x8004027B", 0x208, 3, 3, 7, false, false, filters, null);
        Assert.True(WindowsDvdSession.CanConnectExplicitly(observed));
        foreach (var unsupported in new[]
        {
            observed with { NativeResult = "0x80004005" }, observed with { InvalidVolume = true },
            observed with { UnknownVolume = true }, observed with { StreamCount = 2 },
            observed with { FailedStreams = 1 }, observed with { FailedStreamFlags = 1 },
            observed with { InspectionError = "Unavailable diagnostic" }, observed with { Filters = filters[..^1] },
            observed with { Filters = filters.Append(filters[0]).ToArray() },
            observed with { Filters = filters.Append(new WindowsDvdFilterDiagnostic(Guid.NewGuid(), filters[0].Pins)).ToArray() },
            observed with { Filters = filters.Select((filter, index) => index == 0 ? filter with { Pins = [] } : filter).ToArray() },
            observed with { Filters = filters.Select((filter, index) => index == 0 ? filter with
                { Pins = new[] { new WindowsDvdPinDiagnostic("Connected pin", 1, true, "0x00000000") } } : filter).ToArray() }
        }) Assert.False(WindowsDvdSession.CanConnectExplicitly(unsupported));
    }

    [Theory]
    [InlineData(100, false, 0)]
    [InlineData(50, false, -602)]
    [InlineData(10, false, -2000)]
    [InlineData(0, false, -10000)]
    [InlineData(100, true, -10000)]
    [InlineData(-1, false, -10000)]
    [InlineData(200, false, 0)]
    public void Volume_UsesDirectShowHundredthsOfDecibels(int volume, bool muted, int expected) =>
        Assert.Equal(expected, WindowsDvdSession.VolumeAttenuation(volume, muted));

    [Fact]
    public void TimeCodes_UseBinaryHmsfAndRespectPalNtscAndDropFrameClocks()
    {
        var pal = new WindowsDvdSession.DvdTimeCode { Hours = 1, Minutes = 2, Seconds = 3, Frames = 10 };
        Assert.Equal(3723400, pal.ToMilliseconds(1));
        Assert.Equal(3723333, pal.ToMilliseconds(2));
        var ntsc = new WindowsDvdSession.DvdTimeCode { Minutes = 1, Frames = 2 };
        Assert.Equal(60060, ntsc.ToMilliseconds(6));
        var tenthMinute = new WindowsDvdSession.DvdTimeCode { Minutes = 10 };
        Assert.Equal(599999, tenthMinute.ToMilliseconds(6));
        Assert.Null(new WindowsDvdSession.DvdTimeCode { Minutes = 1 }.ToMilliseconds(6));
        Assert.Null(new WindowsDvdSession.DvdTimeCode { Minutes = 60 }.ToMilliseconds(1));
        Assert.Null(new WindowsDvdSession.DvdTimeCode { Frames = 25 }.ToMilliseconds(1));
        var seek = WindowsDvdSession.DvdTimeCode.FromMilliseconds(3723456);
        Assert.Equal((byte)1, seek.Hours); Assert.Equal((byte)2, seek.Minutes); Assert.Equal((byte)3, seek.Seconds);
        Assert.Equal((byte)0, seek.Frames);
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsDvdSession.DvdTimeCode.FromMilliseconds(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsDvdSession.DvdTimeCode.FromMilliseconds(256L * 3600000));
    }

    [Theory]
    [InlineData("")]
    [InlineData("VIDEO_TS")]
    [InlineData("dvd:///G:/")]
    [InlineData("\\\\server\\share\\VIDEO_TS")]
    public void SourceResolution_RequiresAnExplicitLocalFolder(string source) =>
        Assert.Throws<ArgumentException>(() => WindowsDvdSession.ResolveDirectory(source));

    [Fact]
    public void SourceResolution_ChecksTheVideoManagerReadOnlyWithoutScanningDrives()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoirDvdSource-" + Guid.NewGuid());
        var directory = Path.Combine(root, "VIDEO_TS");
        Directory.CreateDirectory(directory);
        var manager = Path.Combine(directory, "VIDEO_TS.IFO");
        try
        {
            Assert.Throws<FileNotFoundException>(() => WindowsDvdSession.ResolveDirectory(root));
            File.WriteAllBytes(manager, "not DVD"u8.ToArray());
            Assert.Throws<EndOfStreamException>(() => WindowsDvdSession.ResolveDirectory(root));
            File.WriteAllBytes(manager, "NOT-DVD-VMG!"u8.ToArray());
            Assert.Throws<InvalidDataException>(() => WindowsDvdSession.ResolveDirectory(root));
            File.WriteAllBytes(manager, "DVDVIDEO-VMG"u8.ToArray());
            using var readOnly = new FileStream(manager, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(directory, WindowsDvdSession.ResolveDirectory(root));
            Assert.Equal(directory, WindowsDvdSession.ResolveDirectory(directory));
            Assert.Equal("DVDVIDEO-VMG"u8.ToArray(), File.ReadAllBytes(manager));
        }
        finally
        {
            File.Delete(manager);
            Directory.Delete(directory);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task NativeInitialization_UsesWindowsComponentsAndEnforcesThreadOwnership()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new HwndSource(new HwndSourceParameters("NOIR DVD initialization test")
                {
                    Width = 160, Height = 90, PositionX = -5000, PositionY = -5000,
                    WindowStyle = unchecked((int)0x80000000)
                });
                using var session = new WindowsDvdSession(window.Handle);
                var classes = session.GetFilterClasses();
                Assert.Contains(new Guid("212690FB-83E5-4526-8FD7-74478B7939CD"), classes);
                Assert.Contains(new Guid("E1F1A0B8-BEEE-490D-BA7C-066C40B5E2B9"), classes);
                Assert.Contains(new Guid("51B4ABF3-748F-4E3B-A276-C828330E926A"), classes);
                var capabilities = session.GetVideoDecoderCapabilities();
                Assert.Equal(new[] { "DVD", "VMR9", "EVR" }, capabilities.Select(capability => capability.Name));
                Assert.All(capabilities, capability => Assert.StartsWith("0x", capability.HResult));
                var optionalCodecs = session.GetOptionalCodecAvailability();
                Assert.Equal(new[] { "Media Foundation MPEG-2", "Media Foundation H.264" }, optionalCodecs.Select(codec => codec.Name));
                Assert.All(optionalCodecs, codec => Assert.StartsWith("0x", codec.HResult));
                // Optional codec probes must not insert anything into the DVD graph.
                Assert.Equal(classes, session.GetFilterClasses());
                var diagnostic = session.InspectGraph();
                Assert.Equal("0x80040279", diagnostic.NativeResult);
                Assert.Null(diagnostic.InspectionError);
                Assert.Equal(classes.Count, diagnostic.Filters.Count);
                Assert.All(diagnostic.Filters, filter => Assert.NotEmpty(filter.Pins));
                Assert.All(diagnostic.Filters.SelectMany(filter => filter.Pins), pin => Assert.False(pin.Connected));
                Assert.Throws<InvalidOperationException>(() => session.Play());
                Assert.Throws<InvalidOperationException>(() => session.GetState());
                Assert.Throws<DirectoryNotFoundException>(() => session.Open(Path.Combine(Path.GetTempPath(), "NoirMissingDvd-" + Guid.NewGuid())));
                _ = session.DrainEvents();
                Task.Run(() => Assert.Throws<InvalidOperationException>(() => session.GetFilterClasses())).GetAwaiter().GetResult();
                session.Dispose(); session.Dispose();
                Assert.Throws<ObjectDisposedException>(() => session.GetFilterClasses());
                done.TrySetResult();
            }
            catch (Exception exception) { done.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
