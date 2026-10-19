using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Notely.Core;

public sealed record SpeechResult(string Text, bool End, bool Started = false);
public static class DeepgramProtocol
{
    public static Uri Url(IEnumerable<string> keyterms) => new("wss://api.deepgram.com/v1/listen?model=nova-3&language=ru&encoding=linear16&sample_rate=16000&channels=1&punctuate=true&smart_format=true&interim_results=true&endpointing=400&utterance_end_ms=1000&vad_events=true" +
        string.Concat(keyterms.Select(t => "&keyterm=" + Uri.EscapeDataString(t))));

    public static SpeechResult? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) return null;
            if (type.ValueKind != JsonValueKind.String) return null;
            if (type.GetString() == "Error") throw new IOException("Deepgram: ошибка распознавания, проверьте ключ и баланс");
            if (type.GetString() == "SpeechStarted") return new("", false, Started: true);
            if (type.GetString() == "UtteranceEnd")
            {
                if (root.TryGetProperty("last_word_end", out var last) &&
                    (last.ValueKind != JsonValueKind.Number || !last.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds < 0)) return null;
                return new("", true);
            }
            if (type.GetString() != "Results" || !root.TryGetProperty("is_final", out var final) || final.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            var end = root.TryGetProperty("speech_final", out var sf) && sf.ValueKind == JsonValueKind.True;
            if (!root.TryGetProperty("channel", out var ch) || ch.ValueKind != JsonValueKind.Object ||
                !ch.TryGetProperty("alternatives", out var alts) || alts.ValueKind != JsonValueKind.Array || alts.GetArrayLength() == 0 ||
                alts[0].ValueKind != JsonValueKind.Object || !alts[0].TryGetProperty("transcript", out var transcript) || transcript.ValueKind != JsonValueKind.String) return null;
            var text = transcript.GetString() ?? "";
            // Interim words are unstable: signal resumed speech without adding them to context.
            if (final.ValueKind == JsonValueKind.False) return string.IsNullOrWhiteSpace(text) ? null : new("", false, Started: true);
            return text.Length > 0 || end ? new(text, end) : null;
        }
        catch (JsonException) { return null; }
    }
}

public sealed class DeepgramStream(string key, string[] keyterms) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private Channel<(byte[] Data, WebSocketMessageType Type)>? queue;
    private Task? worker;
    public event Action<SpeechResult>? Speech;
    public event Action<bool, string>? Status;

    public void Start() { lock (gate) worker ??= Task.Run(RunAsync); }
    public void Send(byte[] pcm)
    {
        lock (gate) queue?.Writer.TryWrite((pcm, WebSocketMessageType.Binary));
    }

    private async Task RunAsync()
    {
        var delay = 2;
        while (!lifetime.IsCancellationRequested)
        {
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            using var socket = new ClientWebSocket();
            var outgoing = Channel.CreateBounded<(byte[] Data, WebSocketMessageType Type)>(new BoundedChannelOptions(40)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            try
            {
                socket.Options.SetRequestHeader("Authorization", "Token " + key);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
                { timeout.CancelAfter(TimeSpan.FromSeconds(15)); await socket.ConnectAsync(DeepgramProtocol.Url(keyterms), timeout.Token); }
                lock (gate) queue = outgoing;
                Status?.Invoke(true, "подключено"); delay = 2;
                var sender = SendAsync(socket, outgoing.Reader, connection.Token);
                var receiver = ReceiveAsync(socket, connection.Token);
                var keepalive = KeepAliveAsync(outgoing.Writer, connection.Token);
                var first = await Task.WhenAny(sender, receiver, keepalive);
                connection.Cancel(); socket.Abort();
                try { await Task.WhenAll(sender, receiver, keepalive); }
                catch (Exception) { if (!lifetime.IsCancellationRequested) await first; }
            }
            catch (Exception)
            { if (!lifetime.IsCancellationRequested) Status?.Invoke(false, "нет связи с Deepgram — повторное подключение"); }
            finally
            {
                connection.Cancel(); outgoing.Writer.TryComplete();
                lock (gate) if (queue == outgoing) queue = null;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), lifetime.Token); }
            catch (OperationCanceledException) { break; }
            delay = Math.Min(20, delay * 2);
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, ChannelReader<(byte[] Data, WebSocketMessageType Type)> reader, CancellationToken token)
    {
        await foreach (var item in reader.ReadAllAsync(token))
            await socket.SendAsync(item.Data.AsMemory(), item.Type, true, token);
    }

    private static async Task KeepAliveAsync(ChannelWriter<(byte[] Data, WebSocketMessageType Type)> writer, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        var data = Encoding.UTF8.GetBytes("{\"type\":\"KeepAlive\"}");
        while (await timer.WaitForNextTickAsync(token)) writer.TryWrite((data, WebSocketMessageType.Text));
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (!token.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Deepgram закрыл соединение");
            message.Write(buffer, 0, result.Count);
            if (message.Length > 1_000_000) throw new IOException("Слишком большое сообщение Deepgram");
            if (!result.EndOfMessage) continue;
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var parsed = DeepgramProtocol.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                if (parsed is not null) Speech?.Invoke(parsed);
            }
            message.SetLength(0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (worker is not null) await worker;
        lifetime.Dispose();
    }
}
