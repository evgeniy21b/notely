using System.Text;
using System.Text.RegularExpressions;

namespace Notely.Core;

public enum BrainEventKind { Thinking, Answer, Done, Skipped, Error }
public sealed record BrainEvent(long Session, long Request, BrainEventKind Kind, string Text = "", bool StartsNew = false);
public sealed record ContextEntry(DateTimeOffset Time, string Kind, string Text);

// All state and events share one lock. Late network callbacks can never replace a newer request/session.
public sealed class Brain(ICompletionClient client, Func<string> profile, Func<string> model, TimeProvider? clock = null) : IDisposable
{
    private readonly object gate = new();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly List<ContextEntry> entries = [];
    private readonly List<string> pending = [];
    private readonly List<(DateTimeOffset Time, HashSet<string> Words)> recentThem = [];
    private readonly StringBuilder themBuffer = new(), meBuffer = new();
    private string summary = "";
    private int summarizedUpTo;
    private long session = 1, requestId;
    private RequestState? active;
    private CancellationTokenSource? summaryCancellation;
    private bool paused, disposed;
    private sealed class RequestState(long id)
    {
        public long Id = id;
        public CancellationTokenSource Cancellation = new();
        public string Text = "";
        public bool Visible, Recorded;
    }

    public event Action<BrainEvent>? Changed;
    public long Session { get { lock (gate) return session; } }
    public bool Paused
    {
        get { lock (gate) return paused; }
        set { lock (gate) { paused = value; themBuffer.Clear(); meBuffer.Clear(); } }
    }
    public ContextEntry[] Snapshot() { lock (gate) return entries.ToArray(); }
    public void DiscardSpeechBuffer(bool them) { lock (gate) (them ? themBuffer : meBuffer).Clear(); }

    public void Speech(bool them, string text, bool end)
    {
        lock (gate)
        {
            if (disposed || paused) return;
            var buffer = them ? themBuffer : meBuffer;
            if (!string.IsNullOrWhiteSpace(text)) { if (buffer.Length > 0) buffer.Append(' '); buffer.Append(text.Trim()); }
            if (buffer.Length > 20_000) buffer.Remove(0, buffer.Length - 20_000);
            if (!end) return;
            var final = buffer.ToString().Trim(); buffer.Clear();
            if (final.Length == 0) return;
            var now = clock.GetUtcNow();
            recentThem.RemoveAll(x => now - x.Time > TimeSpan.FromSeconds(25));
            if (!them)
            {
                var words = Words(final);
                var echo = recentThem.SelectMany(x => x.Words).ToHashSet();
                if (words.Length > 0 && words.Count(echo.Contains) / (double)words.Length >= 0.6) return;
            }
            Add(them ? "Собеседник" : "Я", final);
            if (!them) return;
            recentThem.Add((now, Words(final).ToHashSet()));
            if (final.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3) return;
            pending.Add(final);
            if (pending.Count > 12) pending.RemoveAt(0);
            Start(true, "", null);
        }
    }

    public void Ask(string text, byte[]? image = null)
    {
        lock (gate)
        {
            if (disposed) return;
            text = text.Trim();
            if (text.Length == 0 && image is not { Length: > 0 }) return;
            if (text.Length > 20_000) throw new ArgumentException("Вопрос слишком длинный (максимум 20 000 символов)");
            if (image is { Length: > 8_000_000 }) throw new ArgumentException("Изображение слишком большое");
            Add(image is null ? "Я (написал тебе)" : "Скриншот", text.Length > 0 ? text : "кандидат снял экран");
            Start(false, text, image);
        }
    }

    private void Start(bool auto, string text, byte[]? image)
    {
        if (active is { } old) { Record(old); old.Cancellation.Cancel(); }
        var request = new RequestState(++requestId);
        active = request;
        var generation = session;
        var prompt = Prompts.System;
        var userProfile = profile();
        if (userProfile.Length > 0) prompt += "\n\nПрофиль кандидата:\n" + userProfile;
        var context = (summary.Length > 0 ? "Конспект начала сессии:\n" + summary + "\n\nПоследние минуты:\n" : "Разговор с начала сессии:\n") +
            string.Join('\n', entries.Skip(summarizedUpTo).Select(Line));
        context += auto
            ? "\n\nНовые реплики без подсказки:\n«" + string.Join(' ', pending) + "»\n" + Prompts.Auto
            : image is not null
                ? "\n\nКандидат снял экран и пишет: «" + text + "». Свяжи изображение с разговором. Начни строкой «Экран: …» (до 20 слов), затем решение."
                : "\n\nКандидат пишет тебе: «" + text + "». Ответь с учётом разговора.";
        // Only this immutable request owns the image; later requests contain the textual answer.
        var body = new CompletionRequest(model(), prompt, context, image?.ToArray());
        if (!auto) Emit(request, BrainEventKind.Thinking);
        _ = Task.Run(() => RunAsync(request, generation, body, auto));
    }

    private async Task RunAsync(RequestState request, long generation, CompletionRequest body, bool auto)
    {
        var text = new StringBuilder();
        try
        {
            await foreach (var chunk in client.StreamAsync(body, request.Cancellation.Token))
            {
                lock (gate)
                {
                    if (active != request || generation != session || disposed) return;
                    text.Append(chunk);
                    if (text.Length > 100_000) throw new IOException("Ответ превысил допустимый размер");
                    var head = text.ToString().TrimStart();
                    if (auto && !request.Visible && "SKIP".StartsWith(head, StringComparison.OrdinalIgnoreCase)) continue;
                    if (auto && !request.Visible && head.StartsWith("SKIP", StringComparison.OrdinalIgnoreCase))
                    { active = null; Emit(request, BrainEventKind.Skipped); return; }
                    var startsNew = !request.Visible;
                    request.Text = text.ToString(); request.Visible = true;
                    if (startsNew && auto) pending.Clear();
                    Emit(request, BrainEventKind.Answer, request.Text, startsNew);
                }
            }
            lock (gate)
            {
                if (active != request || generation != session || disposed) return;
                Record(request); active = null;
                Emit(request, request.Visible ? BrainEventKind.Done : auto ? BrainEventKind.Skipped : BrainEventKind.Error,
                    request.Visible ? request.Text : auto ? "" : "Модель вернула пустой ответ");
            }
        }
        catch (Exception e)
        {
            lock (gate)
            {
                if (active != request || generation != session || disposed) return;
                Record(request); active = null;
                Emit(request, BrainEventKind.Error, e is OperationCanceledException ? "Истекло время ожидания ответа" : e.Message);
            }
        }
        finally { request.Cancellation.Dispose(); }
    }

    private void Emit(RequestState request, BrainEventKind kind, string text = "", bool startsNew = false) =>
        Changed?.Invoke(new BrainEvent(session, request.Id, kind, text, startsNew));
    private static string[] Words(string text) => Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{2,}").Select(m => m.Value).ToArray();
    private static string Line(ContextEntry e) => $"[{e.Time.ToLocalTime():HH:mm}] {e.Kind}: {e.Text}";

    private void Record(RequestState request)
    {
        if (!request.Visible || request.Recorded || string.IsNullOrWhiteSpace(request.Text)) return;
        request.Recorded = true;
        Add("Твоя подсказка", request.Text[..Math.Min(request.Text.Length, 6000)]);
    }

    private void Add(string kind, string text)
    {
        entries.Add(new ContextEntry(clock.GetUtcNow(), kind, text));
        MaybeSummarize();
    }

    private void MaybeSummarize()
    {
        if (summaryCancellation is not null || disposed) return;
        var cut = summarizedUpTo;
        while (cut < entries.Count - 20 && clock.GetUtcNow() - entries[cut].Time > TimeSpan.FromMinutes(12)) cut++;
        if (cut - summarizedUpTo < 15) return;
        var generation = session;
        var prompt = "Обнови конспект собеседования, добавив новый кусок. Сохрани точно задачи, вопросы, таблицы, поля, цифры, ограничения, содержание экранов, ответы кандидата, подсказки и открытые вопросы. Удали small talk. До 600 слов.\n\nКонспект:\n" + summary +
            "\n\nНовый кусок:\n" + string.Join('\n', entries.Skip(summarizedUpTo).Take(cut - summarizedUpTo).Select(Line));
        var cancellation = new CancellationTokenSource();
        summaryCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = new StringBuilder();
                await foreach (var chunk in client.StreamAsync(new CompletionRequest(model(), "Ты ведёшь точный конспект разговора.", prompt, Summary: true), cancellation.Token))
                { result.Append(chunk); if (result.Length > 20_000) throw new IOException("Слишком длинный конспект"); }
                lock (gate)
                {
                    if (generation == session && !disposed && result.Length > 0)
                    {
                        summary = result.ToString();
                        // Snapshot copied before async work; reset/late callback cannot index the new session.
                        entries.RemoveRange(0, cut); summarizedUpTo = 0;
                    }
                }
            }
            catch (Exception) { /* Preserve verbatim context if compression fails. */ }
            finally
            {
                lock (gate) { if (summaryCancellation == cancellation) summaryCancellation = null; }
                cancellation.Dispose();
            }
        });
    }

    public void Reset()
    {
        lock (gate)
        {
            session++;
            active?.Cancellation.Cancel(); active = null;
            summaryCancellation?.Cancel(); summaryCancellation = null;
            entries.Clear(); pending.Clear(); recentThem.Clear(); themBuffer.Clear(); meBuffer.Clear();
            summary = ""; summarizedUpTo = 0;
        }
    }

    public void Dispose() { lock (gate) { Reset(); disposed = true; } }
}
