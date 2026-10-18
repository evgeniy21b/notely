using NAudio.CoreAudioApi;
using NAudio.Wave;
using Notely.Core;

namespace Notely.Windows;

internal sealed record AudioDevice(string Id, string Name);
internal sealed class AudioService(Settings settings) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private Task? worker;
    public event Action<bool, byte[], float>? Pcm;
    public event Action<bool, string>? Status;

    public static AudioDevice[] Devices(bool output)
    {
        using var enumerator = new MMDeviceEnumerator();
        var list = new List<AudioDevice> { new("", "По умолчанию Windows") };
        foreach (var device in enumerator.EnumerateAudioEndPoints(output ? DataFlow.Render : DataFlow.Capture, DeviceState.Active))
        { using (device) list.Add(new(device.ID, device.FriendlyName)); }
        return list.ToArray();
    }

    public void Start() => worker ??= Task.WhenAll(Task.Run(() => WatchAsync(true)), Task.Run(() => WatchAsync(false)));

    private async Task WatchAsync(bool output)
    {
        var token = lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var selected = output ? settings.OutputDevice : settings.InputDevice;
                using var device = string.IsNullOrEmpty(selected)
                    ? enumerator.GetDefaultAudioEndpoint(output ? DataFlow.Render : DataFlow.Capture, Role.Multimedia)
                    : enumerator.GetDevice(selected);
                if (device.State != DeviceState.Active) throw new IOException("Устройство недоступно");
                using WasapiCapture capture = output ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
                var format = capture.WaveFormat;
                var floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                    format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
                var encoder = new PcmEncoder(format.SampleRate, format.Channels, format.BitsPerSample, floating);
                var stopped = 0;
                capture.DataAvailable += (_, e) =>
                {
                    if (token.IsCancellationRequested || Volatile.Read(ref stopped) != 0) return;
                    try
                    {
                        var converted = encoder.Convert(e.Buffer.AsSpan(0, e.BytesRecorded));
                        if (converted.Data.Length > 0) Pcm?.Invoke(output, converted.Data, converted.Level);
                    }
                    catch (Exception) { Interlocked.Exchange(ref stopped, 1); }
                };
                capture.RecordingStopped += (_, _) => Interlocked.Exchange(ref stopped, 1);
                capture.StartRecording();
                Status?.Invoke(output, device.FriendlyName);
                Log.Write(output ? "system-audio" : "microphone", $"started {format.SampleRate}Hz {format.Channels}ch {format.BitsPerSample}bit float={floating}");
                try
                {
                    while (!token.IsCancellationRequested && Volatile.Read(ref stopped) == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), token);
                        if (device.State != DeviceState.Active) break;
                        if (string.IsNullOrEmpty(selected))
                        {
                            using var current = enumerator.GetDefaultAudioEndpoint(output ? DataFlow.Render : DataFlow.Capture, Role.Multimedia);
                            if (current.ID != device.ID) break;
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref stopped, 1);
                    capture.StopRecording();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception e) { Log.Write(output ? "system-audio" : "microphone", "capture failed: " + e.GetType().Name); }
            if (!token.IsCancellationRequested) Status?.Invoke(output, "недоступно — повтор через 2 с");
            try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (worker is not null) await worker;
        lifetime.Dispose();
    }
}
