# NOIR Media Player

NOIR is a modern, keyboard-first DVD, video, audio, and network-stream player for Windows. It is built with WPF on .NET 10 and uses LibVLC for broad codec, subtitle, disc, and streaming support.

## What is included

- Sleek black custom-chrome interface using Segoe UI Variable and Segoe Fluent Icons
- LibVLC 3 playback engine with hardware-decoding support
- Local video and audio playback across common container and codec formats
- Optical DVD and Blu-ray source discovery
- One-click optical-disc eject with active-drive selection
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

NOIR bundles an open-source libaacs runtime for AACS integration. It does not bundle decryption keys or certificates.

When an AACS-protected Blu-ray is opened, NOIR uses the bundled 64-bit libaacs and libbluray modules. If the runtime is missing or damaged, NOIR reports `AACS REQUIRED` instead of remaining at `BUFFERING 0%`. A protected disc that does not begin playback within 35 seconds is stopped with an AACS-specific error rather than buffering indefinitely. Unprotected Blu-rays do not require a key database.

For protected media, place your legally obtained plaintext `KEYDB.cfg` at `%APPDATA%\aacs\KEYDB.cfg` using **Quick Settings → Blu-ray AACS → Open key folder**. **Download AACS keys** accepts either plaintext or a ZIP download and extracts and validates the database before installing it. If a ZIP archive has already been saved as `KEYDB.cfg`, NOIR repairs it automatically at startup and retains the archive as `KEYDB.cfg.backup`. Finding a valid file does not prove that it contains a matching key for a particular disc; NOIR reports an unlock failure when playback cannot begin. A different compatible 64-bit `libaacs.dll` can be selected in Quick Settings and is validated and applied after NOIR restarts. The VideoLAN libaacs project supplies no keys or certificates; key material must be configured for the disc in accordance with local law, or the disc must be played with licensed Blu-ray playback software. Binary provenance, source links, and licenses for the replaceable bundled libraries are documented in `src/NoirMediaPlayer/ThirdParty/Aacs/README.md`.

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
| `Ctrl` + `E` | Eject the active optical disc |
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
