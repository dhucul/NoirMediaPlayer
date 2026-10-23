using System.IO;
using System.Text.Json;

namespace NoirMediaPlayer.Services;

// Only the cue at the captured playback time crosses the boundary, not a live
// subtitle track or engine. Explicit DTOs preserve internal styling properties.
internal sealed record SnapshotCaption(long Start, long End, string Text,
    SubtitleStyleRange[] Styles, SubtitleAlignment Alignment);
internal sealed record SnapshotMessage(int Version, string Source, long Time, string? ContentType,
    int Rotation, SnapshotCaption? Caption, string Destination)
{
    internal SourceSnapshotRequest ToRequest() => new(Source, Time, ContentType, Rotation: Rotation,
        EmbeddedCue: Caption is { } cue ? new SubtitleCue(cue.Start, cue.End, cue.Text, cue.Styles, cue.Alignment) : null);
}

internal static class SnapshotProtocol
{
    internal const int MaxMessageBytes = 256 * 1024;

    internal static byte[] Encode(SourceSnapshotRequest request, string destination)
    {
        var cue = request.Subtitles?.CueAt(request.Time) ?? request.EmbeddedCue;
        var message = new SnapshotMessage(1, request.Source, request.Time, request.ContentType, request.Rotation,
            cue is null ? null : new(cue.Start, cue.End, cue.Text, cue.Styles.ToArray(), cue.Alignment), destination);
        Validate(message);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length >= MaxMessageBytes) throw new InvalidDataException("Snapshot request is too large.");
        return [.. bytes, (byte)'\n'];
    }

    internal static SnapshotMessage Read(Stream input)
    {
        using var bytes = new MemoryStream();
        for (var i = 0; i < MaxMessageBytes; i++)
        {
            var value = input.ReadByte();
            if (value < 0) throw new EndOfStreamException("Incomplete snapshot request.");
            if (value == '\n')
            {
                var message = JsonSerializer.Deserialize<SnapshotMessage>(bytes.ToArray())
                    ?? throw new InvalidDataException("Missing snapshot request.");
                Validate(message);
                return message;
            }
            bytes.WriteByte((byte)value);
        }
        throw new InvalidDataException("Snapshot request is too large.");
    }

    private static void Validate(SnapshotMessage message)
    {
        if (message.Version != 1 || string.IsNullOrWhiteSpace(message.Source) || message.Source.Length > 32768 ||
            string.IsNullOrWhiteSpace(message.Destination) || !Path.IsPathFullyQualified(message.Destination) ||
            message.Time < 0 || message.Time > long.MaxValue / 10000 || message.Rotation is not (0 or 90 or 180 or 270) ||
            message.ContentType?.Length > 1024)
            throw new InvalidDataException("Invalid snapshot request.");
        if (message.Caption is not { } cue) return;
        if (cue.Text is null || cue.Text.Length > 8192 || cue.Styles is null || cue.Styles.Length > 8192 ||
            !Enum.IsDefined(cue.Alignment) || cue.Start < 0 || cue.End <= cue.Start ||
            cue.Styles.Any(style => style.Start < 0 || style.Length < 0 || style.Start > cue.Text.Length - style.Length ||
                (style.Style & ~(SubtitleStyle.Bold | SubtitleStyle.Italic | SubtitleStyle.Underline)) != 0 ||
                style.Language?.Length > 128))
            throw new InvalidDataException("Invalid snapshot caption.");
    }
}
