using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NoirMediaPlayer.Services;

public enum AacsRuntimeState
{
    NotConfigured,
    Missing,
    Invalid,
    LoadFailed,
    Ready
}

public sealed record AacsRuntimeStatus(
    AacsRuntimeState State,
    string Message,
    string LibraryPath = "",
    bool KeyDatabaseFound = false)
{
    public bool IsReady => State == AacsRuntimeState.Ready;
}

public sealed record AacsKeyDownloadResult(
    bool Success,
    string Message);

public static class AacsService
{
    private const string LibraryFileName = "libaacs.dll";
    private const long MaximumDownloadBytes = 128 * 1024 * 1024;
    private const long MaximumKeyDatabaseBytes = 256 * 1024 * 1024;
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    private enum KeyDatabaseState
    {
        Missing,
        Invalid,
        Compressed,
        Ready
    }

    private static readonly object SyncRoot = new();
    private static IntPtr _libraryHandle;
    private static AacsRuntimeStatus _status = new(
        AacsRuntimeState.NotConfigured,
        "AACS component not configured");

    public static string SupportDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoirMediaPlayer",
        "aacs");

    public static string KeyDatabaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "aacs");

    public static string KeyDatabasePath => Path.Combine(KeyDatabaseDirectory, "KEYDB.cfg");

    public static AacsRuntimeStatus CurrentStatus
    {
        get
        {
            lock (SyncRoot)
            {
                return _status.IsReady ? CreateReadyStatus(_status.LibraryPath) : _status;
            }
        }
    }

    public static AacsRuntimeStatus Initialize(string? configuredLibraryPath = null)
    {
        lock (SyncRoot)
        {
            if (_libraryHandle != IntPtr.Zero)
            {
                return _status = CreateReadyStatus(_status.LibraryPath);
            }

            AacsRuntimeStatus? firstFailure = null;
            if (!string.IsNullOrWhiteSpace(configuredLibraryPath))
            {
                if (!TryNormalizeLibraryPath(configuredLibraryPath, out var configuredPath))
                {
                    firstFailure = new AacsRuntimeStatus(
                        AacsRuntimeState.Invalid,
                        "Choose a 64-bit file named libaacs.dll");
                }
                else
                {
                    var configuredStatus = File.Exists(configuredPath)
                        ? LoadLibrary(configuredPath)
                        : new AacsRuntimeStatus(
                            AacsRuntimeState.Missing,
                            "Configured libaacs.dll was not found",
                            configuredPath);
                    if (configuredStatus.IsReady)
                    {
                        return _status = configuredStatus;
                    }

                    firstFailure = configuredStatus;
                }
            }

            foreach (var candidate in GetAutomaticLibraryCandidates())
            {
                if (File.Exists(candidate))
                {
                    var candidateStatus = LoadLibrary(candidate);
                    if (candidateStatus.IsReady)
                    {
                        return _status = candidateStatus;
                    }

                    firstFailure ??= candidateStatus;
                }
            }

            return _status = firstFailure ?? new AacsRuntimeStatus(
                AacsRuntimeState.NotConfigured,
                "AACS component not configured");
        }
    }

    public static bool LibraryChangeRequiresRestart(string? configuredLibraryPath, AacsRuntimeStatus currentStatus)
    {
        return TryNormalizeLibraryPath(configuredLibraryPath, out var normalizedPath) &&
               (!currentStatus.IsReady ||
                !string.Equals(normalizedPath, currentStatus.LibraryPath, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryNormalizeLibraryPath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var value = path.Trim();
            if (!Path.IsPathFullyQualified(value))
            {
                return false;
            }

            var candidate = Path.GetFullPath(value);
            if (!Path.IsPathFullyQualified(candidate) ||
                !Path.GetFileName(candidate).Equals(LibraryFileName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            normalizedPath = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<string> GetAutomaticLibraryCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        return new[]
        {
            Path.Combine(SupportDirectory, LibraryFileName),
            Path.Combine(baseDirectory, LibraryFileName),
            Path.Combine(baseDirectory, "libvlc", "win-x64", LibraryFileName)
        }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static AacsRuntimeStatus LoadLibrary(string path)
    {
        var handle = LoadLibraryEx(
            path,
            IntPtr.Zero,
            LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            var detail = error == 193
                ? "the library is not 64-bit"
                : error == 126
                    ? "the library or one of its dependencies is missing"
                    : new Win32Exception(error).Message;
            return new AacsRuntimeStatus(
                AacsRuntimeState.LoadFailed,
                $"AACS component could not load: {detail}",
                path);
        }

        if (!NativeLibrary.TryGetExport(handle, "aacs_open", out _))
        {
            FreeLibrary(handle);
            return new AacsRuntimeStatus(
                AacsRuntimeState.Invalid,
                "The selected DLL is not a compatible libaacs library",
                path);
        }

        _libraryHandle = handle;
        Environment.SetEnvironmentVariable("LIBAACS_PATH", path, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("AACS_CONFIG_DIR", KeyDatabaseDirectory, EnvironmentVariableTarget.Process);
        return CreateReadyStatus(path);
    }

    private static AacsRuntimeStatus CreateReadyStatus(string path)
    {
        var keyDatabaseState = InspectKeyDatabase(KeyDatabasePath);
        var keyDatabaseFound = keyDatabaseState == KeyDatabaseState.Ready;
        return new AacsRuntimeStatus(
            AacsRuntimeState.Ready,
            keyDatabaseState switch
            {
                KeyDatabaseState.Ready => "AACS runtime ready · KEYDB.cfg valid (disc key not yet verified)",
                KeyDatabaseState.Compressed => "AACS runtime ready · KEYDB.cfg is still a ZIP archive; download it again to extract it",
                KeyDatabaseState.Invalid => "AACS runtime ready · KEYDB.cfg is not a valid key database",
                _ => "AACS runtime ready · playback credentials not configured"
            },
            path,
            keyDatabaseFound);
    }

    private static KeyDatabaseState InspectKeyDatabase(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                return KeyDatabaseState.Missing;
            }

            if (IsZipArchive(path))
            {
                return KeyDatabaseState.Compressed;
            }

            return IsValidKeyDatabaseContent(path)
                ? KeyDatabaseState.Ready
                : KeyDatabaseState.Invalid;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return KeyDatabaseState.Invalid;
        }
    }

    internal static bool IsValidKeyDatabaseContent(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096);
            using var reader = new StreamReader(stream);

            // KEYDB files can begin with blank lines and ';' or '#' comments. Look for
            // a config record (| ... |) or a disc-key assignment instead of trusting
            // only the filename or the first byte.
            for (var lineNumber = 0; lineNumber < 10_000 && !reader.EndOfStream; lineNumber++)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                var trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] is ';' or '#')
                {
                    continue;
                }

                if (trimmed.StartsWith("<!", StringComparison.Ordinal) ||
                    trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (trimmed[0] == '|' && trimmed.Count(character => character == '|') >= 3)
                {
                    return true;
                }

                var separatorIndex = trimmed.IndexOf('=');
                if (separatorIndex > 0)
                {
                    var identifier = trimmed[..separatorIndex].Trim();
                    if (identifier.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        identifier = identifier[2..];
                    }

                    if (identifier.Length >= 16 && identifier.All(Uri.IsHexDigit))
                    {
                        return true;
                    }
                }

                return false;
            }

            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsZipArchive(string path)
    {
        Span<byte> signature = stackalloc byte[4];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Read(signature) != signature.Length)
        {
            return false;
        }

        return signature[0] == (byte)'P' &&
               signature[1] == (byte)'K' &&
               (signature[2], signature[3]) is ((3, 4) or (5, 6) or (7, 8));
    }

    public static async Task<AacsKeyDownloadResult> DownloadKeyDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        const string keyDatabaseUrl = "http://fvonline-db.bplaced.net/fv_download.php?lang=eng";
        const string userAgent = "NoirMediaPlayer/1.0";

        try
        {
            Directory.CreateDirectory(KeyDatabaseDirectory);

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            client.Timeout = TimeSpan.FromSeconds(60);

            using var response = await client.GetAsync(
                keyDatabaseUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value == 0)
            {
                return new AacsKeyDownloadResult(
                    false,
                    "The key database server returned an empty response. Try again later.");
            }

            var temporaryPath = Path.Combine(
                KeyDatabaseDirectory,
                $".KEYDB.cfg.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var destinationStream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 8192,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                await CopyToAsync(
                    sourceStream,
                    destinationStream,
                    MaximumDownloadBytes,
                    cancellationToken).ConfigureAwait(false);
                await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup.
                }

                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();

            return await InstallDownloadedKeyDatabaseAsync(
                temporaryPath,
                KeyDatabasePath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return new AacsKeyDownloadResult(
                false,
                $"Could not reach the key database server: {exception.Message}");
        }
        catch (TaskCanceledException)
        {
            return new AacsKeyDownloadResult(
                false,
                "Key database download timed out. Check your internet connection.");
        }
        catch (OperationCanceledException)
        {
            return new AacsKeyDownloadResult(false, "Key database download was cancelled.");
        }
        catch (Exception exception)
        {
            return new AacsKeyDownloadResult(
                false,
                $"Key database download failed: {exception.Message}");
        }
    }

    internal static async Task<AacsKeyDownloadResult> InstallDownloadedKeyDatabaseAsync(
        string downloadedPath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        string? extractedPath = null;
        var installed = false;
        try
        {
            if (!File.Exists(downloadedPath) || new FileInfo(downloadedPath).Length == 0)
            {
                return new AacsKeyDownloadResult(
                    false,
                    "The downloaded key database is empty. The server may be unavailable.");
            }

            var installationCandidate = downloadedPath;
            var wasCompressed = IsZipArchive(downloadedPath);
            if (wasCompressed)
            {
                extractedPath = Path.Combine(
                    Path.GetDirectoryName(destinationPath) ?? Path.GetTempPath(),
                    $".KEYDB.cfg.{Environment.ProcessId}.{Guid.NewGuid():N}.extracting");

                using var archive = ZipFile.OpenRead(downloadedPath);
                var matchingEntries = archive.Entries
                    .Where(entry =>
                        entry.Length > 0 &&
                        Path.GetFileName(entry.FullName).Equals("KEYDB.cfg", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matchingEntries.Length != 1)
                {
                    return new AacsKeyDownloadResult(
                        false,
                        "The downloaded archive does not contain exactly one KEYDB.cfg file.");
                }

                var keyDatabaseEntry = matchingEntries[0];
                if (keyDatabaseEntry.Length > MaximumKeyDatabaseBytes)
                {
                    return new AacsKeyDownloadResult(false, "The key database archive is unexpectedly large.");
                }

                await using (var source = keyDatabaseEntry.Open())
                await using (var destination = new FileStream(
                    extractedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 8192,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await CopyToAsync(
                        source,
                        destination,
                        MaximumKeyDatabaseBytes,
                        cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                installationCandidate = extractedPath;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidKeyDatabaseContent(installationCandidate))
            {
                return new AacsKeyDownloadResult(
                    false,
                    "The downloaded file does not appear to be a valid KEYDB.cfg. The server may have returned an error page.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? Path.GetTempPath());
            var backupPath = destinationPath + ".backup";
            if (File.Exists(destinationPath))
            {
                File.Move(destinationPath, backupPath, true);
            }

            try
            {
                File.Move(installationCandidate, destinationPath);
                installed = true;
            }
            catch
            {
                if (File.Exists(backupPath) && !File.Exists(destinationPath))
                {
                    File.Move(backupPath, destinationPath);
                }

                throw;
            }

            return new AacsKeyDownloadResult(
                true,
                wasCompressed
                    ? "AACS key database downloaded and extracted successfully."
                    : "AACS key database installed successfully.");
        }
        finally
        {
            if (!installed || !string.Equals(downloadedPath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(downloadedPath);
            }

            if (extractedPath is not null &&
                (!installed || !string.Equals(extractedPath, destinationPath, StringComparison.OrdinalIgnoreCase)))
            {
                TryDeleteFile(extractedPath);
            }
        }
    }

    private static async Task CopyToAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long totalBytes = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return;
            }

            totalBytes += bytesRead;
            if (totalBytes > maximumBytes)
            {
                throw new InvalidDataException("The downloaded key database is unexpectedly large.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of a temporary download or extraction.
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
