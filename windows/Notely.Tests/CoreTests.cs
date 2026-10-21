using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Notely.Core;
using Xunit;

namespace Notely.Tests;

public sealed partial class CoreTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("SKIP")]
    [InlineData("**SKIP**")]
    [InlineData("__skip__")]
    [InlineData(" `SKIP`. ")]
    public async Task Non_question_does_not_interrupt_the_visible_answer(string skip)
    {
        var fake = new ScriptedClient();
        using var brain = new Brain(fake, () => "", () => "test");
        var events = Channel.CreateUnbounded<BrainEvent>(); brain.Changed += e => events.Writer.TryWrite(e);
        brain.Ask("Первый вопрос");
        var first = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        first.Chunks.Writer.TryWrite("Начало ответа");
        var answer = await Next(events.Reader, BrainEventKind.Answer);
        Assert.True(answer.StartsNew);
        brain.Speech(true, "Ну хорошо давайте дальше", true);
        var second = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.Contains("Начало ответа", second.Request.Context);
        foreach (var character in skip) second.Chunks.Writer.TryWrite(character.ToString());
        second.Chunks.Writer.TryComplete();
        await Next(events.Reader, BrainEventKind.Skipped);
        Assert.False(first.Token.IsCancellationRequested);
        first.Chunks.Writer.TryWrite(" завершение"); first.Chunks.Writer.TryComplete();
        var continuation = await Next(events.Reader, BrainEventKind.Answer);
        Assert.False(continuation.StartsNew); Assert.Equal("Начало ответа завершение", continuation.Text);
        await Next(events.Reader, BrainEventKind.Done);
        Assert.Equal("Начало ответа завершение", Assert.Single(brain.Snapshot(), e => e.Kind == "Твоя подсказка").Text);
    }

    [Fact]
    public async Task Next_answer_starts_a_new_block_and_never_overwrites_the_previous_one()
    {
        var fake = new ScriptedClient(); using var brain = new Brain(fake, () => "", () => "test");
        var events = Channel.CreateUnbounded<BrainEvent>(); brain.Changed += e => events.Writer.TryWrite(e);
        brain.Ask("Один"); var first = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        first.Chunks.Writer.TryWrite("Ответ 1"); await Next(events.Reader, BrainEventKind.Answer);
        brain.Ask("Два"); var second = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        first.Chunks.Writer.TryWrite("старый хвост"); first.Chunks.Writer.TryComplete();
        second.Chunks.Writer.TryWrite("Ответ"); second.Chunks.Writer.TryWrite(" 2"); second.Chunks.Writer.TryComplete();
        var start = await Next(events.Reader, BrainEventKind.Answer);
        Assert.True(start.StartsNew); Assert.Equal("Ответ", start.Text);
        var update = await Next(events.Reader, BrainEventKind.Answer);
        Assert.False(update.StartsNew); Assert.Equal("Ответ 2", update.Text);
        await Next(events.Reader, BrainEventKind.Done);
        Assert.Equal(2, brain.Snapshot().Count(e => e.Kind == "Твоя подсказка"));
    }

    [Fact]
    public async Task Screenshot_is_sent_once_and_later_turns_use_its_textual_description()
    {
        var fake = new ScriptedClient(); using var brain = new Brain(fake, () => "", () => "test");
        var events = Channel.CreateUnbounded<BrainEvent>(); brain.Changed += e => events.Writer.TryWrite(e);
        brain.Ask("Разбери", [1, 2, 3]); var first = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(new byte[] { 1, 2, 3 }, first.Request.Image);
        first.Chunks.Writer.TryWrite("Экран: таблица users. SELECT id FROM users;"); first.Chunks.Writer.TryComplete();
        await Next(events.Reader, BrainEventKind.Done);
        brain.Speech(true, "А как сделать JOIN", true);
        var second = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.Null(second.Request.Image); Assert.Contains("таблица users", second.Request.Context);
        var json = JsonSerializer.Serialize(OpenRouterClient.CreateBody(second.Request));
        Assert.DoesNotContain("image_url", json);
        second.Chunks.Writer.TryComplete();
    }

    [Fact]
    public async Task Reset_discards_in_flight_generation_and_clears_pending_speech()
    {
        var fake = new ScriptedClient(); using var brain = new Brain(fake, () => "", () => "test");
        brain.Ask("СЕКРЕТ СТАРОЙ СЕССИИ"); var old = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        brain.Speech(true, "Недописанная фраза", false);
        brain.Reset(); var generation = brain.Session;
        old.Chunks.Writer.TryWrite("старый ответ"); old.Chunks.Writer.TryComplete();
        brain.Ask("Новая сессия"); var next = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.DoesNotContain("СЕКРЕТ", next.Request.Context); Assert.DoesNotContain("Недописанная", next.Request.Context);
        Assert.Equal(generation, brain.Session); Assert.Single(brain.Snapshot()); next.Chunks.Writer.TryComplete();
    }

    [Fact]
    public async Task Confirmed_auto_answer_replaces_manual_generation_and_rejects_its_late_chunks()
    {
        var fake = new ScriptedClient(); using var brain = new Brain(fake, () => "", () => "test");
        var events = Channel.CreateUnbounded<BrainEvent>(); brain.Changed += e => events.Writer.TryWrite(e);
        brain.Ask("Ручной вопрос");
        var first = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        first.Chunks.Writer.TryWrite("Ручной ответ"); await Next(events.Reader, BrainEventKind.Answer);
        brain.Speech(true, "Что такое REST API", true);
        var second = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.False(first.Token.IsCancellationRequested);
        second.Chunks.Writer.TryWrite("REST");
        var promoted = await Next(events.Reader, BrainEventKind.Answer);
        Assert.True(promoted.StartsNew); Assert.Equal("REST", promoted.Text);
        first.Chunks.Writer.TryWrite("СТАРЫЙ ХВОСТ"); first.Chunks.Writer.TryComplete();
        second.Chunks.Writer.TryWrite(" — архитектурный стиль."); second.Chunks.Writer.TryComplete();
        Assert.Equal("REST — архитектурный стиль.", (await Next(events.Reader, BrainEventKind.Answer)).Text);
        await Next(events.Reader, BrainEventKind.Done);
        await first.Finished.Task.WaitAsync(Deadline); await second.Finished.Task.WaitAsync(Deadline);
        Assert.Equal(2, brain.Snapshot().Count(e => e.Kind == "Твоя подсказка"));
        Assert.DoesNotContain(brain.Snapshot(), e => e.Text.Contains("СТАРЫЙ ХВОСТ"));
    }

    [Fact]
    public async Task Partial_answer_is_recorded_when_provider_fails_mid_stream()
    {
        var handler = new StubHandler("data: {\"choices\":[{\"delta\":{\"content\":\"Начало\"}}]}\n\ndata: {\"error\":{\"message\":\"bad\"}}\n\n");
        using var http = new HttpClient(handler); using var brain = new Brain(new OpenRouterClient(http, "dummy"), () => "", () => "test");
        var events = Channel.CreateUnbounded<BrainEvent>(); brain.Changed += e => events.Writer.TryWrite(e);
        brain.Ask("Вопрос"); await Next(events.Reader, BrainEventKind.Error);
        Assert.Equal("Начало", brain.Snapshot().Single(e => e.Kind == "Твоя подсказка").Text);
    }

    [Fact]
    public async Task Summary_from_previous_session_cannot_leak_after_reset()
    {
        var fake = new ScriptedClient(); var clock = new ManualClock();
        using var brain = new Brain(fake, () => "", () => "test", clock);
        for (var i = 0; i < 36; i++) brain.Speech(false, "старая запись " + i, true);
        clock.Now += TimeSpan.FromMinutes(13);
        brain.Speech(false, "ещё запись", true);
        var summary = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.True(summary.Request.Summary);
        var summaryWork = brain.SummaryWork;
        brain.Reset();
        summary.Chunks.Writer.TryWrite("ЧУЖОЙ КОНСПЕКТ"); summary.Chunks.Writer.TryComplete();
        await summaryWork.WaitAsync(Deadline);
        brain.Ask("Новая тема"); var next = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.DoesNotContain("ЧУЖОЙ", next.Request.Context); next.Chunks.Writer.TryComplete();
    }

    [Fact]
    public void Empty_input_pause_and_echo_do_not_trigger_questions()
    {
        var fake = new ScriptedClient(); using var brain = new Brain(fake, () => "", () => "test");
        brain.Ask(" "); brain.Speech(true, "", true); brain.Paused = true;
        brain.Speech(true, "Что такое REST API", true);
        Assert.Empty(brain.Snapshot()); Assert.False(fake.Calls.Reader.TryRead(out _));
        brain.Paused = false; brain.Speech(true, "API", true); brain.Speech(false, "API API", true);
        Assert.Equal(2, brain.Snapshot().Length);
        Assert.Contains(brain.Snapshot(), e => e.Kind == "Я" && e.Text == "API API");
    }

    [Fact]
    public void Profile_comments_including_unclosed_comments_are_not_sent()
    {
        Assert.Equal("", Prompts.CleanProfile(Prompts.ProfileTemplate));
        Assert.Equal("Факт", Prompts.CleanProfile("Факт\n<!--секрет"));
        Assert.DoesNotContain("секрет", Prompts.CleanProfile("# Заголовок\nФакт <!--секрет-->"));
        Assert.Equal(new[] { "SQL", "JOIN" }, Prompts.ParseKeyterms("# comment\nSQL\nsql\n\n JOIN\r\n"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not-json")]
    [InlineData("{\"type\":[]}")]
    [InlineData("{\"type\":\"Results\",\"is_final\":true,\"channel\":null}")]
    [InlineData("{\"type\":\"Results\",\"is_final\":true,\"channel\":{\"alternatives\":[]}}")]
    [InlineData("{\"type\":\"Results\",\"is_final\":false}")]
    public void Malformed_or_interim_deepgram_frames_are_ignored(string json) => Assert.Null(DeepgramProtocol.Parse(json));

    [Fact]
    public void Speech_final_and_utterance_end_are_kept_even_with_empty_text()
    {
        Assert.Equal(new SpeechResult("", true), DeepgramProtocol.Parse("{\"type\":\"UtteranceEnd\"}"));
        Assert.Equal(new SpeechResult("", true), DeepgramProtocol.Parse("{\"type\":\"Results\",\"is_final\":true,\"speech_final\":true,\"channel\":{\"alternatives\":[{\"transcript\":\"\"}]}}"));
        Assert.Contains("keyterm=LEFT%20JOIN", DeepgramProtocol.Url(["LEFT JOIN"]).AbsoluteUri);
        Assert.Throws<IOException>(() => DeepgramProtocol.Parse("{\"type\":\"Error\"}"));
    }

    [Fact]
    public async Task Sse_supports_comments_multiline_data_and_final_line_without_newline()
    {
        var reader = new StringReader(": ping\n\ndata:{\n" + "data: \"ok\":true}\n\ndata:[DONE]");
        var frames = new List<string>();
        await foreach (var frame in ServerEvents.ReadAsync(reader, default)) frames.Add(frame);
        Assert.Equal(new[] { "{\n\"ok\":true}", "[DONE]" }, frames);
    }

    [Theory]
    [InlineData(401)] [InlineData(402)] [InlineData(429)] [InlineData(503)]
    public async Task Http_failures_do_not_expose_raw_response_or_credentials(int status)
    {
        using var http = new HttpClient(new StubHandler("SECRET-RESPONSE", (HttpStatusCode)status));
        var error = await Assert.ThrowsAsync<IOException>(async () =>
        { await foreach (var _ in new OpenRouterClient(http, "SECRET-KEY").StreamAsync(new("test", "", ""), default)) { } });
        Assert.Contains(status.ToString(), error.Message); Assert.DoesNotContain("SECRET", error.Message);
    }

    [Fact]
    public async Task Unexpected_eof_is_failure_not_a_successful_answer()
    {
        using var http = new HttpClient(new StubHandler("data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n"));
        await Assert.ThrowsAsync<IOException>(async () =>
        { await foreach (var _ in new OpenRouterClient(http, "dummy").StreamAsync(new("test", "", ""), default)) { } });
    }

    [Fact]
    public async Task Openrouter_skips_bad_json_usage_frames_and_handles_content_and_done()
    {
        using var http = new HttpClient(new StubHandler("data: bad\n\ndata: null\n\ndata: {\"usage\":{}}\n\ndata: {\"choices\":[]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"ответ\"}}]}\n\ndata: [DONE]\n\n"));
        var chunks = new List<string>();
        await foreach (var chunk in new OpenRouterClient(http, "dummy").StreamAsync(new("test", "", ""), default)) chunks.Add(chunk);
        Assert.Equal(new[] { "ответ" }, chunks);
    }

    [Fact]
    public async Task Cancelled_network_request_stops_promptly()
    {
        using var token = new CancellationTokenSource(); token.Cancel();
        using var http = new HttpClient(new StubHandler(""));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in new OpenRouterClient(http, "dummy").StreamAsync(new("test", "", ""), token.Token)) { } });
    }

    [Fact]
    public void Pcm_native_float_stereo_converts_identically_across_chunk_boundaries()
    {
        var bytes = new byte[48000 * 8];
        for (var i = 0; i < 48000; i++)
        {
            var v = (float)Math.Sin(i * 2 * Math.PI * 400 / 48000) * 0.4f;
            BitConverter.GetBytes(v).CopyTo(bytes, i * 8); BitConverter.GetBytes(v).CopyTo(bytes, i * 8 + 4);
        }
        var whole = new PcmEncoder(48000, 2, 32, true).Convert(bytes);
        var chunked = new PcmEncoder(48000, 2, 32, true); var result = new List<byte>();
        for (var at = 0; at < bytes.Length; at += 8 * 137)
            result.AddRange(chunked.Convert(bytes.AsSpan(at, Math.Min(8 * 137, bytes.Length - at))).Data);
        Assert.Equal(32000, whole.Data.Length); Assert.Equal(whole.Data, result.ToArray()); Assert.True(whole.Level > 0);
    }

    [Fact]
    public void Pcm_silence_malformed_input_and_nonfinite_samples_are_safe()
    {
        Assert.Throws<ArgumentException>(() => new PcmEncoder(0, 2, 32, true));
        Assert.Throws<ArgumentException>(() => new PcmEncoder(48000, 2, 32, true).Convert(new byte[3]));
        var empty = new PcmEncoder(16000, 1, 16, false).Convert([]); Assert.Empty(empty.Data); Assert.Equal(0, empty.Level);
        var nan = new PcmEncoder(16000, 1, 32, true).Convert(BitConverter.GetBytes(float.NaN));
        Assert.Equal(new byte[2], nan.Data); Assert.Equal(0, nan.Level);
        Assert.All(new PcmEncoder(44100, 1, 16, false).Convert(new byte[88200]).Data, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Loopback_silence_ends_a_turn_without_double_feeding_active_audio()
    {
        var clock = new ManualClock(); var silence = new LoopbackSilence(clock);
        silence.AudioArrived(); Assert.Null(silence.Poll());
        clock.Now += TimeSpan.FromMilliseconds(199); Assert.Null(silence.Poll());
        clock.Now += TimeSpan.FromMilliseconds(1); Assert.Equal(3200, silence.Poll()!.Length);
        Assert.Null(silence.Poll()); clock.Now += TimeSpan.FromMilliseconds(100);
        Assert.Equal(3200, silence.Poll()!.Length);
        silence.AudioArrived(); clock.Now += TimeSpan.FromMilliseconds(100); Assert.Null(silence.Poll());
    }

    [Theory] [InlineData(16)] [InlineData(24)] [InlineData(32)]
    public void Integer_pcm_formats_support_positive_and_negative_full_scale(int bits)
    {
        var bytes = bits switch { 16 => new byte[] { 255, 127, 0, 128 }, 24 => [255, 255, 127, 0, 0, 128], _ => [255, 255, 255, 127, 0, 0, 0, 128] };
        var result = new PcmEncoder(16000, 1, bits, false).Convert(bytes).Data;
        Assert.Equal(4, result.Length); Assert.True(BitConverter.ToInt16(result, 0) > 32000); Assert.True(BitConverter.ToInt16(result, 2) < -32000);
    }

    private static async Task<BrainEvent> Next(ChannelReader<BrainEvent> reader, BrainEventKind kind)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (true) { var e = await reader.ReadAsync(timeout.Token); if (e.Kind == kind) return e; }
    }
    private sealed class ManualClock : TimeProvider
    { public DateTimeOffset Now = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class ScriptedClient : ICompletionClient
    {
        public sealed record Call(CompletionRequest Request, Channel<string> Chunks, TaskCompletionSource Finished, CancellationToken Token);
        public Channel<Call> Calls = Channel.CreateUnbounded<Call>();
        // Deliberately ignores cancellation to simulate stale callbacks from an uncooperative provider.
        public async IAsyncEnumerable<string> StreamAsync(CompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var call = new Call(request, Channel.CreateUnbounded<string>(), new(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
            Calls.Writer.TryWrite(call);
            try { await foreach (var chunk in call.Chunks.Reader.ReadAllAsync()) yield return chunk; }
            finally { call.Finished.TrySetResult(); }
        }
    }
    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") }); }
    }
}
