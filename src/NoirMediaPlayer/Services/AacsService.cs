using System.ComponentModel;
using System.IO;
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
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

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
        return CreateReadyStatus(path);
    }

    private static AacsRuntimeStatus CreateReadyStatus(string path)
    {
        var keyDatabaseFound = HasKeyDatabase();
        return new AacsRuntimeStatus(
            AacsRuntimeState.Ready,
            keyDatabaseFound
                ? "AACS runtime ready · KEYDB.cfg found (disc key not yet verified)"
                : "AACS runtime ready · playback credentials not configured",
            path,
            keyDatabaseFound);
    }

    private static bool HasKeyDatabase()
    {
        try
        {
            return File.Exists(KeyDatabasePath) && new FileInfo(KeyDatabasePath).Length > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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

                await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
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

            if (new FileInfo(temporaryPath).Length == 0)
            {
                File.Delete(temporaryPath);
                return new AacsKeyDownloadResult(
                    false,
                    "The downloaded key database is empty. The server may be unavailable.");
            }

            if (File.Exists(KeyDatabasePath))
            {
                var backupPath = KeyDatabasePath + ".backup";
                File.Move(KeyDatabasePath, backupPath, true);
            }

            File.Move(temporaryPath, KeyDatabasePath);

            return new AacsKeyDownloadResult(true, "AACS key database installed successfully.");
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

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
