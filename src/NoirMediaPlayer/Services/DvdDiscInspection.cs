using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;

namespace NoirMediaPlayer.Services;

internal sealed record DvdTitleInspection(int Number, int TitleSet, int TitleInSet, int Angles, int DeclaredChapters,
    string? MetadataFile, IReadOnlyList<DvdChapter> Chapters, DvdLinearTitleMap? LinearMap,
    string? CompatibilityReason, string? Error);

internal sealed record DvdDiscInspection(string VideoTsDirectory, string ManagerFile, bool HasFirstPlay,
    bool HasManagerMenus, IReadOnlyList<DvdTitleInspection> Titles)
{
    public bool PlaybackVerified => false;
    public bool MainTitleSelected => false;
    public string Note => "First-party DVD structure inspection only. Chapter sectors are relative to the title-set VOB files. " +
        "Linear maps do not execute DVD commands, select a movie, decode media or handle protection. DVD playback remains on its existing backend.";

    internal static bool IsDvdSource(string source)
    {
        var path = Path.GetFullPath(source);
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Use an explicit local disc or metadata folder.", nameof(source));
        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals("VIDEO_TS.IFO", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(Path.Combine(path, "BDMV")) && Directory.Exists(Path.Combine(path, "VIDEO_TS"));
    }

    internal static DvdDiscInspection Read(string source, CancellationToken cancellationToken = default)
    {
        var folder = ResolveDirectory(source);
        var reader = new DvdIfoReader(cancellationToken);
        var manager = reader.Load(Path.Combine(folder, "VIDEO_TS.IFO"), true, DvdIfoReader.ReadManager);
        var sets = new Dictionary<int, (string? File, DvdIfoReader.TitleSet? Value, string? Error)>();
        var titles = new List<DvdTitleInspection>();
        foreach (var title in manager.Value.Titles)
        {
            reader.CheckBudget();
            if (!sets.TryGetValue(title.Set, out var set))
            {
                try
                {
                    var loaded = reader.Load(Path.Combine(folder, $"VTS_{title.Set:00}_0.IFO"), false, DvdIfoReader.ReadTitleSet);
                    set = (Path.GetFileName(loaded.Path), loaded.Value, null);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    set = (null, null, exception.Message);
                }
                sets.Add(title.Set, set);
            }
            if (set.Value is null)
            {
                titles.Add(new(title.Number, title.Set, title.Local, title.Angles, title.Chapters, set.File, [], null, null, set.Error));
                continue;
            }
            try { titles.Add(DvdIfoReader.InspectTitle(title, set.File!, set.Value)); }
            catch (InvalidDataException exception)
            {
                titles.Add(new(title.Number, title.Set, title.Local, title.Angles, title.Chapters, set.File, [], null, null, exception.Message));
            }
        }
        return new(folder, Path.GetFileName(manager.Path), manager.Value.HasFirstPlay, manager.Value.HasMenus, titles.AsReadOnly());
    }

    internal static string ResolveDirectory(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source) || source.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Choose a local DVD root, VIDEO_TS folder or VIDEO_TS.IFO file.", nameof(source));
        var path = Path.GetFullPath(source);
        if (Path.GetFileName(path).Equals("VIDEO_TS.IFO", StringComparison.OrdinalIgnoreCase)) path = Path.GetDirectoryName(path)!;
        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase)
            ? path : Path.Combine(path, "VIDEO_TS");
    }
}

// Own bounded big-endian parser. The DVD field layouts are documented in the
// VideoLAN references listed in README.md; no library code is linked here.
internal sealed partial class DvdIfoReader(CancellationToken cancellationToken)
{
    internal sealed record Title(int Number, int Set, int Local, int Angles, int Chapters, byte PlaybackFlags, ushort ParentalId);
    internal sealed record Manager(bool HasFirstPlay, bool HasMenus, IReadOnlyList<Title> Titles);
    internal sealed record Chain(long Duration, uint Prohibited, bool HasCommands, bool HasLinksOrHolds,
        IReadOnlyList<int> Programs, IReadOnlyList<DvdCell> Cells);
    internal sealed record TitleSet(IReadOnlyList<IReadOnlyList<DvdChapter>> Titles, IReadOnlyList<Chain> Chains);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _bytes;

    internal void CheckBudget()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_bytes > 32 * 1024 * 1024) throw new NotSupportedException("DVD metadata exceeds the 32 MiB inspection limit.");
        if (_clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("DVD metadata inspection exceeded its five-second deadline.");
    }

    internal (string Path, T Value) Load<T>(string path, bool manager, Func<byte[], T> parse)
    {
        Exception? primaryError = null;
        foreach (var candidate in new[] { path, Path.ChangeExtension(path, ".BUP") })
        {
            CheckBudget();
            try
            {
                var info = new FileInfo(candidate);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked DVD metadata files are not inspected.");
                using var file = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                if (file.Length < 2048 || file.Length > 4 * 1024 * 1024 || (_bytes += file.Length) > 32 * 1024 * 1024)
                    throw new InvalidDataException("DVD metadata exceeds its size limit.");
                var data = new byte[(int)file.Length];
                for (var offset = 0; offset < data.Length; offset += 65536)
                {
                    CheckBudget();
                    file.ReadExactly(data.AsSpan(offset, Math.Min(65536, data.Length - offset)));
                }
                CheckBudget();
                if (!data.AsSpan(0, 12).SequenceEqual(manager ? "DVDVIDEO-VMG"u8 : "DVDVIDEO-VTS"u8))
                    throw new InvalidDataException("Invalid DVD information-file signature.");
                var value = parse(data);
                CheckBudget();
                return (candidate, value);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                primaryError ??= exception;
            }
        }
        throw new InvalidDataException($"Neither {Path.GetFileName(path)} nor its backup is usable: {primaryError!.Message}", primaryError);
    }

    internal static Manager ReadManager(byte[] data)
    {
        var source = new Binary(data);
        var setCount = source.U16(0x3e);
        if (setCount is 0 or > 99) throw new InvalidDataException("Invalid DVD title-set count.");
        var table = source.Table(0xc4);
        var count = table.U16(0);
        if (count is 0 or > 99 || 8 + count * 12 > table.Length) throw new InvalidDataException("Invalid DVD title inventory.");
        var result = new List<Title>();
        for (var index = 0; index < count; index++)
        {
            var offset = 8 + index * 12;
            var set = table.Byte(offset + 6); var local = table.Byte(offset + 7);
            var angles = table.Byte(offset + 1); var chapters = table.U16(offset + 2);
            if (set < 1 || set > setCount || local is 0 or > 99 || angles is 0 or > 9 || chapters is 0 or > 999)
                throw new InvalidDataException("Invalid DVD title reference.");
            result.Add(new(index + 1, set, local, angles, chapters, table.Byte(offset), table.U16(offset + 4)));
        }
        var firstPlay = source.U32(0x84);
        if (firstPlay != 0) source.Slice(firstPlay, 236);
        var menus = source.U32(0xc8);
        if (menus != 0) source.Slice(checked((long)menus * 2048), 8);
        return new(firstPlay != 0, menus != 0, result.AsReadOnly());
    }

    internal static TitleSet ReadTitleSet(byte[] data)
    {
        var source = new Binary(data);
        var parts = source.Table(0xc8);
        var titleCount = parts.U16(0);
        if (titleCount is 0 or > 99 || 8 + titleCount * 4 > parts.Length) throw new InvalidDataException("Invalid DVD part-of-title table.");
        var titleOffsets = Offsets(parts, titleCount, 8, 4, 8 + titleCount * 4);
        var titles = new List<IReadOnlyList<DvdChapter>>();
        for (var index = 0; index < titleCount; index++)
        {
            var start = titleOffsets[index];
            var end = index + 1 < titleCount ? titleOffsets[index + 1] : parts.Length;
            var length = end - start;
            if (length < 4 || length % 4 != 0 || length / 4 > 999) throw new InvalidDataException("Invalid DVD chapter references.");
            var chapters = new List<DvdChapter>();
            for (var offset = start; offset < end; offset += 4)
                chapters.Add(new(chapters.Count + 1, parts.U16(offset), parts.U16(offset + 2), 0));
            titles.Add(chapters.AsReadOnly());
        }
        var chainTable = source.Table(0xcc);
        var chainCount = chainTable.U16(0);
        if (chainCount is 0 or > 4096 || 8 + chainCount * 8 > chainTable.Length) throw new InvalidDataException("Invalid DVD program-chain inventory.");
        var chainOffsets = Offsets(chainTable, chainCount, 12, 8, 8 + chainCount * 8);
        var chains = new List<Chain>();
        for (var index = 0; index < chainCount; index++)
        {
            var end = index + 1 < chainCount ? chainOffsets[index + 1] : chainTable.Length;
            chains.Add(ReadChain(chainTable.Slice(chainOffsets[index], end - chainOffsets[index])));
        }
        return new(titles.AsReadOnly(), chains.AsReadOnly());
    }

    private static int[] Offsets(Binary table, int count, int first, int stride, int minimum)
    {
        var result = new int[count];
        for (var index = 0; index < count; index++)
        {
            var value = table.U32(first + index * stride);
            if (value < minimum || value >= table.Length || index > 0 && value <= result[index - 1])
                throw new InvalidDataException("DVD table offsets are outside their bounds or unordered.");
            result[index] = checked((int)value);
        }
        return result;
    }

    private static Chain ReadChain(Binary data)
    {
        data.Slice(0, 236);
        var programCount = data.Byte(2); var cellCount = data.Byte(3);
        if (programCount == 0 || cellCount == 0 || programCount > cellCount) throw new InvalidDataException("Invalid DVD program/cell inventory.");
        var programs = Relative(data, 230, programCount);
        var playback = Relative(data, 232, cellCount * 24);
        var positions = Relative(data, 234, cellCount * 4);
        var ranges = new List<(int Start, int Length)>
        {
            (data.U16(230), programCount), (data.U16(232), cellCount * 24), (data.U16(234), cellCount * 4)
        };
        var firstCells = new List<int>();
        for (var index = 0; index < programCount; index++)
        {
            var first = programs.Byte(index);
            if (first == 0 || first > cellCount || index > 0 && first <= firstCells[index - 1])
                throw new InvalidDataException("Invalid DVD program-to-cell map.");
            firstCells.Add(first);
        }
        if (firstCells[0] != 1) throw new InvalidDataException("The DVD program map omits its first cell.");
        var commands = data.U16(228);
        var hasCommands = false;
        if (commands != 0)
        {
            if (commands < 236) throw new InvalidDataException("Invalid DVD command-table offset.");
            var header = data.Slice(commands, 8);
            var count = header.U16(0) + header.U16(2) + header.U16(4);
            if (count > 255 || header.U16(6) + 1 < 8 + count * 8) throw new InvalidDataException("Invalid DVD command-table length.");
            data.Slice(commands, header.U16(6) + 1);
            ranges.Add((commands, header.U16(6) + 1));
            hasCommands = count != 0;
        }
        var orderedRanges = ranges.OrderBy(range => range.Start).ToArray();
        if (orderedRanges.Zip(orderedRanges.Skip(1)).Any(pair => pair.First.Start + pair.First.Length > pair.Second.Start))
            throw new InvalidDataException("DVD program-chain data sections overlap.");
        var cells = new List<DvdCell>();
        for (var index = 0; index < cellCount; index++)
        {
            var entry = playback.Slice(index * 24, 24);
            var first = entry.U32(8); var last = entry.U32(20);
            if (first > last || entry.U32(16) < first || entry.U32(16) > last)
                throw new InvalidDataException("Invalid DVD cell sector range.");
            var vob = positions.U16(index * 4); var cell = positions.Byte(index * 4 + 3);
            if (vob == 0 || cell == 0) throw new InvalidDataException("Invalid DVD VOB/cell identity.");
            cells.Add(new(index + 1, first, last, ReadTime(entry.Slice(4, 4)), vob, cell,
                entry.Byte(0), entry.Byte(2), entry.Byte(3)));
            hasCommands |= entry.Byte(3) != 0;
            hasCommands |= (entry.Byte(1) & 0x7f) != 0;
        }
        return new(ReadTime(data.Slice(4, 4)), data.U32(8), hasCommands,
            data.U16(156) != 0 || data.Byte(162) != 0 || data.Byte(163) != 0, firstCells.AsReadOnly(), cells.AsReadOnly());
    }

    private static Binary Relative(Binary source, int field, int length)
    {
        var offset = source.U16(field);
        if (offset < 236) throw new InvalidDataException("Invalid DVD program-chain data offset.");
        return source.Slice(offset, length);
    }

    internal static long ReadTime(ReadOnlySpan<byte> data)
    {
        if (data.Length != 4) throw new InvalidDataException("Incomplete DVD time code.");
        static int Bcd(byte value)
        {
            if ((value & 15) > 9 || (value >> 4) > 9) throw new InvalidDataException("Invalid DVD BCD time.");
            return (value >> 4) * 10 + (value & 15);
        }
        var hours = Bcd(data[0]); var minutes = Bcd(data[1]); var seconds = Bcd(data[2]);
        var frames = Bcd((byte)(data[3] & 0x3f));
        var rate = data[3] >> 6;
        if (minutes > 59 || seconds > 59 || rate is not (1 or 3) || frames >= (rate == 1 ? 25 : 30))
            throw new InvalidDataException("Unsupported DVD frame-rate/time code.");
        return ((hours * 60L + minutes) * 60 + seconds) * 90_000 + frames * (rate == 1 ? 3600 : 3003);
    }

    private static long ReadTime(Binary data) => ReadTime(data.Bytes);

    internal static DvdTitleInspection InspectTitle(Title title, string file, TitleSet set)
    {
        if (title.Local > set.Titles.Count) throw new InvalidDataException("The DVD title references a missing title-set entry.");
        var parts = set.Titles[title.Local - 1];
        if (parts.Count != title.Chapters) throw new InvalidDataException("DVD manager and title-set chapter counts disagree.");
        var chapters = new List<DvdChapter>();
        foreach (var part in parts)
        {
            if (part.ProgramChain < 1 || part.ProgramChain > set.Chains.Count) throw new InvalidDataException("The DVD chapter references a missing program chain.");
            var chain = set.Chains[part.ProgramChain - 1];
            if (part.Program < 1 || part.Program > chain.Programs.Count) throw new InvalidDataException("The DVD chapter references a missing program.");
            chapters.Add(part with { FirstCell = chain.Programs[part.Program - 1] });
        }
        var primary = set.Chains[chapters[0].ProgramChain - 1];
        string? reason = null;
        if (title.Angles != 1 || title.PlaybackFlags != 0 || title.ParentalId != 0)
            reason = "The title requires angle, command, random/multiple-chain or parental navigation.";
        else if (chapters.Any(part => part.ProgramChain != chapters[0].ProgramChain) || chapters[0].FirstCell != 1 ||
            chapters.Zip(chapters.Skip(1)).Any(pair => pair.First.FirstCell >= pair.Second.FirstCell))
            reason = "The chapters do not describe one sequential program chain.";
        else if (primary.Prohibited != 0 || primary.HasCommands || primary.HasLinksOrHolds ||
            primary.Cells.Any(cell => (cell.Flags & 0xf5) != 0 || cell.StillSeconds != 0 || cell.Duration90Khz <= 0))
            reason = "The program chain needs authored restrictions, VM commands, holds, angles or interleaved navigation.";
        else if (Math.Abs(primary.Duration - primary.Cells.Sum(cell => cell.Duration90Khz)) > 9000)
            reason = "The program-chain duration disagrees with its sequential cell durations.";
        var map = reason is null ? new DvdLinearTitleMap(primary.Cells, chapters) : null;
        return new(title.Number, title.Set, title.Local, title.Angles, title.Chapters, file, chapters.AsReadOnly(), map, reason, null);
    }

    private readonly struct Binary(byte[] data, int start, int length)
    {
        internal Binary(byte[] data) : this(data, 0, data.Length) { }
        internal int Length => length;
        internal ReadOnlySpan<byte> Bytes => data.AsSpan(start, length);
        internal Binary Slice(long offset, long size)
        {
            if (offset < 0 || size < 0 || offset > length || size > length - offset)
                throw new InvalidDataException("DVD metadata extends beyond its containing table.");
            return new(data, checked(start + (int)offset), checked((int)size));
        }
        internal byte Byte(int offset) => Slice(offset, 1).Bytes[0];
        internal ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(Slice(offset, 2).Bytes);
        internal uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(Slice(offset, 4).Bytes);
        internal Binary Table(int pointer)
        {
            var offset = checked((long)U32(pointer) * 2048);
            if (offset < 2048) throw new InvalidDataException("Missing DVD metadata table.");
            return Slice(offset, checked((long)Slice(offset, 8).U32(4) + 1));
        }
    }
}
