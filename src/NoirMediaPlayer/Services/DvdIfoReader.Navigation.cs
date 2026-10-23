using System.Buffers.Binary;
using System.IO;

namespace NoirMediaPlayer.Services;

internal sealed partial class DvdIfoReader
{
    internal DvdNavigationCatalog ReadNavigationCatalog(string folder)
    {
        var manager = Load(Path.Combine(folder, "VIDEO_TS.IFO"), true, bytes =>
        {
            var source = new Binary(bytes);
            var info = ReadManager(bytes);
            var chains = new List<DvdNavigationChain>();
            var first = source.U32(0x84);
            if (first != 0)
            {
                // First-play PGC is inside the manager information region, before tables.
                var regionEnd = source.U32(0x80) + 1L;
                if (regionEnd <= first || regionEnd > bytes.Length) throw new InvalidDataException("Invalid first-play PGC region.");
                chains.Add(ReadNavigationChain(source.Slice(first, regionEnd - first), new(DvdPlaybackDomain.FirstPlay, 0, 0, 1), 0, 0, 0));
            }
            ReadMenuLanguages(source, 0xc8, DvdPlaybackDomain.ManagerMenu, 0, chains);
            return (Info: info, Sets: source.U16(0x3e), Chains: chains);
        });
        var allChains = new List<DvdNavigationChain>(manager.Value.Chains);
        var files = new List<string> { Path.GetFileName(manager.Path) };
        var titleSets = new Dictionary<int, TitleSet>();
        for (var setNumber = 1; setNumber <= manager.Value.Sets; setNumber++)
        {
            CheckBudget();
            var set = setNumber;
            var loaded = Load(Path.Combine(folder, $"VTS_{set:00}_0.IFO"), false, bytes =>
            {
                var source = new Binary(bytes);
                // Reuse chapter validation while parsing navigation PGCs independently:
                // command-only chains (including empty first-play/menu chains) are valid.
                var parts = ReadNavigationParts(source.Table(0xc8));
                var chains = ReadNavigationTable(source.Table(0xcc), DvdPlaybackDomain.Title, set, 0);
                ReadMenuLanguages(source, 0xd0, DvdPlaybackDomain.TitleSetMenu, set, chains);
                return (Parts: parts, Chains: chains);
            });
            files.Add(Path.GetFileName(loaded.Path)); allChains.AddRange(loaded.Value.Chains);
            titleSets.Add(set, new(loaded.Value.Parts, []));
            if (allChains.Count > 4096 || allChains.Sum(chain => chain.Cells.Count) > 65536 ||
                allChains.Sum(chain => chain.PreCommands.Count + chain.PostCommands.Count + chain.CellCommands.Count) > 16384)
                throw new InvalidDataException("DVD navigation topology exceeds its aggregate limit.");
        }
        var titles = new List<DvdNavigationTitle>();
        foreach (var title in manager.Value.Info.Titles)
        {
            var parts = titleSets[title.Set].Titles;
            if (title.Local > parts.Count || parts[title.Local - 1].Count != title.Chapters)
                throw new InvalidDataException("DVD navigation title/chapter inventories disagree.");
            var chapters = new List<DvdChapter>();
            foreach (var part in parts[title.Local - 1])
            {
                var chain = allChains.SingleOrDefault(candidate => candidate.Key == new DvdPgcKey(DvdPlaybackDomain.Title, title.Set, 0, part.ProgramChain));
                if (chain is null || part.Program < 1 || part.Program > chain.Programs.Count)
                    throw new InvalidDataException("DVD navigation chapter references a missing program chain/program.");
                chapters.Add(part with { FirstCell = chain.Programs[part.Program - 1] });
            }
            titles.Add(new(title.Number, title.Set, title.Local, title.Angles, title.PlaybackFlags, title.ParentalId, chapters.AsReadOnly()));
        }
        CheckBudget();
        return new(folder, files, allChains, titles);
    }

    private static IReadOnlyList<IReadOnlyList<DvdChapter>> ReadNavigationParts(Binary table)
    {
        var count = table.U16(0);
        if (count is < 1 or > 99) throw new InvalidDataException("Invalid navigation title inventory.");
        var offsets = Offsets(table, count, 8, 4, 8 + count * 4);
        var titles = new List<IReadOnlyList<DvdChapter>>();
        for (var i = 0; i < count; i++)
        {
            var end = i + 1 < count ? offsets[i + 1] : table.Length;
            var size = end - offsets[i];
            if (size < 4 || size % 4 != 0 || size / 4 > 999) throw new InvalidDataException("Invalid navigation chapter table.");
            var parts = new List<DvdChapter>();
            for (var p = offsets[i]; p < end; p += 4) parts.Add(new(parts.Count + 1, table.U16(p), table.U16(p + 2), 0));
            titles.Add(parts.AsReadOnly());
        }
        return titles.AsReadOnly();
    }

    private static void ReadMenuLanguages(Binary source, int pointer, DvdPlaybackDomain domain, int set, List<DvdNavigationChain> chains)
    {
        if (source.U32(pointer) == 0) return;
        var table = source.Table(pointer); var count = table.U16(0);
        if (count is < 1 or > 99 || table.U16(2) != 0) throw new InvalidDataException("Invalid DVD menu language inventory.");
        var offsets = Offsets(table, count, 12, 8, 8 + count * 8);
        var languages = new HashSet<ushort>();
        for (var i = 0; i < count; i++)
        {
            var language = table.U16(8 + i * 8);
            if (!languages.Add(language)) throw new InvalidDataException("Duplicate DVD menu language unit.");
            var end = i + 1 < count ? offsets[i + 1] : table.Length;
            var unit = table.Slice(offsets[i], end - offsets[i]);
            var declaredLength = (long)unit.U32(4) + 1;
            var parsed = ReadNavigationTable(unit.Slice(0, declaredLength), domain, set, language);
            if (table.Byte(10 + i * 8) != 0)
                parsed = parsed.Select(chain => chain with { CompatibilityReason = "Unsupported DVD menu language extension." }).ToList();
            chains.AddRange(parsed);
        }
    }

    private static List<DvdNavigationChain> ReadNavigationTable(Binary table, DvdPlaybackDomain domain, int set, ushort language)
    {
        var count = table.U16(0);
        if (count is < 1 or > 4096 || table.U16(2) != 0) throw new InvalidDataException("Invalid navigation program-chain count.");
        var offsets = Offsets(table, count, 12, 8, 8 + count * 8);
        var chains = new List<DvdNavigationChain>();
        for (var i = 0; i < count; i++)
        {
            var end = i + 1 < count ? offsets[i + 1] : table.Length;
            chains.Add(ReadNavigationChain(table.Slice(offsets[i], end - offsets[i]), new(domain, set, language, i + 1),
                table.Byte(8 + i * 8), table.Byte(9 + i * 8), table.U16(10 + i * 8)));
        }
        return chains;
    }

    private static DvdNavigationChain ReadNavigationChain(Binary data, DvdPgcKey key, byte entry, byte block, ushort parental)
    {
        data.Slice(0, 236);
        var programCount = data.Byte(2); var cellCount = data.Byte(3);
        if (programCount > cellCount || (programCount == 0) != (cellCount == 0)) throw new InvalidDataException("Invalid navigation program/cell counts.");
        var ranges = new List<(int Start, int Length)>();
        Binary Section(int field, int length)
        {
            if (length == 0)
            {
                if (data.U16(field) != 0) throw new InvalidDataException("Empty navigation section has an offset.");
                return data.Slice(0, 0);
            }
            var section = Relative(data, field, length); ranges.Add((data.U16(field), length)); return section;
        }
        var programs = Section(230, programCount); var playback = Section(232, cellCount * 24); var positions = Section(234, cellCount * 4);
        var firstCells = new List<int>(); var cells = new List<DvdCell>();
        for (var i = 0; i < programCount; i++)
        {
            var first = programs.Byte(i);
            if (first < 1 || first > cellCount || i == 0 && first != 1 || i > 0 && first <= firstCells[i - 1])
                throw new InvalidDataException("Invalid navigation program map.");
            firstCells.Add(first);
        }
        string? reason = block != 0 || parental != 0 || data.U16(0) != 0 ? "Parental/block/unknown PGC metadata requires compatibility." :
            data.Byte(162) != 0 ? "Random/shuffle program playback requires compatibility." : null;
        for (var i = 0; i < cellCount; i++)
        {
            var item = playback.Slice(i * 24, 24); var first = item.U32(8); var last = item.U32(20);
            var vob = positions.U16(i * 4); var cell = positions.Byte(i * 4 + 3);
            if (first > last || item.U32(16) < first || item.U32(16) > last || vob == 0 || cell == 0)
                throw new InvalidDataException("Invalid navigation cell extent/identity.");
            var duration = item.U32(4) == 0 ? 0 : ReadTime(item.Slice(4, 4).Bytes);
            cells.Add(new(i + 1, first, last, duration, vob, cell, item.Byte(0), item.Byte(2), item.Byte(3)));
            if ((item.Byte(0) & 0xf5) != 0 || (item.Byte(1) & 0x7f) != 0) reason ??= "Angle/interleaved/restricted cell navigation requires compatibility.";
        }
        var pre = new List<DvdButtonCommand>(); var post = new List<DvdButtonCommand>(); var cellCommands = new List<DvdButtonCommand>();
        var commandOffset = data.U16(228);
        if (commandOffset != 0)
        {
            if (commandOffset < 236) throw new InvalidDataException("Invalid navigation command offset.");
            var header = data.Slice(commandOffset, 8);
            var total = header.U16(0) + header.U16(2) + header.U16(4); var length = header.U16(6) + 1;
            if (total > 255 || length < 8 + total * 8) throw new InvalidDataException("Invalid navigation command table.");
            var commands = data.Slice(commandOffset, length); ranges.Add((commandOffset, length));
            var row = 8;
            for (var section = 0; section < 3; section++)
            {
                var target = section == 0 ? pre : section == 1 ? post : cellCommands;
                for (var n = 0; n < header.U16(section * 2); n++, row += 8)
                    target.Add(new(BinaryPrimitives.ReadUInt64BigEndian(commands.Slice(row, 8).Bytes)));
            }
        }
        if (cells.Any(cell => cell.CommandNumber > cellCommands.Count)) throw new InvalidDataException("Missing navigation cell command.");
        var sorted = ranges.OrderBy(range => range.Start).ToArray();
        if (sorted.Zip(sorted.Skip(1)).Any(pair => pair.First.Start + pair.First.Length > pair.Second.Start))
            throw new InvalidDataException("Navigation PGC sections overlap.");
        var palette = new uint[16];
        for (var index = 0; index < palette.Length; index++) palette[index] = data.U32(164 + index * 4);
        return new(key, entry, data.U16(156), data.U16(158), data.U16(160), data.U32(8), data.Byte(163),
            firstCells.AsReadOnly(), cells.AsReadOnly(), pre.AsReadOnly(), post.AsReadOnly(), cellCommands.AsReadOnly(), reason)
        { Palette = Array.AsReadOnly(palette) };
    }
}
