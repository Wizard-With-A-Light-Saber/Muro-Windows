using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Muro.Core.Library;

/// A short identity for a video file: its size and a hash of its first and last megabyte. Enough to
/// tell the same file imported twice from two different ones without reading gigabytes. Same recipe
/// as the macOS app, so a fingerprint means the same thing on both.
public static class Fingerprint
{
    private const long Chunk = 1 << 20;

    public static string? Of(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var size = stream.Length;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var sizeBytes = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(sizeBytes, (ulong)size);
            hash.AppendData(sizeBytes);

            var head = new byte[Math.Min(Chunk, size)];
            stream.ReadExactly(head);
            hash.AppendData(head);

            if (size > Chunk)
            {
                stream.Seek(size - Math.Min(Chunk, size - Chunk), SeekOrigin.Begin);
                var tail = new byte[size - stream.Position];
                stream.ReadExactly(tail);
                hash.AppendData(tail);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch { return null; }
    }
}
