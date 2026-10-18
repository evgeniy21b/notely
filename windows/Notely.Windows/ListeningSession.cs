using System.Diagnostics;
using Notely.Core;

namespace Notely.Windows;

internal sealed class ListeningSession : IAsyncDisposable
{
    private readonly AudioService audio;
    private readonly DeepgramStream them, me;
    private readonly Brain brain;
    private readonly long generation;
    private long themActiveUntil;
    private volatile bool stopping;
    private readonly LoopbackSilence silence = new();
    private readonly CancellationTokenSource lifetime = new();
    private Task? silenceWorker;
    public event Action<bool, float>? Level;
    public event Action<string>? Status;
    private readonly string[] statuses = ["системный звук: запуск", "микрофон: запуск", "Deepgram собеседник: подключение", "Deepgram я: подключение"];
    private readonly object gate = new();

    public ListeningSession(ApiKeys keys, Settings settings, Brain brain)
    {
        this.brain = brain; generation = brain.Session;
        audio = new AudioService(settings);
        them = new DeepgramStream(keys.Deepgram, Paths.Keyterms());
        me = new DeepgramStream(keys.Deepgram, Paths.Keyterms());
        them.Speech += result => { if (Current) brain.Speech(true, result.Text, result.End); };
        me.Speech += result => { if (Current) brain.Speech(false, result.Text, result.End); };
        them.Status += (connected, text) => { if (!connected && Current) brain.DiscardSpeechBuffer(true); Update(2, "Deepgram собеседник: " + text); };
        me.Status += (connected, text) => { if (!connected && Current) brain.DiscardSpeechBuffer(false); Update(3, "Deepgram я: " + text); };
        audio.Status += (output, text) => Update(output ? 0 : 1, (output ? "системный звук: " : "микрофон: ") + text);
        audio.Pcm += (output, data, level) =>
        {
            if (!Current || brain.Paused) return;
            Level?.Invoke(output, level);
            if (output)
            {
                silence.AudioArrived();
                if (level > 0.035f) Interlocked.Exchange(ref themActiveUntil, Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.6));
                them.Send(data);
            }
            else me.Send(Stopwatch.GetTimestamp() < Interlocked.Read(ref themActiveUntil) ? new byte[data.Length] : data);
        };
    }
    private bool Current => !stopping && brain.Session == generation;
    private void Update(int index, string text)
    {
        lock (gate)
        {
            if (!Current) return;
            statuses[index] = text;
            Status?.Invoke(string.Join("\n", statuses));
        }
    }
    public void Start()
    {
        them.Start(); me.Start(); audio.Start();
        silenceWorker = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                while (await timer.WaitForNextTickAsync(lifetime.Token))
                    if (Current && !brain.Paused && silence.Poll() is { } data) them.Send(data);
            }
            catch (OperationCanceledException) { }
        });
    }
    public async ValueTask DisposeAsync()
    {
        stopping = true;
        lifetime.Cancel();
        if (silenceWorker is not null) await silenceWorker;
        await audio.DisposeAsync();
        await Task.WhenAll(them.DisposeAsync().AsTask(), me.DisposeAsync().AsTask());
        lifetime.Dispose();
    }
}
