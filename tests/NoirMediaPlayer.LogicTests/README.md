# Isolated logic tests

This project compiles selected production logic directly, without referencing the
WPF app, LibVLC, or Media Foundation. Tests use in-memory state, a manual clock and
temporary playlist files. They do not create windows, play media, capture images,
enumerate optical drives, or invoke native disc operations.

The subtitle regressions also compile the actual `MainWindow.SubtitleRestoration.cs`
partial against managed fake collaborators in `SubtitleLogicFixture.cs`. The
inventory, load/capture/restore methods, queue deadlines, and generation guards
come from production source. The fake `MainWindow` has no window base class; its
track IDs, queued commands and failure notifications are in-memory values. The
`MediaSlaveType` test declaration is an enum shape, not a LibVLC dependency.
The tests cover successful SRT/ASS session capture, ambiguous discovery, blocked
attachment/selection commands, late faults and superseded generations. Timeout
cases deliberately wait for the five-second logic deadline without calling any
native operation.

`VideoPresentationLogicTests` links `MainWindow.VideoPresentation.cs` against the
same managed collaborators. It verifies commands for first/recreated outputs,
Original and forced ratios, output removal and stale generations. Its video-view
replacement is a counter: no window, image, frame, DVD, renderer or graphics API
is created or accessed.
DVD startup cases also verify that a temporary engine aspect is prepared before
playback and replaced with the selected ratio when the video output becomes
available. Other sources start with their selected ratio directly.

The startup, position-restoration and snapshot partials are also linked directly.
Regressions cover rotation changed before queued native creation, stale startup,
zero/unknown/nonseekable positions, delayed metadata, and discovery expiration.
Manual clocks advance the two-second restoration and ten-second capture deadlines.
Snapshot tests launch `NoirMediaPlayer.SnapshotTestHost`, a managed console helper
with no WPF, native decoder or graphics references. It shares the production pipe
protocol and parent-disconnect watchdog, replacing decoding with tiny text files
or an eight-second bounded sleep. The production process launcher kills and reaps
these children on timeout/cancellation, including when a pipe write is blocked by
a child that never reads its input. Tests verify retry without app restart, the
single-child cap, transport/stop/close responsiveness, partial-output cleanup,
destination collisions, launch/decoder failures, compatibility fallback, parent
disconnect, and preservation of time, rotation, Unicode captions and styles.
Protocol tests cover null, invalid, truncated and oversized requests. Process
tests run sequentially with a 50 ms pause before each launch and bounded waits.
They neither decode media nor allocate graphics surfaces.

Run this project explicitly when playback/GPU tests are prohibited:

```powershell
dotnet test .\tests\NoirMediaPlayer.LogicTests\NoirMediaPlayer.LogicTests.csproj --configuration Release -p:Platform=x64
```

Compile the application or existing integration tests without loading the custom
VLC runtime or regenerating its native plugin cache:

```powershell
dotnet build .\src\NoirMediaPlayer\NoirMediaPlayer.csproj --configuration Release -p:Platform=x64 -p:NoirSkipNativeRuntimeChecks=true --no-restore
dotnet build .\tests\NoirMediaPlayer.Tests\NoirMediaPlayer.Tests.csproj --configuration Release -p:Platform=x64 -p:NoirSkipNativeRuntimeChecks=true --no-restore
```

The skip property affects build/publish runtime checks, not application behavior.
Normal builds retain those checks. Do not use a solution-wide test command under
the GPU restriction: the separate `NoirMediaPlayer.Tests` suite includes playback,
rendering and snapshot tests. `AuditFixMigrationTests.cs` adds four media integration
regressions for the audit fixes; these are compile-only during restricted validation.
