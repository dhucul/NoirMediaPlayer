using System.Globalization;
using System.Runtime.InteropServices;

namespace NoirMediaPlayer.Services;

internal sealed record WindowsAudioTrack(int Id, int Ordinal, string Name, bool Selected);

internal sealed unsafe partial class WindowsMediaEngine
{
    private bool _audioDisabled;
    private bool _muted;
    private static readonly Guid MajorTypeKey = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid AudioType = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoType = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid StreamNameKey = new("4F1B099D-D314-41E5-A781-7FEFAA4C501F");
    private static readonly Guid LanguageKey = new("00AF2180-BDC2-423C-ABCA-F503593BC121");

    // Audio-only mode still exposes the complete inventory. Reject video there so
    // disabling hardware decoding cannot silently turn a video into audio playback.
    // Unsupported captions, unknown streams and alternate video retain compatibility support.
    internal bool HasSupportedStreams
    {
        get
        {
            lock (_sync)
            {
                EnsureAlive();
                if (!HasSupportedCaptionTracks()) return false;
                var types = StreamTypes();
                if (_mpegSource && (_mpegInventory is null || types.Count(type => type == VideoType) != _mpegInventory.VideoCount ||
                    types.Count(type => type == AudioType) != _mpegInventory.AudioCount)) return false;
                // Network Matroska and identified download endpoints can reset the
                // video clock after an audio-track change. Keep their multi-track
                // video on VLC until switching and immediate seeks are reliable.
                if (types.Contains(VideoType) && types.Count(type => type == AudioType) > 1 &&
                    Uri.TryCreate(_source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
                    (uri.AbsolutePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || _sourceContentType is not null)) return false;
                return types.Length > 0 && types.Count(type => type == VideoType) <= 1 &&
                    types.All(type => type == AudioType || !_requireAudioOnly && type == VideoType);
            }
        }
    }

    internal IReadOnlyList<WindowsAudioTrack> AudioTracks
    {
        get
        {
            lock (_sync)
            {
                EnsureAlive();
                var types = StreamTypes();
                var tracks = new List<WindowsAudioTrack>();
                for (var index = 0; index < types.Length; index++)
                {
                    if (types[index] != AudioType) continue;
                    var name = StreamText(index, StreamNameKey);
                    var language = StreamText(index, LanguageKey);
                    if (!string.IsNullOrWhiteSpace(language))
                    {
                        try { language = CultureInfo.GetCultureInfo(language).DisplayName; }
                        catch (CultureNotFoundException) { }
                    }
                    name = string.IsNullOrWhiteSpace(name) ? $"Audio {tracks.Count + 1}" : name;
                    if (!string.IsNullOrWhiteSpace(language)) name += $" · {language}";
                    var selected = 0;
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, int*, int>)Slot(_extended, 56))(_extended, (uint)index, &selected));
                    tracks.Add(new WindowsAudioTrack(index, tracks.Count, name, selected != 0 && !_audioDisabled));
                }
                return tracks;
            }
        }
    }

    internal void SelectAudioTrack(int id)
    {
        lock (_sync)
        {
            EnsureAlive();
            var tracks = AudioTracks;
            if (id != -1 && !tracks.Any(track => track.Id == id)) throw new ArgumentOutOfRangeException(nameof(id));
            // Deselecting the final audio-only stream can shut down the source. Silence
            // it separately from the user's mute setting so it can always be re-enabled.
            if (id == -1)
            {
                _audioDisabled = true;
                ApplyAudioMute();
                return;
            }
            var changed = false;
            foreach (var track in tracks)
            {
                var selected = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int*, int>)Slot(_extended, 56))(_extended, (uint)track.Id, &selected));
                var requested = track.Id == id ? 1 : 0;
                if ((selected != 0) == (requested != 0)) continue;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(_extended, 57))(_extended, (uint)track.Id, requested));
                changed = true;
            }
            // Audio off only mutes the selected stream. Re-enabling that stream
            // must not reconfigure it and asynchronously reset a later seek.
            if (changed) Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(_extended, 58))(_extended));
            _audioDisabled = false;
            ApplyAudioMute();
        }
    }

    private void ApplyAudioMute() => Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(_engine, 35))(_engine, _muted || _audioDisabled ? 1 : 0));

    private Guid[] StreamTypes()
    {
        uint count = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(_extended, 54))(_extended, &count));
        if (count > 256) throw new InvalidOperationException("Too many media streams.");
        var types = new Guid[count];
        for (var index = 0; index < types.Length; index++)
        {
            var value = new PropVariant();
            try
            {
                var key = MajorTypeKey;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, PropVariant*, int>)Slot(_extended, 55))(_extended, (uint)index, &key, &value));
                if (value.Type != 72 || value.Pointer == 0) throw new InvalidOperationException("The stream type is unavailable.");
                types[index] = Marshal.PtrToStructure<Guid>(value.Pointer);
            }
            finally { PropVariantClear(ref value); }
        }
        return types;
    }

    private string? StreamText(int index, Guid key)
    {
        var value = new PropVariant();
        try
        {
            var result = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, PropVariant*, int>)Slot(_extended, 55))(_extended, (uint)index, &key, &value);
            if (result < 0 || value.Pointer == 0) return null;
            return value.Type switch { 31 => Marshal.PtrToStringUni(value.Pointer), 8 => Marshal.PtrToStringBSTR(value.Pointer), _ => null };
        }
        finally { PropVariantClear(ref value); }
    }

    // x64 PROPVARIANT includes a 16-byte union following the 8-byte type/reserved header.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal nint Pointer;
        [FieldOffset(8)] internal long Int64;
    }

    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int PropVariantClear(ref PropVariant value);
}
