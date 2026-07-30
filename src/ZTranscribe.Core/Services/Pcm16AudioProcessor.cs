using System.Buffers.Binary;

namespace ZTranscribe.Core.Services;

public sealed record Pcm16LevelReading(
    double PeakDbfs,
    double RmsDbfs,
    bool IsClipping,
    double AppliedGain);

public sealed class Pcm16AudioProcessor
{
    private const double TargetRms = 0.1259; // -18 dBFS
    private const double MaximumPeak = 0.8913; // -1 dBFS
    private const double SilenceFloor = 0.0018; // About -55 dBFS
    private const double MinimumGain = 0.25;
    private const double MaximumGain = 8;
    private double _gain = 1;

    public Pcm16LevelReading ProcessInPlace(
        byte[] buffer,
        int offset,
        int count,
        bool automaticGain)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length) throw new ArgumentException("The audio range exceeds the source buffer.");
        if (count == 0)
            return new Pcm16LevelReading(
                double.NegativeInfinity,
                double.NegativeInfinity,
                false,
                _gain);
        if (count % 2 != 0)
            throw new ArgumentException("PCM16 audio must contain complete two-byte samples.", nameof(count));

        var sampleCount = count / 2;
        double inputSquares = 0;
        var inputPeak = 0;
        for (var index = offset; index < offset + count; index += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(index, 2));
            var magnitude = Math.Abs((int)sample);
            inputPeak = Math.Max(inputPeak, magnitude);
            inputSquares += (double)sample * sample;
        }

        var inputRms = Math.Sqrt(inputSquares / sampleCount) / 32_768d;
        var inputPeakLinear = inputPeak / 32_768d;
        if (!automaticGain)
        {
            _gain = 1;
        }
        else if (inputRms >= SilenceFloor)
        {
            var rmsGain = TargetRms / inputRms;
            var peakLimitedGain = inputPeakLinear > 0 ? MaximumPeak / inputPeakLinear : MaximumGain;
            var desiredGain = Math.Clamp(Math.Min(rmsGain, peakLimitedGain), MinimumGain, MaximumGain);
            var smoothing = desiredGain < _gain ? 0.65 : 0.25;
            _gain += (desiredGain - _gain) * smoothing;
            _gain = Math.Min(_gain, peakLimitedGain);
        }
        else
        {
            _gain += (1 - _gain) * 0.1;
        }

        double outputSquares = 0;
        var outputPeak = 0;
        var clipping = inputPeak >= 32_767;
        for (var index = offset; index < offset + count; index += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(index, 2));
            var scaled = (int)Math.Round(sample * _gain);
            if (scaled is >= short.MaxValue or <= short.MinValue) clipping = true;
            var output = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(index, 2), output);
            var magnitude = Math.Abs((int)output);
            outputPeak = Math.Max(outputPeak, magnitude);
            outputSquares += (double)output * output;
        }

        var outputRms = Math.Sqrt(outputSquares / sampleCount) / 32_768d;
        var outputPeakLinear = outputPeak / 32_768d;
        clipping |= outputPeak >= 32_767;
        return new Pcm16LevelReading(
            ToDbfs(outputPeakLinear),
            ToDbfs(outputRms),
            clipping,
            _gain);
    }

    public static double NormalizeMeter(double peakDbfs) =>
        Math.Clamp((peakDbfs + 60) / 60, 0, 1);

    private static double ToDbfs(double linear) =>
        linear <= 0 ? double.NegativeInfinity : 20 * Math.Log10(linear);
}
