using System.IO;
using System.Text;

namespace NoirMediaPlayer.Services;

// Limits apply to inspected input, including comments and invalid entries.
internal static class PlaylistTextReader
{
    internal const int MaximumFileBytes = 8 * 1024 * 1024;
    internal const int MaximumLineCharacters = 32_768;
    internal const int MaximumLines = 100_000;

    internal static IEnumerable<string> ReadLines(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("The playlist exceeds its byte limit.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var lines = 0;
        var skipLineFeed = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = reader.Read(buffer, 0, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Position > MaximumFileBytes) throw new InvalidDataException("The playlist exceeds its byte limit.");
            if (count == 0) break;
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var character = buffer[index];
                if (skipLineFeed && character == '\n') { skipLineFeed = false; continue; }
                skipLineFeed = false;
                if (character is '\r' or '\n')
                {
                    if (++lines > MaximumLines) throw new InvalidDataException("The playlist exceeds its line-count limit.");
                    yield return line.ToString();
                    line.Clear();
                    skipLineFeed = character == '\r';
                }
                else
                {
                    if (line.Length == MaximumLineCharacters) throw new InvalidDataException("A playlist line exceeds its character limit.");
                    line.Append(character);
                }
            }
        }
        if (line.Length > 0)
        {
            if (++lines > MaximumLines) throw new InvalidDataException("The playlist exceeds its line-count limit.");
            yield return line.ToString();
        }
    }
}
