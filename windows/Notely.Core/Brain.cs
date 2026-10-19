using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Notely.Tests")]

namespace Notely.Core;

public enum BrainEventKind { Thinking, Answer, Done, Skipped, Error }
public sealed record BrainEvent(long Session, long Request, BrainEventKind Kind, string Text = "", bool StartsNew = false);
public sealed record ContextEntry(DateTimeOffset Time, string Kind, string Text);

public sealed class Brain(ICompletionClient client, Func<string> profile, Func<string> model, TimeProvider? clock = null) : IDisposable
{
    private readonly object gate = new();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly List<ContextEntry> entries = [];
    private readonly List<(long Id, string Text)> pending = [];
    private readonly List<(DateTimeOffset Time, string[] Words)> recentThem = [];
    private readonly StringBuilder themBuffer = new(), meBuffer = new();
    private readonly Channel<BrainEvent> notifications = Channel.CreateUnbounded<BrainEvent>(new UnboundedChannelOptions { SingleReader = true });
    private Task? notificationWorker;
    private string summary = "";
    private long session = 1, requestId, speechId;
    private RequestState? active, candidate;
    private CancellationTokenSource? summaryCancellation;
    private DateTimeOffset nextSummaryAttempt = DateTimeOffset.MinValue;
    private int summaryFailures;
    private bool paused, disposed;
    internal Task SummaryWork { get; private set; } = Task.CompletedTask;
    internal Task AutoWork { get; private set; } = Task.CompletedTask;
    internal Task AutoDelayReady { get { lock (gate) return candidate?.DelayReady.Task ?? Task.CompletedTask; } }

    private sealed class RequestState(long id, long session, bool auto, long pendingThrough)
    {
        public readonly long Id = id, Session = session, PendingThrough = pendingThrough;
        public readonly bool Auto = auto;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource DelayReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Text = "";
        public bool Visible, Recorded;
    }

    public event Action<BrainEvent>? Changed;
    public long Session { get { lock (gate) return session; } }
    public bool Paused
    {
        get { lock (gate) return paused; }
        set
        {
            lock (gate)
            {
                paused = value; themBuffer.Clear(); meBuffer.Clear();
                if (value) { Cancel(candidate?.Cancellation); candidate = null; pending.Clear(); }
                // Pause stops listening/auto work; an already visible or manual answer may finish.
            }
        }
    }
    public ContextEntry[] Snapshot() { lock (gate) return entries.ToArray(); }
    public void DiscardSpeechBuffer(bool them) { lock (gate) (them ? themBuffer : meBuffer).Clear(); }
    public void SpeechStarted()
    {
        lock (gate)
        {
            if (disposed || paused) return;
            Cancel(candidate?.Cancellation); candidate = null;
        }
    }

    public void Speech(bool them, string text, bool end)
    {
        lock (gate)
        {
            if (disposed || paused) return;
            var buffer = them ? themBuffer : meBuffer;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (buffer.Length > 0) buffer.Append(' ');
                buffer.Append(text.Trim());
                if (them) { Cancel(candidate?.Cancellation); candidate = null; }
            }
            if (buffer.Length > 20_000) buffer.Remove(0, buffer.Length - 20_000);
            if (!end) return;
            var final = buffer.ToString().Trim(); buffer.Clear();
            if (final.Length == 0)
            {
                if (them && pending.Count > 0 && candidate is null) Start(true, "", null);
                return;
            }
            var now = clock.GetUtcNow();
            recentThem.RemoveAll(x => now - x.Time > TimeSpan.FromSeconds(3));
            var words = Words(final);
            // Keep short replies and repeated terminology. Only a long, almost verbatim,
            // contiguous copy of one recent interviewer utterance is plausible acoustic echo.
            if (!them && words.Length >= 8 && recentThem.Any(x => IsEcho(words, x.Words))) return;
            Add(them ? "Собеседник" : "Я", final);
            if (!them) return;
            recentThem.Add((now, words));
            if (final.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3 && pending.Count == 0) return;
            pending.Add((++speechId, final));
            if (pending.Count > 12) pending.RemoveAt(0);
            Start(true, "", null);
        }
    }

    private static bool IsEcho(string[] spoken, string[] original)
    {
        if (spoken.Length > original.Length) return false;
        for (var start = 0; start <= original.Length - spoken.Length; start++)
            if (spoken.Where((word, index) => word == original[start + index]).Count() / (double)spoken.Length >= 0.9) return true;
        return false;
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

    // Called under gate; configuration delegates, event handlers and cancellation callbacks
    // run outside this lock. One visible request may coexist with one unconfirmed auto candidate.
    private void Start(bool auto, string text, byte[]? image)
    {
        Cancel(candidate?.Cancellation); candidate = null;
        if (!auto)
        {
            if (active is { } old) { Record(old); Cancel(old.Cancellation); }
            active = null; pending.Clear();
        }
        var request = new RequestState(++requestId, session, auto, speechId);
        if (auto) candidate = request; else active = request;
        var context = (summary.Length > 0 ? "Конспект начала сессии:\n" + summary + "\n\nПоследние минуты:\n" : "Разговор с начала сессии:\n") +
            string.Join('\n', entries.Select(Line));
        if (auto && active is { Visible: true, Recorded: false } current)
            context += "\nТвоя подсказка (ещё печатается): " + current.Text[..Math.Min(6000, current.Text.Length)];
        context += auto
            ? "\n\nНовые реплики без подсказки:\n«" + string.Join(' ', pending.Select(x => x.Text)) + "»\n" + Prompts.Auto
            : image is not null
                ? "\n\nКандидат снял экран и пишет: «" + text + "». Свяжи изображение с разговором. Начни строкой «Экран: …» (до 20 слов), затем решение."
                : "\n\nКандидат пишет тебе: «" + text + "». Ответь с учётом разговора.";
        if (!auto) Emit(request, BrainEventKind.Thinking);
        var imageCopy = image?.ToArray();
        var work = Task.Run(() => RunAsync(request, context, imageCopy));
        if (auto) AutoWork = work;
    }

    private bool Current(RequestState request) => !disposed && request.Session == session && (active == request || candidate == request);

    private async Task RunAsync(RequestState request, string context, byte[]? image)
    {
        var text = new StringBuilder();
        try
        {
            if (request.Auto)
            {
                var delay = Task.Delay(TimeSpan.FromMilliseconds(150), clock, request.Cancellation.Token);
                request.DelayReady.TrySetResult();
                await delay;
            }
            lock (gate) if (!Current(request)) return;
            // File I/O/settings callbacks may be slow or call back into Brain; never hold gate here.
            var userProfile = profile();
            var selectedModel = model();
            var prompt = Prompts.System + (userProfile.Length > 0 ? "\n\nПрофиль кандидата:\n" + userProfile : "");
            var body = new CompletionRequest(selectedModel, prompt, context, image);
            lock (gate) if (!Current(request)) return;
            await foreach (var chunk in client.StreamAsync(body, request.Cancellation.Token))
            {
                lock (gate)
                {
                    if (!Current(request)) return;
                    text.Append(chunk);
                    if (text.Length > 100_000) throw new IOException("Ответ превысил допустимый размер");
                    if (request.Auto && !request.Visible && AutoReply.Decide(text.ToString(), complete: false) == AutoDecision.Wait) continue;
                    Publish(request, text.ToString());
                }
            }
            lock (gate)
            {
                if (!Current(request)) return;
                if (request.Auto && !request.Visible)
                {
                    if (AutoReply.Decide(text.ToString(), complete: true) == AutoDecision.Skip)
                    {
                        candidate = null; ClearPending(request); Emit(request, BrainEventKind.Skipped); return;
                    }
                    Publish(request, text.ToString());
                }
                Record(request); active = null;
                Emit(request, request.Visible ? BrainEventKind.Done : BrainEventKind.Error,
                    request.Visible ? request.Text : "Модель вернула пустой ответ");
            }
        }
        catch (Exception e)
        {
            lock (gate)
            {
                if (!Current(request)) return;
                Record(request);
                if (active == request) active = null;
                if (candidate == request) candidate = null;
                Emit(request, BrainEventKind.Error, e is OperationCanceledException ? "Истекло время ожидания ответа" : e.Message);
            }
        }
        finally { request.DelayReady.TrySetResult(); request.Cancellation.Dispose(); }
    }

    private void Publish(RequestState request, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (candidate == request)
        {
            if (active is { } old) { Record(old); Cancel(old.Cancellation); }
            active = request; candidate = null; ClearPending(request);
        }
        var startsNew = !request.Visible;
        request.Text = text; request.Visible = true;
        Emit(request, BrainEventKind.Answer, text, startsNew);
    }

    private void ClearPending(RequestState request) => pending.RemoveAll(x => x.Id <= request.PendingThrough);
    private void Emit(RequestState request, BrainEventKind kind, string text = "", bool startsNew = false)
    {
        notifications.Writer.TryWrite(new BrainEvent(request.Session, request.Id, kind, text, startsNew));
        notificationWorker ??= Task.Run(DispatchAsync);
    }
    private async Task DispatchAsync()
    {
        await foreach (var notification in notifications.Reader.ReadAllAsync())
        {
            lock (gate) if (disposed || notification.Session != session) continue;
            // One consumer preserves order; synchronous UI callbacks cannot deadlock on gate.
            if (Changed is not { } handlers) continue;
            foreach (Action<BrainEvent> handler in handlers.GetInvocationList())
            {
                try { handler(notification); }
                catch (Exception) { /* One observer must not stop delivery to the other observers. */ }
            }
        }
    }
    private static async void Cancel(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* Cancellation remains effective even if a callback fails. */ }
    }
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
        if (summaryCancellation is not null || disposed || clock.GetUtcNow() < nextSummaryAttempt) return;
        var cut = 0;
        while (cut < entries.Count - 20 && clock.GetUtcNow() - entries[cut].Time > TimeSpan.FromMinutes(12)) cut++;
        if (cut < 15) return;
        var generation = session;
        var prompt = "Обнови конспект собеседования, добавив новый кусок. Сохрани точно задачи, вопросы, таблицы, поля, цифры, ограничения, содержание экранов, ответы кандидата, подсказки и открытые вопросы. Удали small talk. До 600 слов.\n\nКонспект:\n" + summary +
            "\n\nНовый кусок:\n" + string.Join('\n', entries.Take(cut).Select(Line));
        var cancellation = new CancellationTokenSource(); summaryCancellation = cancellation;
        SummaryWork = Task.Run(async () =>
        {
            var result = new StringBuilder();
            var succeeded = false;
            try
            {
                await foreach (var chunk in client.StreamAsync(new CompletionRequest(model(), "Ты ведёшь точный конспект разговора.", prompt, Summary: true), cancellation.Token))
                { result.Append(chunk); if (result.Length > 20_000) throw new IOException("Слишком длинный конспект"); }
                succeeded = !string.IsNullOrWhiteSpace(result.ToString());
            }
            catch (Exception) { /* Keep the verbatim entries and back off before another attempt. */ }
            finally
            {
                lock (gate)
                {
                    if (generation == session && !disposed)
                    {
                        if (succeeded)
                        {
                            summary = result.ToString(); entries.RemoveRange(0, cut);
                            summaryFailures = 0; nextSummaryAttempt = DateTimeOffset.MinValue;
                        }
                        else
                        {
                            summaryFailures = Math.Min(5, summaryFailures + 1);
                            nextSummaryAttempt = clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(300, 30 * (1 << (summaryFailures - 1))));
                        }
                    }
                    if (summaryCancellation == cancellation) summaryCancellation = null;
                }
                cancellation.Dispose();
            }
        });
    }

    public void Reset()
    {
        lock (gate)
        {
            session++;
            Cancel(active?.Cancellation); active = null;
            Cancel(candidate?.Cancellation); candidate = null;
            Cancel(summaryCancellation); summaryCancellation = null;
            entries.Clear(); pending.Clear(); recentThem.Clear(); themBuffer.Clear(); meBuffer.Clear();
            summary = ""; summaryFailures = 0; nextSummaryAttempt = DateTimeOffset.MinValue;
        }
    }
    public void Dispose()
    {
        lock (gate) { Reset(); disposed = true; notifications.Writer.TryComplete(); }
    }
}
