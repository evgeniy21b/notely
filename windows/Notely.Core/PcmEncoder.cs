using System.Buffers.Binary;

namespace Notely.Core;

// Continuous conversion across callback boundaries. Input may be native WASAPI float32/PCM.
public sealed class PcmEncoder
{
    private readonly int rate, channels, bits, bytesPerFrame;
    private readonly bool floating;
    private double nextPosition;
    private long position;
    private float previous;
    private bool hasPrevious;
    public PcmEncoder(int sampleRate, int channels, int bits, bool floating)
    {
        if (sampleRate is < 8000 or > 384000 || channels is < 1 or > 32 ||
            (floating ? bits != 32 : bits is not (16 or 24 or 32))) throw new ArgumentException("Неподдерживаемый формат PCM");
        rate = sampleRate; this.channels = channels; this.bits = bits; this.floating = floating;
        bytesPerFrame = channels * (bits / 8);
    }

    public (byte[] Data, float Level) Convert(ReadOnlySpan<byte> input)
    {
        if (input.Length % bytesPerFrame != 0) throw new ArgumentException("Неполный аудиокадр");
        var frames = input.Length / bytesPerFrame;
        var output = new List<byte>((int)((long)frames * 32000 / rate + 8));
        double sum = 0;
        for (var f = 0; f < frames; f++)
        {
            float mono = 0;
            for (var c = 0; c < channels; c++)
            {
                var sample = input.Slice(f * bytesPerFrame + c * (bits / 8), bits / 8);
                var v = floating ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample)) : bits switch
                {
                    16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                    24 => ((sample[0] << 8 | sample[1] << 16 | sample[2] << 24) >> 8) / 8388608f,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f
                };
                mono += float.IsFinite(v) ? Math.Clamp(v, -1, 1) / channels : 0;
            }
            sum += mono * mono;
            if (!hasPrevious) { previous = mono; hasPrevious = true; }
            while (nextPosition <= position)
            {
                var fraction = Math.Clamp(nextPosition - (position - 1), 0, 1);
                var value = previous + (mono - previous) * fraction;
                var pcm = (short)Math.Clamp(Math.Round(value * 32767), short.MinValue, short.MaxValue);
                output.Add((byte)(pcm & 255)); output.Add((byte)((pcm >> 8) & 255));
                nextPosition += rate / 16000.0;
            }
            previous = mono; position++;
        }
        return (output.ToArray(), frames == 0 ? 0 : (float)Math.Min(1, Math.Sqrt(sum / frames) * 8));
    }
}
