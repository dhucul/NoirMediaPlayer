using System.IO;
using System.Runtime.InteropServices;

namespace NoirMediaPlayer.Tests;

/// <summary>ASF fixtures encoded by Windows, with markers written by Windows' metadata API.</summary>
internal static unsafe class AsfFixture
{
    private static readonly Lazy<byte[]> Audio = new(() => Encode(false));
    private static readonly Lazy<byte[]> Video = new(() => Encode(true));
    private static readonly Lazy<byte[]> LongAudio = new(() => Encode(false, 400));

    internal static void Write(string path, bool video = false, params long[] markers) => Write(path, (video ? Video : Audio).Value, markers);
    internal static void WriteLongAudio(string path, params long[] markers) => Write(path, LongAudio.Value, markers);

    private static void Write(string path, byte[] data, long[] markers)
    {
        File.WriteAllBytes(path, data);
        if (markers.Length > 0) Edit(path, header =>
        {
            foreach (var time in markers.Reverse())
            {
                var name = time == 4000 ? "日本語 café" : "Chapter";
                Marker(header, checked((ulong)time * 10_000), name);
            }
        });
    }

    internal static void Marker(nint header, ulong time, string name = "Chapter")
    {
        fixed (char* text = name)
            Check(((delegate* unmanaged[Stdcall]<nint, char*, ulong, int>)Slot(header, 9))(header, text, time));
    }

    internal static void Edit(string path, Action<nint> edit)
    {
        var apartment = CoInitializeEx(0, 0);
        nint editor = 0, header = 0;
        var opened = false;
        try
        {
            if (apartment < 0 && apartment != unchecked((int)0x80010106)) Check(apartment);
            Check(WMCreateEditor(out editor));
            fixed (char* filename = path)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(editor, 3))(editor, filename));
            opened = true;
            var id = new Guid("96406BDA-2B2B-11D3-B36B-00C04F6108FF");
            Check(Marshal.QueryInterface(editor, in id, out header));
            edit(header);
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(editor, 5))(editor));
        }
        finally
        {
            if (opened) Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(editor, 4))(editor));
            if (header != 0) Marshal.Release(header);
            if (editor != 0) Marshal.Release(editor);
            if (apartment >= 0) CoUninitialize();
        }
    }

    private static byte[] Encode(bool video, int frames = 200)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirAsf-" + Guid.NewGuid() + ".wmv");
        var apartment = CoInitializeEx(0, 0);
        nint writer = 0, audio = 0, input = 0, output = 0;
        var started = false;
        try
        {
            if (apartment < 0 && apartment != unchecked((int)0x80010106)) Check(apartment);
            Check(MFStartup(0x20070, 0));
            started = true;
            Check(MFCreateSinkWriterFromURL(path, 0, 0, out writer));
            Check(MFCreateMediaType(out audio));
            SetGuid(audio, new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F"), new("73647561-0000-0010-8000-00AA00389B71"));
            SetGuid(audio, new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5"), new("00000001-0000-0010-8000-00AA00389B71"));
            Set32(audio, new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A"), 2);
            Set32(audio, new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA"), 44100);
            Set32(audio, new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669"), 16);
            Set32(audio, new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731"), 176400);
            Set32(audio, new("322DE230-9EEB-43BD-AB7A-FF412251541D"), 4);
            uint audioIndex = 0, videoIndex = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Slot(writer, 3))(writer, audio, &audioIndex));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(writer, 4))(writer, audioIndex, audio, 0));
            if (video)
            {
                Check(MFCreateMediaType(out output));
                ConfigureVideo(output, new("33564D57-0000-0010-8000-00AA00389B71")); // WMV3
                Set32(output, new("20332624-FB0D-4D9E-BD0D-CBF6786C102E"), 250_000);
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Slot(writer, 3))(writer, output, &videoIndex));
                Check(MFCreateMediaType(out input));
                ConfigureVideo(input, new("00000016-0000-0010-8000-00AA00389B71")); // RGB32
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(writer, 4))(writer, videoIndex, input, 0));
            }
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(writer, 5))(writer));
            for (var frame = 0; frame < frames; frame++)
            {
                Sample(writer, audioIndex, frame * 1_000_000L, 4410 * 4, 0);
                if (video) Sample(writer, videoIndex, frame * 1_000_000L, 320 * 240 * 4, frame < 80 ? 0x002032C8u : 0x00C83220u);
            }
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(writer, 11))(writer));
        }
        finally
        {
            if (input != 0) Marshal.Release(input);
            if (output != 0) Marshal.Release(output);
            if (audio != 0) Marshal.Release(audio);
            if (writer != 0) Marshal.Release(writer);
            if (started) Check(MFShutdown());
            if (apartment >= 0) CoUninitialize();
        }
        try { return File.ReadAllBytes(path); }
        finally { File.Delete(path); }
    }

    private static void ConfigureVideo(nint type, Guid subtype)
    {
        SetGuid(type, new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F"), new("73646976-0000-0010-8000-00AA00389B71"));
        SetGuid(type, new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5"), subtype);
        Set32(type, new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD"), 2);
        Set64(type, new("1652C33D-D6B2-4012-B834-72030849A37D"), (320ul << 32) | 240);
        Set64(type, new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0"), (10ul << 32) | 1);
        Set64(type, new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6"), (1ul << 32) | 1);
    }

    private static void Sample(nint writer, uint index, long time, int length, uint color)
    {
        nint sample = 0, buffer = 0;
        try
        {
            Check(MFCreateMemoryBuffer(length, out buffer));
            nint data = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, null, null));
            try { new Span<uint>((void*)data, length / 4).Fill(color); }
            finally { Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer)); }
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, 6))(buffer, (uint)length));
            Check(MFCreateSample(out sample));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, 42))(sample, buffer));
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 36))(sample, time));
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 38))(sample, 1_000_000));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Slot(writer, 6))(writer, index, sample));
        }
        finally
        {
            if (sample != 0) Marshal.Release(sample);
            if (buffer != 0) Marshal.Release(buffer);
        }
    }

    internal static nint Slot(nint target, int slot) => (*(nint**)target)[slot];
    internal static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    private static void Set32(nint target, Guid key, uint value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(target, 21))(target, &key, value));
    private static void Set64(nint target, Guid key, ulong value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(target, 22))(target, &key, value));
    private static void SetGuid(nint target, Guid key, Guid value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(target, 24))(target, &key, &value));
    [DllImport("wmvcore.dll", ExactSpelling = true)] private static extern int WMCreateEditor(out nint editor);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int MFCreateSinkWriterFromURL(string path, nint sink, nint attributes, out nint writer);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMemoryBuffer(int length, out nint buffer);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateSample(out nint sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
}
