using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

[Collection("Player UI")]
public sealed class NativeDvdStreamIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // H.264/AAC control in PS is encoded by Windows. It verifies the new byte
    // stream/render connection, not unavailable MPEG-2 or real-disc playback.
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task WindowsByteStreamDecodesPaintsResizesAndEndsControlMedia(bool videoOnly) => Run(async (window, surface) =>
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirDvdControl-" + Guid.NewGuid().ToString("N") + ".mpg");
        try
        {
            await Task.Run(() => MpegFixture.Write(path, videoOnly: videoOnly));
            using var stream = new DvdCellComStream(new ReadOnlyMemory<byte>[] { File.ReadAllBytes(path) });
            using var engine = new WindowsMediaEngine(surface.Handle, _ => { }, softwareVideo: true);
            engine.OpenDvdStream(stream, videoOnly ? 0 : 1, volume: 0, muted: true);
            await Until(() => { Assert.True(engine.ErrorCode >= 0, $"Native stream error 0x{engine.ErrorCode:X8}"); return engine.DvdCanPresent && engine.HasVideo && engine.HasSupportedStreams; });
            engine.UpdateVideo();
            await Until(() => engine.DvdPresentation.Frames > 0);
            var pixels = surface.Capture(); Assert.True(Colored(pixels) > 500, "Decoded video must reach the native window pixels.");
            await Until(() => engine.DvdPresentation.Painted);
            var beforePause = engine.Time; engine.Pause(); await Task.Delay(150); Assert.InRange(engine.Time, beforePause - 50, beforePause + 200);
            var basePixels = surface.Capture();
            var packet = DvdSpuFixture.Packet(240, 120, 120, 120);
            // Half-opacity, including both interlaced fields. Rendering must
            // restore the original paused frame on every selection or clear.
            var control = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
            DvdSpuFixture.U16(packet, control + 8, 0x8888);
            var spu = DvdSubpicture.Decode(packet, 0).At(0)!;
            var overlay = spu.Colorize(DvdSpuFixture.Palette, 480);
            engine.SetDvdSubpicture(overlay);
            Assert.False(engine.DvdPresentation.Painted);
            var composited = surface.Capture(); Assert.True(engine.DvdPresentation.Painted);
            Assert.True(Different(basePixels, composited) > 500, "Decoded SPU pixels must reach the native HWND.");
            engine.SetDvdSubpicture(overlay); Assert.Equal(composited, surface.Capture());
            engine.SetDvdSubpicture(spu.Colorize(DvdSpuFixture.Palette, 480, new(1, new(120, 120, 359, 239), 0x2222ffff, false)));
            Assert.True(Different(composited, surface.Capture()) > 500);
            engine.SetDvdSubpicture(null); Assert.Equal(basePixels, surface.Capture());
            engine.SetDvdSubpicture(overlay);
            window.Width = 240; window.Height = 520; window.UpdateLayout(); engine.UpdateVideo(force: true);
            pixels = surface.Capture(); Assert.True(Colored(pixels) > 500); Assert.True(BlackEdgeRows(pixels, surface.LastWidth, surface.LastHeight) > 60);
            var resizedOverlay = pixels;
            engine.SetDvdSubpicture(null); var resizedBase = surface.Capture(); Assert.True(Different(resizedBase, resizedOverlay) > 100);
            engine.SetDvdSubpicture(overlay); Assert.Equal(resizedOverlay, surface.Capture());
            engine.SetRotation(90); var rotated = surface.Capture();
            engine.SetDvdSubpicture(null); Assert.True(Different(rotated, surface.Capture()) > 100);
            engine.SetRotation(0);
            engine.Play();
            try { await Until(() => { surface.Capture(); return engine.DvdPresentation.Ended && engine.DvdPresentation.Painted; }, 30000); }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException($"Native EOF: time={engine.Time}, length={engine.Length}, paused={engine.IsPaused}, error=0x{engine.ErrorCode:X8}, video={engine.DvdPresentation}", error);
            }
            Assert.True(engine.DvdPresentation.Frames > 1); Assert.True(engine.HasAudio == !videoOnly);
            // The EOF frame is retained for still-menu handling and resize.
            Assert.True(Colored(surface.Capture()) > 500);
        }
        finally { File.Delete(path); }
    });

    [Theory]
    [InlineData(0L)] [InlineData(9_000_000L)]
    public Task WindowsFrameTimestampsScheduleSpuDuringMotionPauseAndRateChange(long shift) => Run(async (window, surface) =>
    {
        var control = await Task.Run(() => DvdMovingControlFixture.Create(shift));
        var packet = DvdSpuFixture.Packet(240, 120, 240, 120);
        var firstControl = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        var colorOffset = packet.Length; var stopOffset = colorOffset + 11;
        packet = packet.Concat(new byte[] { 0, 90, 0, 0, 3, 0x33, 0x33, 4, 0x88, 0x88, 255, 0, 180, 0, 0, 2, 255 }).ToArray();
        DvdSpuFixture.U16(packet, 0, packet.Length); DvdSpuFixture.U16(packet, firstControl + 2, colorOffset);
        DvdSpuFixture.U16(packet, colorOffset + 2, stopOffset); DvdSpuFixture.U16(packet, stopOffset + 2, stopOffset);
        var spu = new DvdSubpictureStream(); spu.AddPes(DvdSpuFixture.Pes(packet, shift + 90_000)); spu.Complete();
        var palette = DvdSpuFixture.Palette; palette[2] = palette[1]; palette[3] = 0x0051f05a;
        var timing = new DvdSubpictureTiming(spu, palette, new DvdVideoClock(control.Video, (uint)shift, checked((uint)(control.Video.Max() + 90000))));
        using var stream = new DvdCellComStream(new ReadOnlyMemory<byte>[] { control.Program });
        using var engine = new WindowsMediaEngine(surface.Handle, _ => { }, softwareVideo: true);
        engine.OpenDvdStream(stream, 1, volume: 0, muted: true, autoPlay: false); engine.SetDvdSubpictureTiming(timing);
        if (shift == 0) engine.SetDvdPaused(true);
        await Until(() => { Assert.True(engine.ErrorCode >= 0, $"Clock binding error 0x{engine.ErrorCode:X8}"); return engine.DvdCanPresent && engine.HasVideo && engine.HasSupportedStreams; });
        engine.UpdateVideo();
        await Until(() => { Assert.True(engine.ErrorCode >= 0, $"Frame clock error 0x{engine.ErrorCode:X8}: {engine.DvdFrameFailure}"); return engine.DvdPresentation.Frames > 0; });
        output.WriteLine($"Clock shift={shift}, first PES={control.Video[0]}, native100ns={engine.DvdFrameTime100ns}, mapped90k={engine.DvdSubpictureTime90Khz}, engineMs={engine.Time}");
        if (shift == 0)
        {
            var firstClock = engine.DvdSubpictureTime90Khz; await Task.Delay(150);
            Assert.True(engine.IsPaused); Assert.Equal(firstClock, engine.DvdSubpictureTime90Khz); engine.SetDvdPaused(false);
        }
        Assert.Throws<InvalidOperationException>(() => engine.SetDvdSubpictureTiming(timing));
        Assert.False(engine.DvdSubpictureVisible); Assert.False(White(PicturePixel(surface, engine)));
        await Until(() => { Assert.True(engine.ErrorCode >= 0); return engine.DvdSubpictureVisible; });
        Assert.True(White(PicturePixel(surface, engine)), "Timed SPU pixels must appear on the corresponding native video frame.");
        engine.Pause(); await Task.Delay(120); var pausedClock = engine.DvdSubpictureTime90Khz;
        var pausedPixels = surface.Capture(); await Task.Delay(250);
        Assert.Equal(pausedClock, engine.DvdSubpictureTime90Khz); Assert.Equal(pausedPixels, surface.Capture());
        window.Width = 280; window.Height = 520; window.UpdateLayout(); engine.SetRotation(90);
        Assert.True(White(PicturePixel(surface, engine, rotated: true))); Assert.Equal(pausedClock, engine.DvdSubpictureTime90Khz);
        engine.SetRate(2); engine.Play();
        await Until(() => { Assert.True(engine.ErrorCode >= 0); return engine.DvdSubpictureTime90Khz >= shift + 90_000 + 90 * 1024; });
        Assert.True(engine.DvdSubpictureVisible); Assert.False(White(PicturePixel(surface, engine, rotated: true)), "Authored colour/opacity updates must change the pixels.");
        await Until(() => { Assert.True(engine.ErrorCode >= 0); return engine.DvdSubpictureTime90Khz >= shift + 90_000 + 180 * 1024; });
        Assert.False(engine.DvdSubpictureVisible);
        engine.Pause(); await Task.Delay(120); var cleanPixels = surface.Capture(); engine.SetDvdSubpicture(null);
        Assert.Equal(cleanPixels, surface.Capture());
        Assert.Throws<NotSupportedException>(() => engine.Time = 0); // No silent stale-overlay MPEG seek.
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task WindowsMovingMenuPaintsSelectionActivationAndCurrentNavInput(bool forced) => Run(async (window, surface) =>
    {
        var control = await Task.Run(() => DvdMovingControlFixture.Create(0));
        var palette = DvdSpuFixture.Palette; var packet = DvdSpuFixture.Packet(420, 120, 120, 120);
        var commands = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        DvdSpuFixture.U16(packet, commands + 8, 0); // Only the selected button is visible.
        var spu = new DvdSubpictureStream(); spu.AddPes(DvdSpuFixture.Pes(packet, 90_000));
        var reads = ControlMenuReads((uint)(control.Video.Max() + 90_000), forced);
        var menu = new DvdMovingMenu(reads, new(DvdMenuDomain.Manager, 0, 1, 0), reads[0].State.Registers);
        var timing = new DvdSubpictureTiming(spu, palette, new DvdVideoClock(control.Video, 0, reads[^1].Unit!.Navigation.End90Khz), menu);
        using var stream = new DvdCellComStream(new ReadOnlyMemory<byte>[] { control.Program });
        using var engine = new WindowsMediaEngine(surface.Handle, _ => { }, softwareVideo: true);
        engine.OpenDvdStream(stream, 1, 0, true, autoPlay: false); engine.SetDvdSubpictureTiming(timing);
        await Until(() => engine.DvdCanPresent && engine.HasVideo && engine.HasSupportedStreams); engine.UpdateVideo();
        await Until(() => { Assert.True(engine.ErrorCode >= 0, engine.DvdFrameFailure); return engine.DvdSubpictureTime90Khz >= 90_000; });
        Assert.True(White(PicturePixel(surface, engine, sourceX: 210d / 720)));
        DvdMovingMenuCommand request;
        if (forced)
        {
            surface.Capture(); engine.PollDvdMenu(); // Automatic input only after the normal picture painted.
            surface.Capture(); request = engine.PollDvdMenu().Command!; Assert.NotNull(request);
            Assert.True(engine.IsPaused); var active = PicturePixel(surface, engine, sourceX: 210d / 720);
            Assert.True(active[2] > 240 && active[0] < 10, "Activated SPU highlight must paint red.");
            Assert.Equal(DvdMenuOutcome.Inactive, engine.DvdMenuInput(new(DvdMenuInputKind.Activate)).Outcome);
        }
        else
        {
            DvdMenuAction? moved = null;
            await Until(() => { surface.Capture(); moved = engine.DvdMenuInput(new(DvdMenuInputKind.Move)); return moved.Outcome != DvdMenuOutcome.Inactive; });
            Assert.Equal(DvdMenuOutcome.SelectionChanged, moved!.Outcome); Assert.False(engine.IsPaused);
            Assert.True(White(PicturePixel(surface, engine, sourceX: .625)));
            engine.SetDvdPaused(true); await Task.Delay(100);
            window.Width = 280; window.Height = 520; window.UpdateLayout(); engine.SetRotation(90); surface.Capture();
            Assert.Equal(DvdMenuOutcome.Inactive, engine.DvdMenuInput(new(DvdMenuInputKind.Click, X: 0, Y: 0)).Outcome);
            var point = PictureLocation(surface, engine, true, 210d / 720, .375);
            Assert.Equal(DvdMenuOutcome.SelectionChanged, engine.DvdMenuInput(new(DvdMenuInputKind.Point, X: point.X, Y: point.Y)).Outcome);
            Assert.True(White(PicturePixel(surface, engine, true, 210d / 720)));
            point = PictureLocation(surface, engine, true, .625, .375); surface.Capture();
            engine.DvdMenuInput(new(DvdMenuInputKind.Click, X: point.X, Y: point.Y));
            Assert.Null(engine.PollDvdMenu().Command); // Activated picture is not painted yet.
            surface.Capture(); request = engine.PollDvdMenu().Command!; Assert.NotNull(request);
        }
        Assert.Equal(7, request.Action.InputState!.General[0]); Assert.Equal(8, menu.Registers.General[0]);
        var clock = engine.DvdSubpictureTime90Khz; var frames = engine.DvdPresentation.Frames; await Task.Delay(150);
        Assert.Equal(clock, engine.DvdSubpictureTime90Khz); Assert.Equal(frames, engine.DvdPresentation.Frames);
        engine.SetDvdPaused(true); engine.CompleteDvdMenu(request.Ticket); Assert.True(engine.IsPaused);
        Assert.Throws<InvalidOperationException>(() => engine.CompleteDvdMenu(request.Ticket));
        engine.SetDvdPaused(false);
        await Until(() => { Assert.True(engine.ErrorCode >= 0, engine.DvdFrameFailure); return engine.DvdSubpictureTime90Khz >= 180_000; });
        engine.SetDvdPaused(true); await Task.Delay(100); surface.Capture();
        Assert.Null(engine.PollDvdMenu().Command); // Continuation must not re-run forced activation.
        engine.DvdMenuInput(new(DvdMenuInputKind.Number, Number: 2)); surface.Capture();
        engine.DvdMenuInput(new(DvdMenuInputKind.Activate)); surface.Capture();
        request = engine.PollDvdMenu().Command!; Assert.NotNull(request); Assert.Equal(20u, request.Read.Unit!.Navigation.Sector);
        Assert.Equal(10, menu.Registers.General[0]); engine.CompleteDvdMenu(request.Ticket); Assert.True(engine.IsPaused);
        if (!forced)
        {
            Assert.Throws<InvalidOperationException>(() => engine.HoldDvdPicture());
            surface.Capture(); engine.DvdMenuInput(new(DvdMenuInputKind.Number, Number: 1)); surface.Capture();
            engine.SetDvdPaused(false);
            await Until(() => { Assert.True(engine.ErrorCode >= 0, engine.DvdFrameFailure); surface.Capture(); return engine.DvdPresentation.Ended && engine.DvdPresentation.Painted; }, 30000);
            var finalPixels = surface.Capture(); var finalFrames = engine.DvdPresentation.Frames;
            engine.HoldDvdPicture(); Assert.True(menu.Held); Assert.True(timing.Held);
            Assert.Equal(finalPixels, surface.Capture()); Assert.Equal(1, menu.Highlight!.Button);
            Assert.Equal(reads[^1].Unit!.Navigation.End90Khz, engine.DvdSubpictureTime90Khz);
            Assert.Null(engine.PollDvdMenu().Command); engine.HoldDvdPicture();
            engine.SetDvdPaused(true); engine.SetDvdPaused(false); await Task.Delay(150);
            Assert.True(engine.IsPaused); Assert.Equal(finalFrames, engine.DvdPresentation.Frames);
            Assert.Equal(finalPixels, surface.Capture());
            window.Width = 500; window.Height = 300; window.UpdateLayout(); engine.SetRotation(90); surface.Capture();
            var heldPoint = PictureLocation(surface, engine, true, .625, .375);
            Assert.Equal(DvdMenuOutcome.SelectionChanged, engine.DvdMenuInput(new(DvdMenuInputKind.Point, X: heldPoint.X, Y: heldPoint.Y)).Outcome);
            Assert.True(White(PicturePixel(surface, engine, true, .625)));
            engine.DvdMenuInput(new(DvdMenuInputKind.Activate)); Assert.Null(engine.PollDvdMenu().Command);
            var active = PicturePixel(surface, engine, true, .625); Assert.True(active[2] > 240 && active[0] < 10);
            var heldRequest = engine.PollDvdMenu().Command!; Assert.NotNull(heldRequest);
            Assert.Equal(20u, heldRequest.Read.Unit!.Navigation.Sector); Assert.Equal(12, menu.Registers.General[0]);
            engine.CompleteDvdMenu(heldRequest.Ticket); await Task.Delay(150);
            Assert.True(engine.IsPaused); Assert.Equal(finalFrames, engine.DvdPresentation.Frames);
            Assert.Null(engine.PollDvdMenu().Command); Assert.True(engine.DvdPresentation.Ended);
            output.WriteLine("Actual native EOF retained the exact picture/menu/selection; held repaint, transformed pointer, activation and local acknowledgement did not restart playback.");
        }
        output.WriteLine($"Moving menu forced={forced}: actual selected/activated pixels, painted action gate, pending frame freeze, continued NAV command, transformed pointer and pause preservation passed.");
    });

    private static DvdStreamRead[] ControlMenuReads(uint end, bool forced)
    {
        var key = new DvdPgcKey(DvdPlaybackDomain.ManagerMenu, 0, 0, 1); var identity = Guid.NewGuid();
        var cell = new DvdCell(1, 10, 29, end, 1, 1, 0, 0, 0); var registers = new DvdVmState().WithGeneral(0, 7);
        DvdStreamRead Read(uint sector, uint begin, uint finish, bool continuation)
        {
            var bytes = DvdStreamFixture.Nav(sector, 9); DvdNavFixture.U32(bytes, DvdNavFixture.Pci + 12, begin); DvdNavFixture.U32(bytes, DvdNavFixture.Pci + 16, finish);
            DvdNavFixture.U32(bytes, DvdNavFixture.Hli + 2, 90_000); DvdNavFixture.U32(bytes, DvdNavFixture.Hli + 6, uint.MaxValue);
            DvdNavFixture.U32(bytes, DvdNavFixture.Hli + 10, uint.MaxValue);
            DvdNavFixture.U32(bytes, DvdNavFixture.Hli + 22, 0x1111ffff); DvdNavFixture.U32(bytes, DvdNavFixture.Hli + 26, 0x2222ffff);
            for (var i = 0; i < 2; i++)
            {
                var slot = DvdNavFixture.ButtonOffset(i); var x = (1 << 22) | ((120 + i * 240) << 12) | (299 + i * 240); var y = 120 << 12 | 239;
                bytes[slot] = (byte)(x >> 16); bytes[slot + 1] = (byte)(x >> 8); bytes[slot + 2] = (byte)x;
                bytes[slot + 3] = (byte)(y >> 16); bytes[slot + 4] = (byte)(y >> 8); bytes[slot + 5] = (byte)y;
            }
            if (continuation) DvdNavFixture.U16(bytes, DvdNavFixture.Hli, 3);
            else if (forced) bytes[DvdNavFixture.Hli + 21] = 1;
            DvdNavFixture.Command(bytes, 0, forced ? 0x7300000000010000UL : 0UL);
            DvdNavFixture.Command(bytes, 1, continuation ? 0x7300000000020000UL : 0x7300000000010000UL);
            var state = new DvdDomainState(identity, new(key, DvdNavigationPhase.Cell, 1, 1, sector), registers);
            return new(DvdStreamStatus.Ready, state, new DvdStreamUnit(DvdNavigationPacket.Read(bytes), bytes, cell, null));
        }
        return [Read(10, 0, 180_000, false), Read(20, 180_000, end, true)];
    }

    private static bool White(byte[] pixel) => pixel[0] > 240 && pixel[1] > 240 && pixel[2] > 240;
    private static (int X, int Y) PictureLocation(Surface surface, WindowsMediaEngine engine, bool rotated, double sourceX, double sourceY)
    {
        var aspect = engine.PresentationAspectRatio;
        var width = (int)Math.Round(Math.Min(surface.LastWidth, surface.LastHeight * aspect)); var height = (int)Math.Round(width / aspect);
        var x = (surface.LastWidth - width) / 2 + (int)(width * (rotated ? 1 - sourceY : sourceX));
        var y = (surface.LastHeight - height) / 2 + (int)(height * (rotated ? sourceX : sourceY));
        return (x, y);
    }
    private static byte[] PicturePixel(Surface surface, WindowsMediaEngine engine, bool rotated = false, double sourceX = .5)
    {
        var pixels = surface.Capture(); var (x, y) = PictureLocation(surface, engine, rotated, sourceX, .375);
        var offset = (y * surface.LastWidth + x) * 4; return pixels[offset..(offset + 4)];
    }

    private static int Colored(byte[] data)
    { var count = 0; for (var i = 0; i < data.Length; i += 4) if (data[i] + data[i + 1] + data[i + 2] > 60) count++; return count; }
    private static int Different(byte[] first, byte[] second)
    { var count = 0; Assert.Equal(first.Length, second.Length); for (var i = 0; i < first.Length; i += 4) if (Math.Abs(first[i] - second[i]) + Math.Abs(first[i + 1] - second[i + 1]) + Math.Abs(first[i + 2] - second[i + 2]) > 30) count++; return count; }
    private static int BlackEdgeRows(byte[] data, int width, int height)
    {
        var rows = 0;
        for (var y = 0; y < height; y++)
        { var i = (y * width + width / 2) * 4; if (data[i] + data[i + 1] + data[i + 2] < 10) rows++; else if (rows > 0) break; }
        return rows;
    }
    private static async Task Until(Func<bool> condition, int timeout = 10000)
    {
        var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException("Native DVD stream control timed out."); await Task.Delay(20); }
    }
    private static Task Run(Func<Window, Surface, Task> work)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var surface = new Surface();
            var window = new Window { Content = surface, Width = 640, Height = 360, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
            window.Loaded += async (_, _) =>
            {
                try { await work(window, surface); complete.TrySetResult(); }
                catch (Exception error) { complete.TrySetException(error); }
                finally { window.Close(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            };
            window.Show(); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return complete.Task;
    }
    private sealed class Surface : HwndHost
    {
        internal int LastWidth, LastHeight;
        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        { var handle = CreateWindowEx(0, "STATIC", "", 0x50000000, 0, 0, 10, 10, hwndParent.Handle, 0, 0, 0); return new(this, handle); }
        protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);
        protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
        { if (WindowsSoftwareVideoSurface.TryPaint(hwnd, msg, wParam)) { handled = true; return 0; } return base.WndProc(hwnd, msg, wParam, lParam, ref handled); }
        internal byte[] Capture()
        {
            GetClientRect(Handle, out var rect); LastWidth = rect.Right; LastHeight = rect.Bottom;
            var dc = GetDC(Handle); var memory = CreateCompatibleDC(dc); var bitmap = CreateCompatibleBitmap(dc, rect.Right, rect.Bottom);
            var previous = SelectObject(memory, bitmap);
            try
            {
                SendMessage(Handle, 0x0318, memory, 4); // Real HWND WM_PRINTCLIENT path.
                var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                var data = new byte[rect.Right * rect.Bottom * 4]; converted.CopyPixels(data, rect.Right * 4, 0); return data;
            }
            finally { SelectObject(memory, previous); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(Handle, dc); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
