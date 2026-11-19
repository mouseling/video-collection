using System.Security.Cryptography;

namespace Vcm.Core;

/// <summary>
/// Identifies a video by its content so statistics survive renames and moves, and so identical
/// copies on different drives collapse to one entry.
///
/// Hashing a 70 GB collection end to end would make every scan unbearable, so this hashes the
/// file size plus the first and last chunk. For real video files — which differ in their headers,
/// their stream data and their length — that is decisive, and it costs two seeks rather than a
/// full read. Files small enough to read outright are hashed whole.
/// </summary>
public static class ContentHasher
{
    public const int ChunkSize = 1 << 20;          // 1 MiB from each end
    private const long FullHashBelow = 4L << 20;   // under 4 MiB, just hash everything

    public static string Compute(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                          bufferSize: 1 << 16, FileOptions.SequentialScan);
        return Compute(stream, stream.Length);
    }

    public static string Compute(Stream stream, long length)
    {
        using var sha = SHA256.Create();

        Span<byte> sizeBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(sizeBytes, length);
        sha.TransformBlock(sizeBytes.ToArray(), 0, 8, null, 0);

        var buffer = new byte[ChunkSize];

        if (length <= FullHashBelow)
        {
            stream.Position = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                sha.TransformBlock(buffer, 0, read, null, 0);
        }
        else
        {
            ReadExactlyInto(stream, 0, buffer, sha);
            ReadExactlyInto(stream, length - ChunkSize, buffer, sha);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static void ReadExactlyInto(Stream stream, long offset, byte[] buffer, SHA256 sha)
    {
        stream.Position = offset;
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total > 0) sha.TransformBlock(buffer, 0, total, null, 0);
    }
}
