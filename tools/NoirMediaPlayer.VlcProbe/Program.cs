using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Tests;

// Runs in its own process so each runtime gets an independent DLL loader and
// plugin inventory. It never selects a runtime for the NOIR application.
if (args is ["--generate", var fixtureDirectory])
{
    var directory = Path.GetFullPath(fixtureDirectory);
    Directory.CreateDirectory(directory);
    Mp4Fixture.Write(Path.Combine(directory, "h264-aac.mp4"));
    Mp4Fixture.WriteAudio(Path.Combine(directory, "aac.m4a"));
    MpegFixture.Write(Path.Combine(directory, "h264-program.mpg"));
    foreach (var extension in new[] { "ts", "m2ts" })
        MpegFixture.Write(Path.Combine(directory, "h264-transport." + extension));
    MatroskaFixture.Write(Path.Combine(directory, "mjpeg-pcm.mkv"), video: true, subtitles: true);
    AsfFixture.Write(Path.Combine(directory, "wmv3-pcm.wmv"), video: true);
    Console.WriteLine($"Synthetic Windows-encoded fixtures generated: {directory}");
    return 0;
}
if (args is ["--check-modules", var moduleRuntime, var moduleReport])
{
    var selectedRuntime = Path.GetFullPath(moduleRuntime);
    Core.Initialize(selectedRuntime);
    using var engine = new LibVLC("--quiet");
    var modules = new List<object>();
    var failures = 0;
    foreach (var path in Directory.EnumerateFiles(Path.Combine(selectedRuntime, "plugins"), "*_plugin.dll", SearchOption.AllDirectories))
    {
        string? error = null;
        nint library = 0;
        try
        {
            library = NativeLibrary.Load(path);
            if (!NativeLibrary.TryGetExport(library, "vlc_entry__3_0_0f", out _))
                throw new InvalidOperationException("VLC 3 module entry point is missing.");
        }
        catch (Exception moduleError) { error = moduleError.Message; failures++; }
        finally { if (library != 0) NativeLibrary.Free(library); }
        modules.Add(new { Module = Path.GetRelativePath(selectedRuntime, path), Passed = error is null, Error = error });
    }
    var destination = Path.GetFullPath(moduleReport);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllText(destination, JsonSerializer.Serialize(new
    {
        Runtime = selectedRuntime, Passed = failures == 0 && modules.Count > 0,
        Total = modules.Count, Failed = failures, Modules = modules, CheckedUtc = DateTime.UtcNow
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Module loading: {modules.Count - failures}/{modules.Count} passed; {destination}");
    return failures == 0 && modules.Count > 0 ? 0 : 1;
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException("Arguments must be --name value pairs.");
    options.Add(args[index], args[index + 1]);
}
var runtime = Path.GetFullPath(options["--runtime"]);
var source = options["--media"];
var report = Path.GetFullPath(options["--report"]);
var expectVideo = options.GetValueOrDefault("--video", "true") == "true";
var expectAudio = options.GetValueOrDefault("--audio", "true") == "true";
var controls = options.GetValueOrDefault("--controls", "true") == "true";
var timeout = int.Parse(options.GetValueOrDefault("--timeout", "30"));
if (timeout is < 5 or > 120) throw new ArgumentOutOfRangeException(nameof(timeout));
var duration = int.Parse(options.GetValueOrDefault("--duration", "0"));
if (duration is < 0 or > 120) throw new ArgumentOutOfRangeException(nameof(duration));
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
var messages = new ConcurrentQueue<string>();
var checks = new List<string>();
var frames = 0L;
var audioSamples = 0L;
var firstRgb = 0;
var lastRgb = 0;
var firstExpected = options.TryGetValue("--first-rgb", out var firstText) ? Convert.ToInt32(firstText, 16) : (int?)null;
var seekExpected = options.TryGetValue("--seek-rgb", out var seekText) ? Convert.ToInt32(seekText, 16) : (int?)null;
var pixels = Marshal.AllocHGlobal(320 * 240 * 4);
var failure = (string?)null;
object? inventory = null;
string? engineVersion = null, changeset = null;
try
{
    Core.Initialize(runtime);
    if (source.StartsWith("bluray:", StringComparison.OrdinalIgnoreCase))
    {
        // Match NOIR's normal AACS configuration without copying or modifying
        // the user's key database. Keep this handle alive until process exit.
        var aacsLibrary = Path.Combine(runtime, "libaacs.dll");
        NativeLibrary.Load(aacsLibrary);
        Environment.SetEnvironmentVariable("LIBAACS_PATH", aacsLibrary);
        var database = options.GetValueOrDefault("--keydb") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aacs", "KEYDB.cfg");
        Environment.SetEnvironmentVariable("AACS_CONFIG_DIR", Path.GetDirectoryName(Path.GetFullPath(database)));
    }
    using var engine = new LibVLC("--no-video-title-show", "--no-osd", "--avcodec-hw=none", "--verbose=1");
    engineVersion = engine.Version;
    changeset = engine.Changeset;
    var loadedEngine = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(module =>
        module.ModuleName.Equals("libvlc.dll", StringComparison.OrdinalIgnoreCase)).FileName;
    if (!Path.GetFullPath(loadedEngine).Equals(Path.Combine(runtime, "libvlc.dll"), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The selected runtime was not the engine loaded by the process.");
    engine.Log += (sender, entry) =>
    {
        messages.Enqueue($"{entry.Level}: {entry.Module}: {entry.Message}");
        while (messages.Count > 300) messages.TryDequeue(out _);
    };
    using var player = new MediaPlayer(engine);
    player.SetVideoFormat("RV32", 320, 240, 320 * 4);
    player.SetVideoCallbacks((_, planes) =>
    {
        Marshal.WriteIntPtr(planes, pixels);
        return IntPtr.Zero;
    }, null, (_, _) =>
    {
        var rgb = Marshal.ReadInt32(pixels, (120 * 320 + 160) * 4) & 0xffffff;
        Interlocked.CompareExchange(ref firstRgb, rgb, 0);
        Interlocked.Exchange(ref lastRgb, rgb);
        Interlocked.Increment(ref frames);
    });
    player.SetAudioFormat("S16N", 48_000, 2);
    player.SetAudioCallbacks((_, _, count, _) => Interlocked.Add(ref audioSamples, count), null, null, null, null);
    using var media = new Media(engine, source, source.Contains("://", StringComparison.Ordinal) ? FromType.FromLocation : FromType.FromPath);
    if (options.TryGetValue("--keydb", out var keydb)) media.AddOption(":bluray-keyfile=" + Path.GetFullPath(keydb));
    if (!player.Play(media)) throw new InvalidOperationException("libVLC refused to start playback.");
    WaitUntil(() => (!expectVideo || Interlocked.Read(ref frames) >= 5) &&
        (!expectAudio || Interlocked.Read(ref audioSamples) >= 2_400), "decoded output", timeout * 1000);
    if (firstExpected is int firstColor && !ColorMatches(Volatile.Read(ref firstRgb), firstColor))
        throw new InvalidOperationException($"Wrong decoded RGB: {firstRgb:X6}; expected {firstColor:X6}.");
    if (expectVideo) checks.Add("video decoded to memory");
    if (expectAudio) checks.Add("PCM audio delivered");
    inventory = media.Tracks.Select(track => new
    {
        track.Id, Type = track.TrackType.ToString(),
        Codec = Encoding.ASCII.GetString(BitConverter.GetBytes(track.Codec)), track.Language
    }).ToArray();
    if (controls)
    {
        player.SetPause(true);
        WaitUntil(() => player.State == VLCState.Paused, "pause", 5_000);
        var pausedTime = player.Time;
        Thread.Sleep(300);
        if (Math.Abs(player.Time - pausedTime) > 250) throw new InvalidOperationException("Clock advanced while paused.");
        checks.Add("pause held playback clock");
        if (!player.IsSeekable || player.Length < 8_000) throw new InvalidOperationException("Fixture did not expose a seekable timeline.");
        var target = player.Length / 2;
        player.Time = target;
        player.SetPause(false);
        WaitUntil(() => Math.Abs(player.Time - target) < 1_500, "seek", 6_000);
        var oldFrames = Interlocked.Read(ref frames);
        var oldSamples = Interlocked.Read(ref audioSamples);
        WaitUntil(() => (!expectVideo || Interlocked.Read(ref frames) > oldFrames + 2) &&
            (!expectAudio || Interlocked.Read(ref audioSamples) > oldSamples + 2_400) &&
            (seekExpected is not int seekColor || ColorMatches(Volatile.Read(ref lastRgb), seekColor)), "output after seek", 6_000);
        checks.Add("seek and resume decoded output");
        if (player.SetRate(1.25f) != 0) throw new InvalidOperationException("Playback rate change was rejected.");
        checks.Add("playback rate accepted");
    }
    if (duration > 0)
    {
        var oldFrames = Interlocked.Read(ref frames);
        var oldSamples = Interlocked.Read(ref audioSamples);
        var elapsed = Stopwatch.StartNew();
        var videoProgress = TimeSpan.Zero;
        var audioProgress = TimeSpan.Zero;
        WaitUntil(() =>
        {
            var currentFrames = Interlocked.Read(ref frames);
            var currentSamples = Interlocked.Read(ref audioSamples);
            if (currentFrames != oldFrames) { oldFrames = currentFrames; videoProgress = elapsed.Elapsed; }
            if (currentSamples != oldSamples) { oldSamples = currentSamples; audioProgress = elapsed.Elapsed; }
            if ((expectVideo && elapsed.Elapsed - videoProgress > TimeSpan.FromSeconds(5)) ||
                (expectAudio && elapsed.Elapsed - audioProgress > TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("Decoded output stalled during continued playback.");
            return elapsed.Elapsed.TotalSeconds >= duration;
        }, "continued playback", (duration + 5) * 1000);
        checks.Add($"continued decoded output for {duration} seconds");
    }
    player.Stop();

    void WaitUntil(Func<bool> condition, string operation, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (player.State is VLCState.Error or VLCState.Ended)
                throw new InvalidOperationException($"Playback {player.State} before {operation}.");
            if (watch.ElapsedMilliseconds > milliseconds) throw new TimeoutException($"Timed out waiting for {operation}.");
            Thread.Sleep(25);
        }
    }
}
catch (Exception error) { failure = error.ToString(); }
finally { Marshal.FreeHGlobal(pixels); }
var result = new
{
    Schema = 1, Runtime = runtime, Source = source, EngineVersion = engineVersion, Changeset = changeset,
    Passed = failure is null, Checks = checks, VideoFrames = frames, AudioSamples = audioSamples,
    FirstRgb = firstRgb.ToString("X6"), LastRgb = lastRgb.ToString("X6"),
    Tracks = inventory, Error = failure, Logs = messages.ToArray(), CheckedUtc = DateTime.UtcNow
};
File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{(failure is null ? "PASS" : "FAIL")}: {source}; frames={frames}; audio samples={audioSamples}; {report}");
if (failure is not null) Console.Error.WriteLine(failure);
return failure is null ? 0 : 1;

static bool ColorMatches(int actual, int expected) =>
    new[] { 0, 8, 16 }.All(shift => Math.Abs(((actual >> shift) & 255) - ((expected >> shift) & 255)) <= 8);
