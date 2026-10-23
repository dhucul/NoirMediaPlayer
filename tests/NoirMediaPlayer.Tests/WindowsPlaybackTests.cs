using System.IO;
using System.Text;
using System.Security.Cryptography;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class WindowsPlaybackTests
{
    [Theory]
    [InlineData(".mp4", true)]
    [InlineData(".WAV", true)]
    [InlineData(".mp3", true)]
    [InlineData(".mkv", true)]
    [InlineData(".vob", false)]
    [InlineData(".flac", true)]
    [InlineData(".asf", true)]
    [InlineData(".3gp", true)]
    public void Routing_OnlyAttemptsMigratedLocalFormats(string extension, bool expected)
    {
        var item = new PlaylistItem { Source = Path.Combine(Path.GetTempPath(), "NoirRouting-" + Guid.NewGuid() + extension), Title = "Test" };
        try
        {
            if (extension == ".mkv") MatroskaFixture.Write(item.Source, false);
            Assert.Equal(expected, WindowsPlaybackPolicy.CanTry(item, new PlayerSettings(), 0, null));
        }
        finally { if (extension == ".mkv") File.Delete(item.Source); }
    }

    [Fact]
    public void Routing_PreservesDiscsStreamsAndUnmigratedViewFeatures()
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirRouting-" + Guid.NewGuid() + ".mp4");
        var settings = new PlayerSettings();
        var file = new PlaylistItem { Source = path, Title = "File" };
        Assert.False(WindowsPlaybackPolicy.CanTry(new PlaylistItem { Source = path, Title = "Disc", IsDisc = true }, settings, 0, null));
        Assert.False(WindowsPlaybackPolicy.CanTry(new PlaylistItem { Source = "rtsp://example.com/video.mp4", Title = "Stream", IsNetwork = true }, settings, 0, null));
        Assert.True(WindowsPlaybackPolicy.CanTry(file, settings, 90, null));
        Assert.True(WindowsPlaybackPolicy.CanTry(file, settings, 180, null));
        Assert.True(WindowsPlaybackPolicy.CanTry(file, settings, 270, null));
        Assert.False(WindowsPlaybackPolicy.CanTry(file, settings, 45, null));
        Assert.True(WindowsPlaybackPolicy.CanTry(file, settings, 0, "16:9"));
        Assert.True(WindowsPlaybackPolicy.CanTry(file, settings, 90, "1:1"));
        Assert.False(WindowsPlaybackPolicy.CanTry(file, settings, 0, "bad:ratio"));
        settings.PreferWindowsPlayback = false;
        Assert.False(WindowsPlaybackPolicy.CanTry(file, settings, 0, null));
        settings.PreferWindowsPlayback = true;
        settings.HardwareDecoding = false;
        // The frame-server software decoder and stream inventory guard decide
        // whether this source can stay native without video hardware decoding.
        Assert.True(WindowsPlaybackPolicy.CanTry(file, settings, 0, null));
    }

    [Theory]
    [InlineData("https://example.com/video.mp4?token=AbC", true)]
    [InlineData("http://example.com/audio.wav", true)]
    [InlineData("https://example.com/video.mkv", true)] // Candidate only; startup must validate its timeline.
    [InlineData("https://user:secret@example.com/video.mp4", false)]
    [InlineData("https://example.com/live", true)] // Candidate only; content inspection rejects live/playlist inputs.
    [InlineData("https://example.com/download.php?id=AbC", true)]
    [InlineData("https://example.com/api.ashx?file=Video", true)]
    [InlineData("https://example.com/movie.bin", true)]
    [InlineData("https://example.com/movie.mpg", false)]
    [InlineData("https://example.com/playlist.m3u8", false)]
    [InlineData("udp://example.com/video.mp4", false)]
    [InlineData("rtsp://example.com/video.mp4", false)]
    public void Routing_MigratesDirectWebMediaAndPreservesOtherStreams(string source, bool expected) =>
        Assert.Equal(expected, WindowsPlaybackPolicy.CanTry(new PlaylistItem { Source = source, Title = "Network", IsNetwork = true }, new PlayerSettings(), 0, null));

    [Theory]
    [InlineData("film.srt", false)]
    [InlineData("film.en.ass", false)]
    [InlineData("FILM.fr.VTT", false)]
    [InlineData("another-film.srt", false)] // Fuzzy VLC matching must not lose captions.
    [InlineData("unrelated-title.srt", true)]
    public void Routing_KeepsAutomaticSubtitleDiscovery(string subtitle, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "NoirRouting-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var sidecar = Path.Combine(root, subtitle);
        try
        {
            File.WriteAllText(sidecar, "Test");
            Assert.Equal(expected, WindowsPlaybackPolicy.CanTry(
                new PlaylistItem { Source = Path.Combine(root, "film.mp4"), Title = "Film" }, new PlayerSettings(), 0, null));
        }
        finally { File.Delete(sidecar); Directory.Delete(root); }
    }

    [Fact]
    public async Task MediaFoundation_PlaysFlacWithoutVlc()
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirNative-" + Guid.NewGuid() + ".flac");
        // A complete first-party FLAC fixture: silent mono 16-bit constant subframes,
        // STREAMINFO, frame checksums, and the MD5 of the decoded PCM samples.
        using (var stream = File.Create(path))
        {
            void BigEndian(ulong value, int bytes)
            {
                for (var index = bytes - 1; index >= 0; index--) stream.WriteByte((byte)(value >> (index * 8)));
            }
            stream.Write(Encoding.ASCII.GetBytes("fLaC"));
            stream.Write(new byte[] { 0x80, 0, 0, 34 });
            BigEndian(4096, 2); BigEndian(4096, 2); BigEndian(0, 3); BigEndian(0, 3);
            BigEndian((8000ul << 44) | (15ul << 36) | 65536ul, 8);
            stream.Write(MD5.HashData(new byte[65536 * 2]));
            for (var frame = 0; frame < 16; frame++)
            {
                var header = new byte[] { 0xFF, 0xF8, 0xC0, 0x08, (byte)frame };
                byte crc8 = 0;
                foreach (var value in header)
                {
                    crc8 ^= value;
                    for (var bit = 0; bit < 8; bit++) crc8 = (byte)((crc8 << 1) ^ ((crc8 & 0x80) != 0 ? 7 : 0));
                }
                var encoded = header.Concat(new byte[] { crc8, 0, 0, 0 }).ToArray();
                ushort crc16 = 0;
                foreach (var value in encoded)
                {
                    crc16 ^= (ushort)(value << 8);
                    for (var bit = 0; bit < 8; bit++) crc16 = (ushort)((crc16 << 1) ^ ((crc16 & 0x8000) != 0 ? 0x8005 : 0));
                }
                stream.Write(encoded); BigEndian(crc16, 2);
            }
        }
        try
        {
            var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var player = new WindowsMediaEngine(0, mediaEvent =>
            {
                if (mediaEvent == 13) playing.TrySetResult();
                if (mediaEvent is 5 or 1014) playing.TrySetException(new InvalidOperationException("Native FLAC decoding failed."));
            });
            player.Open(path, 0, true, 1f);
            await playing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(player.HasSupportedStreams);
            Assert.True(player.HasAudio);
            Assert.InRange(player.Length, 7900, 8300); // Windows can report whole-second FLAC duration.
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MediaFoundation_PlaysSeeksPausesAndDisposesWithoutVlc()
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirNative-" + Guid.NewGuid() + ".wav");
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            const int size = 8 * 16_000;
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + size);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(size); writer.Write(new byte[size]);
        }
        try
        {
            var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var player = new WindowsMediaEngine(0, mediaEvent =>
            {
                if (mediaEvent == 13) playing.TrySetResult();
                if (mediaEvent is 5 or 1014) playing.TrySetException(new InvalidOperationException("Media Foundation could not play PCM audio."));
            });
            player.Open(path, 30, true, 1f);
            await playing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.InRange(player.Length, 7900, 8100);
            Assert.True(player.HasAudio);
            Assert.False(player.HasVideo);
            Assert.True(player.HasSupportedStreams);
            var audio = Assert.Single(player.AudioTracks);
            Assert.True(audio.Selected);
            Assert.True(player.IsSeekable);
            player.Pause();
            player.Time = 3000;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while ((!player.IsPaused || Math.Abs(player.Time - 3000) > 100) && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(player.IsPaused);
            Assert.InRange(player.Time, 2900, 3100);
            player.SetRate(1.5f);
            player.SetVolume(70);
            player.SetMuted(false);
            player.Play();
            player.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = player.Time);
        }
        finally { File.Delete(path); }
    }
}
