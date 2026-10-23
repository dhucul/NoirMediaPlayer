namespace NoirMediaPlayer.Services;

internal sealed record DvdCell(int Number, uint FirstSector, uint LastSector, long Duration90Khz,
    int VobId, int CellId, byte Flags, byte StillSeconds, byte CommandNumber);

internal sealed record DvdChapter(int Number, int ProgramChain, int Program, int FirstCell);

internal sealed record DvdChapterTarget(int Chapter, int Cell, uint Sector, long Time90Khz);

// A physical cell map, not a DVD virtual machine or a playable/decrypted stream.
// Only one sequential chain with no commands, holds, angles or restrictions is admitted.
internal sealed class DvdLinearTitleMap
{
    public IReadOnlyList<DvdCell> Cells { get; }
    public IReadOnlyList<DvdChapterTarget> Chapters { get; }
    public long Duration90Khz { get; }
    public long DurationMilliseconds => (Duration90Khz + 45) / 90;

    internal DvdLinearTitleMap(IReadOnlyList<DvdCell> cells, IReadOnlyList<DvdChapter> chapters)
    {
        if (cells.Count is < 1 or > 255 || chapters.Count is < 1 or > 999 ||
            cells.Where((cell, index) => cell.Number != index + 1 || cell.FirstSector > cell.LastSector ||
                cell.Duration90Khz <= 0 || cell.VobId <= 0 || cell.CellId <= 0 ||
                (cell.Flags & 0xf5) != 0 || cell.StillSeconds != 0 || cell.CommandNumber != 0).Any())
            throw new ArgumentException("A linear DVD map requires valid sequential cells without authored holds or commands.", nameof(cells));
        if (chapters[0].FirstCell != 1 || chapters.Where((chapter, index) => chapter.Number != index + 1 ||
            chapter.FirstCell < 1 || chapter.FirstCell > cells.Count || chapter.ProgramChain != chapters[0].ProgramChain ||
            index > 0 && chapter.FirstCell <= chapters[index - 1].FirstCell).Any())
            throw new ArgumentException("A linear DVD map requires ordered chapters in one complete program chain.", nameof(chapters));
        Cells = Array.AsReadOnly(cells.ToArray());
        var starts = new long[cells.Count];
        long total = 0;
        for (var index = 0; index < cells.Count; index++)
        {
            starts[index] = total;
            total = checked(total + cells[index].Duration90Khz);
        }
        Duration90Khz = total;
        Chapters = Array.AsReadOnly(chapters.Select(chapter => new DvdChapterTarget(chapter.Number,
            chapter.FirstCell, cells[chapter.FirstCell - 1].FirstSector, starts[chapter.FirstCell - 1])).ToArray());
    }

    internal DvdChapterTarget SelectChapter(int number) => number >= 1 && number <= Chapters.Count
        ? Chapters[number - 1] : throw new ArgumentOutOfRangeException(nameof(number));
}
