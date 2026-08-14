using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NoirMediaPlayer.Models;

namespace NoirMediaPlayer.Services;

public sealed record DiscInfo(string Root, string Label, string Source, string Format)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? $"{Format} · {Root}" : $"{Label} · {Root}";
}

public sealed record DiscEjectResult(bool Success, string Message, string DriveRoot = "");

public static class DiscService
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint IoctlStorageEjectMedia = 0x002D4808;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotReady = 21;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorIoPending = 997;
    private const int ErrorBusy = 170;
    private static readonly TimeSpan EjectTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan[] EjectRetryDelays =
    [
        TimeSpan.FromMilliseconds(75),
        TimeSpan.FromMilliseconds(175),
        TimeSpan.FromMilliseconds(350)
    ];

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlappedData
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr EventHandle;
    }

    private readonly record struct EjectAttempt(bool Success, int ErrorCode);

    public static IReadOnlyList<DiscInfo> FindVideoDiscs(CancellationToken cancellationToken = default)
    {
        var result = new List<DiscInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.CDRom || !drive.IsReady)
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                if (Directory.Exists(Path.Combine(root, "BDMV")))
                {
                    result.Add(new DiscInfo(root, drive.VolumeLabel, CreateDiscSource("bluray", root), "BLU-RAY"));
                }
                else if (Directory.Exists(Path.Combine(root, "VIDEO_TS")))
                {
                    result.Add(new DiscInfo(root, drive.VolumeLabel, CreateDiscSource("dvd", root), "DVD"));
                }
            }
            catch
            {
                // Ignore inaccessible or transitioning optical drives.
            }
        }

        return result;
    }

    public static IReadOnlyList<string> FindOpticalDriveRoots()
    {
        var result = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.CDRom)
                {
                    result.Add(drive.RootDirectory.FullName);
                }
            }
            catch
            {
                // Ignore optical drives that are transitioning or unavailable.
            }
        }

        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task<DiscEjectResult> EjectAsync(
        string driveRoot,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = NormalizeDriveRoot(driveRoot);
        if (normalizedRoot is null)
        {
            return new DiscEjectResult(false, "The selected optical drive is not valid.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(EjectTimeout);

        try
        {
            if (new DriveInfo(normalizedRoot).DriveType != DriveType.CDRom)
            {
                return new DiscEjectResult(false, $"{normalizedRoot} is not an optical drive.");
            }

            EjectAttempt attempt = default;
            for (var attemptIndex = 0; attemptIndex <= EjectRetryDelays.Length; attemptIndex++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                attempt = await TryEjectOnceAsync(normalizedRoot, timeout.Token).ConfigureAwait(false);
                if (attempt.Success)
                {
                    return new DiscEjectResult(true, $"Ejected {normalizedRoot}", normalizedRoot);
                }

                if (!IsTransientEjectError(attempt.ErrorCode) || attemptIndex == EjectRetryDelays.Length)
                {
                    break;
                }

                await Task.Delay(EjectRetryDelays[attemptIndex], timeout.Token).ConfigureAwait(false);
            }

            return new DiscEjectResult(
                false,
                $"Could not eject {normalizedRoot}: {new Win32Exception(attempt.ErrorCode).Message}",
                normalizedRoot);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DiscEjectResult(
                false,
                $"Ejecting {normalizedRoot} timed out.",
                normalizedRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new DiscEjectResult(false, $"Could not eject {normalizedRoot}: {exception.Message}", normalizedRoot);
        }
    }

    internal static string? SelectEjectTarget(
        IEnumerable<string> opticalDriveRoots,
        params string?[] preferredDiscSources)
    {
        var availableRoots = opticalDriveRoots
            .Select(NormalizeDriveRoot)
            .Where(root => root is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var source in preferredDiscSources)
        {
            if (source is not null &&
                TryGetDiscRoot(source, out var sourceRoot) &&
                availableRoots.FirstOrDefault(root =>
                    string.Equals(root, sourceRoot, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                return match;
            }
        }

        return availableRoots.Length == 1 ? availableRoots[0] : null;
    }

    internal static bool IsDiscSourceOnDrive(string source, string driveRoot) =>
        TryGetDiscRoot(source, out var sourceRoot) &&
        NormalizeDriveRoot(driveRoot) is { } normalizedRoot &&
        string.Equals(sourceRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase);

    public static PlaylistItem CreateItem(DiscInfo disc) => new()
    {
        Source = disc.Source,
        Title = string.IsNullOrWhiteSpace(disc.Label) ? $"{disc.Format} video" : disc.Label,
        Detail = disc.DisplayName,
        IsDisc = true
    };

    public static PlaylistItem? CreateFromFolder(string folder)
    {
        if (Directory.Exists(Path.Combine(folder, "VIDEO_TS")) &&
            !Directory.Exists(Path.Combine(folder, "BDMV")))
        {
            return new PlaylistItem
            {
                Source = CreateDiscSource("dvd", folder),
                Title = new DirectoryInfo(folder).Name,
                Detail = "DVD folder",
                IsDisc = true
            };
        }

        if (Directory.Exists(Path.Combine(folder, "BDMV")))
        {
            return new PlaylistItem
            {
                Source = CreateDiscSource("bluray", folder),
                Title = new DirectoryInfo(folder).Name,
                Detail = "Blu-ray folder",
                IsDisc = true
            };
        }

        return null;
    }

    public static bool IsAacsProtectedSource(string source)
    {
        if (!TryGetDiscFolder(source, "bluray", out var folder))
        {
            return false;
        }

        try
        {
            return Directory.Exists(Path.Combine(folder, "AACS"));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryGetDiscFolder(string source, string expectedScheme, out string folder)
    {
        folder = string.Empty;
        try
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
                !uri.Scheme.Equals(expectedScheme, StringComparison.OrdinalIgnoreCase) ||
                uri.IsUnc ||
                !string.IsNullOrEmpty(uri.Host))
            {
                return false;
            }

            folder = Path.GetFullPath(uri.LocalPath);
            return Path.IsPathFullyQualified(folder);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            folder = string.Empty;
            return false;
        }
    }

    internal static bool TryGetDiscRoot(string source, out string root)
    {
        root = string.Empty;
        if (!TryGetDiscFolder(source, "bluray", out var folder) &&
            !TryGetDiscFolder(source, "dvd", out folder))
        {
            return false;
        }

        var normalizedRoot = NormalizeDriveRoot(Path.GetPathRoot(folder));
        if (normalizedRoot is null)
        {
            return false;
        }

        root = normalizedRoot;
        return true;
    }

    private static string? NormalizeDriveRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string CreateDiscSource(string scheme, string folder)
    {
        var path = Path.GetFullPath(folder);
        var fileUri = new Uri(path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar);
        return $"{scheme}:///{fileUri.AbsolutePath.TrimStart('/')}";
    }

    private static async Task<EjectAttempt> TryEjectOnceAsync(
        string normalizedRoot,
        CancellationToken cancellationToken)
    {
        var devicePath = $@"\\.\{normalizedRoot.TrimEnd(Path.DirectorySeparatorChar)}";
        using var device = CreateFile(
            devicePath,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);
        if (device.IsInvalid)
        {
            return new EjectAttempt(false, Marshal.GetLastWin32Error());
        }

        using var completionEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        var overlapped = new NativeOverlappedData
        {
            EventHandle = completionEvent.SafeWaitHandle.DangerousGetHandle()
        };
        var overlappedPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlappedData>());
        var operationPending = false;
        try
        {
            Marshal.StructureToPtr(overlapped, overlappedPointer, fDeleteOld: false);
            cancellationToken.ThrowIfCancellationRequested();

            if (DeviceIoControl(
                    device,
                    IoctlStorageEjectMedia,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero,
                    0,
                    out _,
                    overlappedPointer))
            {
                return new EjectAttempt(true, 0);
            }

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorIoPending)
            {
                return new EjectAttempt(false, error);
            }

            operationPending = true;
            using var cancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var context = ((SafeFileHandle Device, IntPtr Overlapped))state!;
                    _ = CancelIoEx(context.Device, context.Overlapped);
                },
                (device, overlappedPointer));

            await WaitForSignalAsync(completionEvent).ConfigureAwait(false);
            operationPending = false;
            cancellationToken.ThrowIfCancellationRequested();

            return GetOverlappedResult(device, overlappedPointer, out _, wait: false)
                ? new EjectAttempt(true, 0)
                : new EjectAttempt(false, Marshal.GetLastWin32Error());
        }
        finally
        {
            if (operationPending)
            {
                _ = CancelIoEx(device, overlappedPointer);
                completionEvent.WaitOne();
            }

            Marshal.FreeHGlobal(overlappedPointer);
        }
    }

    private static Task WaitForSignalAsync(WaitHandle waitHandle)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle? registration = null;
        registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);
        return CompleteWaitAsync(completion.Task, registration);
    }

    private static async Task CompleteWaitAsync(Task completion, RegisteredWaitHandle registration)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    private static bool IsTransientEjectError(int errorCode) =>
        errorCode is ErrorAccessDenied or ErrorNotReady or ErrorSharingViolation or ErrorLockViolation or ErrorBusy;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle device,
        IntPtr overlapped,
        out uint bytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle device, IntPtr overlapped);
}
