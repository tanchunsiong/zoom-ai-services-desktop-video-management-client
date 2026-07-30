namespace ZTranscribe.Core.Services;

public sealed class Pcm16FrameAssembler
{
    public const int DefaultFrameBytes = 16_000 * 2 / 10;
    private readonly byte[] _pending;
    private int _pendingCount;

    public Pcm16FrameAssembler(int frameBytes = DefaultFrameBytes)
    {
        if (frameBytes <= 0 || frameBytes % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(frameBytes), "PCM16 frame size must be a positive even number.");
        _pending = new byte[frameBytes];
    }

    public IReadOnlyList<byte[]> Append(byte[] source, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > source.Length) throw new ArgumentException("The audio range exceeds the source buffer.");
        if (count % 2 != 0) throw new ArgumentException("PCM16 audio must contain complete two-byte samples.", nameof(count));

        var frames = new List<byte[]>();
        while (count > 0)
        {
            var copy = Math.Min(count, _pending.Length - _pendingCount);
            Buffer.BlockCopy(source, offset, _pending, _pendingCount, copy);
            offset += copy;
            count -= copy;
            _pendingCount += copy;
            if (_pendingCount != _pending.Length) continue;

            frames.Add((byte[])_pending.Clone());
            _pendingCount = 0;
        }
        return frames;
    }

    public byte[]? Drain()
    {
        if (_pendingCount == 0) return null;
        var remainder = _pending[.._pendingCount];
        _pendingCount = 0;
        return remainder;
    }
}
