namespace Notely.Core;

// WASAPI loopback sends no callbacks when the device is silent. Feed silence so STT can end a turn.
public sealed class LoopbackSilence(TimeProvider? provider = null)
{
    private readonly TimeProvider clock = provider ?? TimeProvider.System;
    private readonly object gate = new();
    private DateTimeOffset lastAudio = DateTimeOffset.MinValue, lastSilence = DateTimeOffset.MinValue;
    public void AudioArrived() { lock (gate) lastAudio = clock.GetUtcNow(); }
    public byte[]? Poll()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (now - lastAudio < TimeSpan.FromMilliseconds(200) || now - lastSilence < TimeSpan.FromMilliseconds(95)) return null;
            lastSilence = now;
            return new byte[3200]; // 100 ms, 16kHz mono PCM16.
        }
    }
}
