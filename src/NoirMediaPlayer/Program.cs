using System.Runtime.CompilerServices;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == SnapshotProcess.WorkerArgument)
            return args.Length == 1
                ? Task.Run(() => SnapshotProcessHost.Run(DecodeSnapshot)).GetAwaiter().GetResult() : 2;
        return RunApplication();
    }

    private static void DecodeSnapshot(SourceSnapshotRequest request, string path) =>
        WindowsMediaEngine.SaveSourceSnapshot(request, path);

    // Worker startup never creates the WPF application, window, or VLC prewarm.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication()
    {
        var application = new App();
        application.InitializeComponent();
        return application.Run();
    }
}
