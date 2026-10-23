using System.Text.Json;
using System.Text.Json.Serialization;
using NoirMediaPlayer.Services;

if (args.Length is not (2 or 4) || args[0] is not ("--source" or "--nav-sector" or "--vm-commands" or "--dvd-navigation" or "--dvd-stream") ||
    (args.Length == 4 && args[2] != "--output"))
{
    Console.Error.WriteLine("Usage: NoirMediaPlayer.DiscInspector --source <disc root, VIDEO_TS/BDMV folder, VIDEO_TS.IFO or .mpls> [--output <new JSON file>]");
    Console.Error.WriteLine("       NoirMediaPlayer.DiscInspector --nav-sector <one clear 2048-byte NAV sector file> [--output <new JSON file>]");
    Console.Error.WriteLine("       NoirMediaPlayer.DiscInspector --vm-commands <binary table of at most 255 eight-byte commands> [--output <new JSON file>]");
    Console.Error.WriteLine("       NoirMediaPlayer.DiscInspector --dvd-navigation <local DVD root, VIDEO_TS folder or VIDEO_TS.IFO> [--output <new JSON file>]");
    Console.Error.WriteLine("       NoirMediaPlayer.DiscInspector --dvd-stream <local DVD root or VIDEO_TS folder> [--output <new JSON file>]");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    object report;
    string summary;
    bool hasErrors;
    if (args[0] == "--dvd-stream")
    {
        var catalog = DvdNavigationCatalog.Read(Path.GetFullPath(args[1]), cancellation.Token);
        var initial = new DvdDomainResolver(catalog).Start();
        var traversal = new DvdStreamTraversal(catalog);
        var state = initial.State;
        var units = new List<object>();
        var stop = initial.Status == DvdDomainStatus.Resolved ? "VobuLimit" : "CompatibilityRequired";
        var reason = initial.Reason;
        hasErrors = initial.Status != DvdDomainStatus.Resolved;
        // Explicit opt-in only: at most 16 VOBUs (32 MiB), and never enter the
        // next cell or auto-release a menu still/command boundary. No video is
        // saved in the report; hashes describe the unchanged stored bytes.
        while (!hasErrors && units.Count < 16)
        {
            if (state.Stopped) { stop = "Stopped"; break; }
            var read = traversal.Read(state, cancellation.Token);
            if (read.Status != DvdStreamStatus.Ready)
            {
                stop = "CompatibilityRequired"; reason = read.Reason; hasErrors = true; break;
            }
            var unit = read.Unit!;
            units.Add(new { Position = state.Position, unit.Navigation.Sector, unit.Navigation.VobId,
                unit.Navigation.CellId, unit.Navigation.Start90Khz, unit.Navigation.End90Khz, unit.ByteCount,
                SHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(unit.Bytes.Span)), unit.Boundary });
            if (read.Next is null) { stop = "CellBoundary"; break; }
            state = read.Next;
        }
        summary = $"Inspected {units.Count} DVD VOBUs from authored first-play: {stop}. No decoding or live playback.";
        report = new { PlaybackVerified = false, MainTitleSelected = false, Note = summary, Stop = stop,
            Reason = reason, VobuLimit = 16, CellCompletionExecuted = false, FirstPlay = initial, Units = units };
    }
    else if (args[0] == "--dvd-navigation")
    {
        var catalog = DvdNavigationCatalog.Read(Path.GetFullPath(args[1]), cancellation.Token);
        var initial = new DvdDomainResolver(catalog).Start();
        summary = $"Inspected {catalog.Chains.Count} DVD domain/PGC definitions. First-play proposal: {initial.Status}. No playback or implicit main-title selection.";
        report = new { PlaybackVerified = false, MainTitleSelected = false, Note = summary, Catalog = catalog, FirstPlay = initial };
        hasErrors = initial.Status != DvdDomainStatus.Resolved;
    }
    else if (args[0] == "--vm-commands")
    {
        var path = Path.GetFullPath(args[1]);
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("A local command-table file is required.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 255 * 8 || input.Length % 8 != 0)
            throw new InvalidDataException("A DVD command table must contain at most 255 complete eight-byte rows.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        var commands = new DvdButtonCommand[bytes.Length / 8];
        for (var i = 0; i < commands.Length; i++)
            commands[i] = new(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(i * 8, 8)));
        var execution = DvdVirtualMachine.Execute(commands, new DvdVmState(), cancellationToken: cancellation.Token);
        summary = $"DVD VM: {execution.Status}, {execution.InstructionsExecuted} instructions. Isolated state only; no playback or domain/stream resolution.";
        report = new { PlaybackVerified = false, SupportedVmSubsetImplemented = true, FullDvdVmImplemented = false, Note = summary, Execution = execution };
        hasErrors = execution.Status is not (DvdVmStatus.Completed or DvdVmStatus.Break);
    }
    else if (args[0] == "--nav-sector")
    {
        var path = Path.GetFullPath(args[1]);
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("A local navigation-sector file is required.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != 2048) throw new InvalidDataException("The NAV input must contain exactly one 2048-byte sector.");
        var bytes = new byte[2048]; input.ReadExactly(bytes);
        var packet = DvdNavigationPacket.Read(bytes);
        summary = "Inspected one clear DVD NAV sector. VM execution and disc playback are not verified.";
        report = new { PlaybackVerified = false, VirtualMachineImplemented = false, Note = summary, Packet = packet };
        hasErrors = packet.CompatibilityReason is not null;
    }
    else if (DvdDiscInspection.IsDvdSource(args[1]))
    {
        var dvd = DvdDiscInspection.Read(Path.GetFullPath(args[1]), cancellation.Token);
        report = dvd;
        summary = $"Inspected {dvd.Titles.Count} DVD titles. {dvd.Note}";
        hasErrors = dvd.Titles.Any(title => title.Error is not null);
    }
    else
    {
        var bluRay = BluRayDiscInspection.Read(args[1], cancellation.Token);
        report = bluRay;
        summary = $"Inspected {bluRay.Playlists.Count} Blu-ray playlists. {bluRay.Note}";
        hasErrors = bluRay.Playlists.Any(playlist => playlist.Error is not null);
    }
    var options = new JsonSerializerOptions { WriteIndented = true };
    options.Converters.Add(new JsonStringEnumConverter());
    if (args.Length == 4)
    {
        // Do not overwrite a source playlist, a previous report or any other existing file.
        using var output = new FileStream(Path.GetFullPath(args[3]), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, report, options);
        Console.WriteLine(summary);
        Console.WriteLine($"Report: {Path.GetFullPath(args[3])}");
    }
    else Console.WriteLine(JsonSerializer.Serialize(report, options));
    return hasErrors ? 1 : 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Disc inspection cancelled.");
    return 3;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException)
{
    Console.Error.WriteLine($"Disc inspection failed: {exception.Message}");
    return 1;
}
