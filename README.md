# NOIR Media Player

NOIR is a modern, keyboard-first DVD, video, audio, and network-stream player for Windows. It is built with WPF on .NET 10 and uses LibVLC for broad codec, subtitle, disc, and streaming support.

## What is included

- Sleek black custom-chrome interface using Segoe UI Variable and Segoe Fluent Icons
- LibVLC 3 playback engine with hardware-decoding support
- Local video and audio playback across common container and codec formats
- Optical DVD and Blu-ray source discovery
- `VIDEO_TS` and `BDMV` folder playback
- DVD menu activation and `Alt` + arrow-key navigation
- HTTP, HTTPS, RTSP, RTP, UDP, and direct network streams
- Multi-select file opening, recursive folder scanning, and drag-and-drop
- Searchable play queue with M3U/M3U8 import and export
- Shuffle plus Off, All, and One repeat modes
- Automatic next-item playback
- Remembered playback positions and recent-source history
- Audio-track and subtitle-track selection
- External SRT, ASS, SSA, SUB, VTT, and IDX subtitle loading
- Playback rates from 0.5× to 2×
- Aspect-ratio overrides, 90-degree rotation, and chapter navigation
- PNG video snapshots saved to `Pictures\Noir Snapshots`
- Fullscreen and always-on-top mini-player modes
- Persistent volume, mute, playback, and decoding preferences
- Per-monitor DPI awareness and long-path support

Protected commercial discs can depend on additional playback/decryption components permitted in the user's jurisdiction. NOIR does not bundle those components.

## Keyboard controls

| Key | Action |
| --- | --- |
| `Space` | Play or pause |
| `Left` / `Right` | Seek backward or forward 10 seconds |
| `Shift` + `Left` / `Right` | Seek backward or forward 30 seconds |
| `Up` / `Down` | Volume up or down |
| `Page Up` / `Page Down` | Previous or next queue item |
| `F` / `Esc` | Enter or leave fullscreen |
| `P` | Toggle mini player |
| `M` | Mute |
| `S` | Save a snapshot |
| `V` | Load external subtitles |
| `R` | Cycle repeat mode |
| `H` | Toggle shuffle |
| `I` / `B` | Toggle the inspector or queue sidebar |
| `Ctrl` + `O` | Open media files |
| `Ctrl` + `Shift` + `O` | Open a folder |
| `Ctrl` + `D` | Open an optical disc |
| `Ctrl` + `L` | Open a network stream |
| `Alt` + arrow keys | Navigate a DVD menu |
| `Enter` | Activate a DVD menu item or play the selected queue item |

## Build

Requirements:

- Windows 10 or later, x64
- .NET 10 SDK (`10.0.400` or a compatible newer feature band)

```powershell
dotnet restore .\NoirMediaPlayer.slnx
dotnet build .\NoirMediaPlayer.slnx --configuration Release
dotnet run --project .\src\NoirMediaPlayer\NoirMediaPlayer.csproj
```

The VideoLAN native runtime is restored through the `VideoLAN.LibVLC.Windows` NuGet package, so a separate VLC installation is not required.

## Project layout

```text
src/NoirMediaPlayer/
├── Models/       Queue and persistent-setting models
├── Services/     Disc discovery, media enumeration, and settings storage
├── App.xaml      Global NOIR design system
├── MainWindow.*  Player workspace and playback orchestration
└── OpenLocationWindow.*
```

Settings are stored per user at `%LOCALAPPDATA%\NoirMediaPlayer\settings.json`.
