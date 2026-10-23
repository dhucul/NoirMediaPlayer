using System.IO;

namespace NoirMediaPlayer.Services;

internal enum DvdPlaybackDomain { FirstPlay, ManagerMenu, TitleSetMenu, Title }
internal sealed record DvdPgcKey(DvdPlaybackDomain Domain, int TitleSet, ushort Language, int Number);
internal sealed record DvdNavigationTitle(int Number, int TitleSet, int TitleInSet, int Angles,
    byte PlaybackFlags, ushort ParentalId, IReadOnlyList<DvdChapter> Chapters);
internal sealed record DvdNavigationChain(DvdPgcKey Key, byte EntryId, int Next, int Previous, int GoUp,
    uint ProhibitedOperations, byte StillSeconds, IReadOnlyList<int> Programs, IReadOnlyList<DvdCell> Cells,
    IReadOnlyList<DvdButtonCommand> PreCommands, IReadOnlyList<DvdButtonCommand> PostCommands,
    IReadOnlyList<DvdButtonCommand> CellCommands, string? CompatibilityReason)
{
    // Authored 00YYCrCb entries; no guessed subtitle or menu colours.
    public IReadOnlyList<uint> Palette { get; init; } = Array.Empty<uint>();
}

// Validated IFO topology, never a flattened movie or a playable/decrypted stream.
internal sealed class DvdNavigationCatalog
{
    public Guid Identity { get; } = Guid.NewGuid();
    public string VideoTsDirectory { get; }
    public IReadOnlyList<string> MetadataFiles { get; }
    public IReadOnlyList<DvdNavigationChain> Chains { get; }
    public IReadOnlyList<DvdNavigationTitle> Titles { get; }
    public bool PlaybackVerified => false;

    internal DvdNavigationCatalog(string folder, IEnumerable<string> files,
        IEnumerable<DvdNavigationChain> chains, IEnumerable<DvdNavigationTitle> titles)
    {
        VideoTsDirectory = folder; MetadataFiles = Array.AsReadOnly(files.ToArray());
        Chains = Array.AsReadOnly(chains.ToArray()); Titles = Array.AsReadOnly(titles.ToArray());
        if (Chains.Count > 4096 || Chains.Select(chain => chain.Key).Distinct().Count() != Chains.Count ||
            Chains.Sum(chain => chain.Cells.Count) > 65536 ||
            Chains.Sum(chain => chain.PreCommands.Count + chain.PostCommands.Count + chain.CellCommands.Count) > 16384)
            throw new InvalidDataException("DVD navigation topology exceeds its bounds or contains duplicate chains.");
    }

    internal static DvdNavigationCatalog Read(string source, CancellationToken cancellationToken = default) =>
        new DvdIfoReader(cancellationToken).ReadNavigationCatalog(DvdDiscInspection.ResolveDirectory(source));
}
