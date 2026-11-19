using LibVLCSharp.Shared;
using Vcm.Core;

// Vcm.Media shadows LibVLCSharp's Media type from inside this namespace.
using VlcMedia = LibVLCSharp.Shared.Media;

namespace Vcm.Media;

/// <summary>
/// Reads duration, resolution and codecs straight out of the file via libvlc, which is already
/// present for playback. That is what keeps ffmpeg out of the dependency list entirely.
/// </summary>
public sealed class MediaProbe
{
    private readonly LibVLC _vlc;

    public MediaProbe(LibVLC vlc) => _vlc = vlc;

    public sealed record ProbeResult(long DurationMs, int Width, int Height,
                                     string? VideoCodec, string? AudioCodec);

    public async Task<ProbeResult?> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;

        // Built from the path rather than a file:// Uri so that spaces, brackets and other
        // characters that are legal in filenames but awkward in URLs are handed over verbatim.
        var media = new VlcMedia(_vlc, Path.GetFullPath(path), FromType.FromPath);
        try
        {
            var status = await media.Parse(MediaParseOptions.ParseLocal, timeout: 10_000,
                                           cancellationToken: ct);
            if (status != MediaParsedStatus.Done) return null;

            int width = 0, height = 0;
            string? videoCodec = null, audioCodec = null;

            foreach (var track in media.Tracks)
            {
                switch (track.TrackType)
                {
                    case TrackType.Video when width == 0:
                        width = (int)track.Data.Video.Width;
                        height = (int)track.Data.Video.Height;
                        videoCodec = FourCc(track.Codec);
                        break;
                    case TrackType.Audio when audioCodec is null:
                        audioCodec = FourCc(track.Codec);
                        break;
                }
            }

            return new ProbeResult(media.Duration, width, height, videoCodec, audioCodec);
        }
        finally
        {
            // Never dispose here — see MediaReaper for why that crashes the process.
            MediaReaper.Retire(media);
        }
    }

    /// <summary>Codecs come back as packed FourCC integers; turn them into something readable.</summary>
    private static string FourCc(uint codec)
    {
        Span<char> chars =
        [
            (char)(codec & 0xFF),
            (char)((codec >> 8) & 0xFF),
            (char)((codec >> 16) & 0xFF),
            (char)((codec >> 24) & 0xFF),
        ];

        Span<char> clean = stackalloc char[4];
        var n = 0;
        foreach (var c in chars)
            if (!char.IsControl(c) && c != ' ') clean[n++] = c;

        return n == 0 ? codec.ToString() : new string(clean[..n]).Trim();
    }

    /// <summary>Probes everything in the library that has not been read yet.</summary>
    public async Task<int> ProbePendingAsync(Library library, IProgress<string>? progress = null,
                                             CancellationToken ct = default)
    {
        var pending = library.GetUnprobed();
        var done = 0;

        foreach (var (id, path) in pending)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(Path.GetFileName(path));

            try
            {
                var result = await ProbeAsync(path, ct);
                if (result is null)
                {
                    // Mark it read anyway, otherwise an unreadable file is retried on every scan.
                    library.SaveProbe(id, 0, 0, 0, null, null);
                    continue;
                }

                library.SaveProbe(id, result.DurationMs, result.Width, result.Height,
                                  result.VideoCodec, result.AudioCodec);
                done++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                library.SaveProbe(id, 0, 0, 0, null, null);
            }
        }

        return done;
    }
}
