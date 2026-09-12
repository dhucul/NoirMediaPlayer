using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
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
    private const int MaximumValidationCharacters = 4 * 1024 * 1024;
    private const int MaximumValidationLineCharacters = 16 * 1024;
    private const int MaximumValidationLines = 10_000;
    private static readonly TimeSpan KeyDatabaseDownloadTimeout = TimeSpan.FromSeconds(60);
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    private enum KeyDatabaseState
    {
        Missing,
        Invalid,
        Compressed,
        Ready
    }

    private readonly record struct KeyDatabaseCacheEntry(
        DateTime LastWriteTimeUtc,
        long Length,
        KeyDatabaseState State);

    private static readonly object SyncRoot = new();
    private static readonly SemaphoreSlim KeyDatabaseInstallGate = new(1, 1);

    // One handler for the process: a per-download HttpClient leaves its socket in TIME_WAIT.
    // The infinite timeout is deliberate - every call supplies its own CancellationToken.
    private static readonly HttpClient KeyDatabaseClient = CreateKeyDatabaseClient();

    private static IntPtr _libraryHandle;

    // Guarded by SyncRoot, like every other read of the key database state.
    private static KeyDatabaseCacheEntry? _keyDatabaseCache;

    private static AacsRuntimeStatus _status = new(
        AacsRuntimeState.NotConfigured,
        "AACS component not configured");

    private static HttpClient CreateKeyDatabaseClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NoirMediaPlayer/1.0");
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    public static string SupportDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoirMediaPlayer",
        "aacs");

    public static string KeyDatabaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "aacs");

    public static string KeyDatabasePath => Path.Combine(KeyDatabaseDirectory, "KEYDB.cfg");

    internal static Uri KeyDatabaseDownloadUri { get; } =
        new("https://fvonline-db.bplaced.net/fv_download.php?lang=eng", UriKind.Absolute);

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

    /// <summary>
    /// Drops the cached key database verdict so the next status read re-inspects the file.
    /// </summary>
    private static void InvalidateKeyDatabaseCache()
    {
        lock (SyncRoot)
        {
            _keyDatabaseCache = null;
        }
    }

    private static KeyDatabaseState InspectKeyDatabase(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                _keyDatabaseCache = null;
                return KeyDatabaseState.Missing;
            }

            // CurrentStatus is read from the UI thread on every settings-popup open, every
            // playback attempt and every failure dialog. Re-parsing up to 4 MB of key database
            // under SyncRoot each time would stall the dispatcher, so the verdict is cached
            // against the file's write stamp and size and only recomputed when those change.
            if (_keyDatabaseCache is { } cache &&
                cache.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc &&
                cache.Length == fileInfo.Length)
            {
                return cache.State;
            }

            var state = IsZipArchive(path)
                ? KeyDatabaseState.Compressed
                : IsValidKeyDatabaseContent(path)
                    ? KeyDatabaseState.Ready
                    : KeyDatabaseState.Invalid;

            _keyDatabaseCache = new KeyDatabaseCacheEntry(fileInfo.LastWriteTimeUtc, fileInfo.Length, state);
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _keyDatabaseCache = null;
            return KeyDatabaseState.Invalid;
        }
    }

    internal static bool IsValidKeyDatabaseContent(string path)
    {
        try
        {
            var fileLength = new FileInfo(path).Length;
            if (fileLength == 0 || fileLength > MaximumKeyDatabaseBytes)
            {
                return false;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096);
            using var reader = new StreamReader(stream);
            return ValidateKeyDatabaseText(reader);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsZipArchive(string path)
    {
        try
        {
            Span<byte> signature = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < signature.Length)
            {
                return false;
            }

            // Stream.Read may legally return fewer bytes than asked for; treating a short read
            // as "not an archive" would install a ZIP as though it were a plaintext KEYDB.cfg.
            stream.ReadExactly(signature);

            return signature[0] == (byte)'P' &&
                   signature[1] == (byte)'K' &&
                   (signature[2], signature[3]) is ((3, 4) or (5, 6) or (7, 8));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task<AacsKeyDownloadResult> DownloadKeyDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(KeyDatabaseDirectory);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(KeyDatabaseDownloadTimeout);

            using var response = await KeyDatabaseClient.GetAsync(
                KeyDatabaseDownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var responseUri = response.RequestMessage?.RequestUri;
            if (responseUri is null || !responseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return new AacsKeyDownloadResult(
                    false,
                    "The key database server redirected to an insecure connection.");
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value == 0)
            {
                return new AacsKeyDownloadResult(
                    false,
                    "The key database server returned an empty response. Try again later.");
            }

            if (contentLength > MaximumDownloadBytes)
            {
                return new AacsKeyDownloadResult(
                    false,
                    "The key database download is unexpectedly large.");
            }

            var temporaryPath = Path.Combine(
                KeyDatabaseDirectory,
                $".KEYDB.cfg.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using var sourceStream = await response.Content.ReadAsStreamAsync(timeout.Token)
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
                    timeout.Token).ConfigureAwait(false);
                await destinationStream.FlushAsync(timeout.Token).ConfigureAwait(false);
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

            timeout.Token.ThrowIfCancellationRequested();

            return await InstallDownloadedKeyDatabaseAsync(
                temporaryPath,
                KeyDatabasePath,
                timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return new AacsKeyDownloadResult(
                false,
                $"Could not reach the key database server: {exception.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AacsKeyDownloadResult(false, "Key database download was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return new AacsKeyDownloadResult(
                false,
                "Key database download timed out. Check your internet connection.");
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
        var gateEntered = false;
        var downloadedIsDestination = PathsReferToSameFile(downloadedPath, destinationPath);
        try
        {
            await KeyDatabaseInstallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;

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

            if (!wasCompressed && downloadedIsDestination)
            {
                return new AacsKeyDownloadResult(true, "AACS key database is already installed as plaintext.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? Path.GetTempPath());
            var backupPath = destinationPath + ".backup";
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // A single filesystem replacement keeps the destination present throughout
                // commit, including if the application exits while installation is completing.
                if (File.Exists(destinationPath))
                    File.Replace(installationCandidate, destinationPath, backupPath);
                else
                    File.Move(installationCandidate, destinationPath);
            }
            finally
            {
                // The file behind the cached verdict has just been replaced either way.
                InvalidateKeyDatabaseCache();
            }

            return new AacsKeyDownloadResult(
                true,
                wasCompressed
                    ? "AACS key database downloaded and extracted successfully."
                    : "AACS key database installed successfully.");
        }
        finally
        {
            if (!downloadedIsDestination)
            {
                TryDeleteFile(downloadedPath);
            }

            if (extractedPath is not null)
            {
                TryDeleteFile(extractedPath);
            }

            if (gateEntered)
            {
                KeyDatabaseInstallGate.Release();
            }
        }
    }

    internal static async Task<AacsKeyDownloadResult?> RepairCompressedKeyDatabaseIfNeededAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(path) || !IsZipArchive(path))
            {
                return null;
            }

            return await InstallDownloadedKeyDatabaseAsync(path, path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new AacsKeyDownloadResult(
                false,
                $"The compressed KEYDB.cfg could not be extracted automatically: {exception.Message}");
        }
    }

    private static bool ValidateKeyDatabaseText(StreamReader reader)
    {
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        var line = new StringBuilder(256);
        var totalCharacters = 0;
        var lineCount = 0;
        var skipLineFeed = false;
        try
        {
            while (true)
            {
                var charactersRead = reader.Read(buffer, 0, buffer.Length);
                if (charactersRead == 0)
                {
                    return line.Length > 0 && EvaluateKeyDatabaseLine(line) == true;
                }

                for (var index = 0; index < charactersRead; index++)
                {
                    var character = buffer[index];
                    totalCharacters++;
                    if (totalCharacters > MaximumValidationCharacters)
                    {
                        return false;
                    }

                    if (skipLineFeed)
                    {
                        skipLineFeed = false;
                        if (character == '\n')
                        {
                            continue;
                        }
                    }

                    if (character is '\r' or '\n')
                    {
                        lineCount++;
                        var lineResult = EvaluateKeyDatabaseLine(line);
                        if (lineResult.HasValue)
                        {
                            return lineResult.Value;
                        }

                        if (lineCount >= MaximumValidationLines)
                        {
                            return false;
                        }

                        line.Clear();
                        skipLineFeed = character == '\r';
                        continue;
                    }

                    if (line.Length >= MaximumValidationLineCharacters)
                    {
                        return false;
                    }

                    line.Append(character);
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static bool? EvaluateKeyDatabaseLine(StringBuilder line)
    {
        var trimmed = line.ToString().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is ';' or '#')
        {
            return null;
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
        if (separatorIndex <= 0)
        {
            return false;
        }

        var identifier = trimmed[..separatorIndex].Trim();
        if (identifier.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            identifier = identifier[2..];
        }

        return identifier.Length >= 16 && identifier.All(Uri.IsHexDigit);
    }

    private static bool PathsReferToSameFile(string firstPath, string secondPath)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(firstPath),
                Path.GetFullPath(secondPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase);
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
