using NoirMediaPlayer.Services;

// Deliberately has no application, WPF, native decoder or GPU references.
if (args.Length != 2) return 4;
if (args[0] == "no-read")
{
    File.WriteAllText(args[1], Environment.ProcessId.ToString());
    Thread.Sleep(TimeSpan.FromSeconds(8));
    return 4;
}
return SnapshotProcessHost.Run((request, staging) =>
{
    File.WriteAllText(staging, "partial fake image");
    File.WriteAllText(args[1], Environment.ProcessId.ToString());
    if (args[0] == "hang") Thread.Sleep(TimeSpan.FromSeconds(8));
    if (args[0] == "fail") throw new IOException("fake decoder failure");
    if (args[0] == "missing") { File.Delete(staging); return; }
    File.WriteAllBytes(staging, SnapshotProtocol.Encode(request, staging));
});
