using System.Collections.Concurrent;
using System.Security.Cryptography;
using OpenMacroBoard.SDK;

namespace StreamDeckPilot.Infrastructure.Rendering;

/// <summary>A key image as last sent to the hardware, with a content hash of its pixels.</summary>
public sealed record PaintedFrame(KeyBitmap Bitmap, string Hash);

/// <summary>
/// The last frame the renderer sent to each physical key (#27): what the deck shows right now,
/// paint-time looks included (offline overlay, press feedback, blink phase). One entry per
/// (serial, key), so memory is bounded by the number of keys. The live preview reads it, and
/// <see cref="Painted"/> lets a live view push only the keys whose content hash changed (#108).
/// </summary>
public sealed class PaintedFrameStore
{
    private readonly ConcurrentDictionary<(string Serial, int KeyIndex), PaintedFrame> _frames = new();

    /// <summary>Raised after a key was painted with different content: (serial, key index, frame).</summary>
    public event Action<string, int, PaintedFrame>? Painted;

    public void Record(string serial, int keyIndex, KeyBitmap bitmap)
    {
        // The renderer hands out the same KeyBitmap instance for a cached blink frame; skip the hash then.
        if (_frames.TryGetValue((serial, keyIndex), out var previous) && ReferenceEquals(previous.Bitmap, bitmap))
            return;

        var frame = new PaintedFrame(bitmap, HashOf(bitmap));
        _frames[(serial, keyIndex)] = frame;
        if (previous?.Hash != frame.Hash)
            Painted?.Invoke(serial, keyIndex, frame);
    }

    public PaintedFrame? Get(string serial, int keyIndex) =>
        _frames.TryGetValue((serial, keyIndex), out var frame) ? frame : null;

    /// <summary>Content hash of a key image: 16 hex characters over its size and BGR pixels.</summary>
    public static string HashOf(KeyBitmap bitmap)
    {
        var access = (IKeyBitmapDataAccess)bitmap;
        if (access.IsEmpty) return "black";   // KeyBitmap.Black carries no pixel data

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[8];
        BitConverter.TryWriteBytes(size, access.Width);
        BitConverter.TryWriteBytes(size[4..], access.Height);
        sha.AppendData(size);
        sha.AppendData(access.GetData());
        return Convert.ToHexString(sha.GetHashAndReset(), 0, 8).ToLowerInvariant();
    }
}
