using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Notely.Core;
using Xunit;

namespace Notely.Tests;

public sealed partial class CoreTests
{
    [Fact]
    public async Task Neighbouring_turns_are_debounced_into_one_request()
    {
        var fake = new ScriptedClient(); var clock = new FakeTimeProvider();
        using var brain = new Brain(fake, () => "", () => "test", clock);
        brain.Speech(true, "Расскажите пожалуйста про индексы", true);
        var firstWork = brain.AutoWork;
        await brain.AutoDelayReady.WaitAsync(Deadline);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        brain.Speech(true, "И когда они замедляют запись", true);
        await brain.AutoDelayReady.WaitAsync(Deadline);
        clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.False(fake.Calls.Reader.TryRead(out _));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var call = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.Contains("про индексы И когда", call.Request.Context);
        Assert.False(fake.Calls.Reader.TryRead(out _));
        await firstWork.WaitAsync(Deadline);
        call.Chunks.Writer.TryWrite("SKIP"); call.Chunks.Writer.TryComplete();
        await brain.AutoWork.WaitAsync(Deadline);
    }

    [Fact]
    public async Task Manual_question_cancels_pending_auto_debounce()
    {
        var fake = new ScriptedClient(); var clock = new FakeTimeProvider();
        using var brain = new Brain(fake, () => "", () => "test", clock);
        brain.Speech(true, "Расскажите пожалуйста про индексы", true);
        await brain.AutoDelayReady.WaitAsync(Deadline);
        var automatic = brain.AutoWork;
        brain.Ask("Мой ручной вопрос");
        var call = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        clock.Advance(TimeSpan.FromSeconds(1));
        await automatic.WaitAsync(Deadline);
        Assert.Contains("Кандидат пишет тебе: «Мой ручной вопрос»", call.Request.Context);
        Assert.False(fake.Calls.Reader.TryRead(out _));
        call.Chunks.Writer.TryComplete();
    }

    [Fact]
    public async Task Candidate_error_does_not_interrupt_screenshot_answer()
    {
        var fake = new ScriptedClient();
        using var brain = new Brain(fake, () => "", () => "test");
        var events = Channel.CreateUnbounded<BrainEvent>(); brain.Changed += e => events.Writer.TryWrite(e);
        brain.Ask("", [1, 2, 3]);
        var screenshot = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        screenshot.Chunks.Writer.TryWrite("Экран: таблица users."); await Next(events.Reader, BrainEventKind.Answer);
        brain.Speech(true, "Ну хорошо давайте дальше", true);
        var candidate = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.Null(candidate.Request.Image);
        candidate.Chunks.Writer.TryComplete(new IOException("Ошибка провайдера"));
        await Next(events.Reader, BrainEventKind.Error);
        Assert.False(screenshot.Token.IsCancellationRequested);
        screenshot.Chunks.Writer.TryWrite(" SELECT id FROM users;"); screenshot.Chunks.Writer.TryComplete();
        var continued = await Next(events.Reader, BrainEventKind.Answer);
        Assert.False(continued.StartsNew); Assert.Contains("SELECT", continued.Text);
        await Next(events.Reader, BrainEventKind.Done);
    }

    [Fact]
    public async Task Replaced_auto_candidate_cannot_interrupt_the_visible_answer()
    {
        var fake = new ScriptedClient(); var clock = new FakeTimeProvider();
        using var brain = new Brain(fake, () => "", () => "test", clock);
        var events = Channel.CreateUnbounded<BrainEvent>(); brain.Changed += e => events.Writer.TryWrite(e);
        brain.Ask("Первый вопрос"); var first = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        first.Chunks.Writer.TryWrite("Первый ответ"); await Next(events.Reader, BrainEventKind.Answer);
        brain.Speech(true, "Первое уточнение про индексы", true);
        await brain.AutoDelayReady.WaitAsync(Deadline); clock.Advance(TimeSpan.FromMilliseconds(150));
        var stale = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        var staleWork = brain.AutoWork;
        brain.Speech(true, "Второе уточнение про запись", true);
        await brain.AutoDelayReady.WaitAsync(Deadline); clock.Advance(TimeSpan.FromMilliseconds(150));
        var latest = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        stale.Chunks.Writer.TryWrite("ЗАПОЗДАЛЫЙ ОТВЕТ"); stale.Chunks.Writer.TryComplete();
        await staleWork.WaitAsync(Deadline);
        Assert.False(first.Token.IsCancellationRequested);
        latest.Chunks.Writer.TryWrite("SKIP"); latest.Chunks.Writer.TryComplete();
        await Next(events.Reader, BrainEventKind.Skipped);
        first.Chunks.Writer.TryWrite(" завершён"); first.Chunks.Writer.TryComplete();
        Assert.Equal("Первый ответ завершён", (await Next(events.Reader, BrainEventKind.Answer)).Text);
        await Next(events.Reader, BrainEventKind.Done);
        Assert.DoesNotContain(brain.Snapshot(), e => e.Text.Contains("ЗАПОЗДАЛЫЙ"));
    }

    [Fact]
    public async Task Pause_cancels_unconfirmed_auto_work_but_allows_visible_answer_to_finish()
    {
        var fake = new ScriptedClient(); var clock = new FakeTimeProvider();
        using var brain = new Brain(fake, () => "", () => "test", clock);
        var events = Channel.CreateUnbounded<BrainEvent>(); brain.Changed += e => events.Writer.TryWrite(e);
        brain.Ask("Вопрос"); var first = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        first.Chunks.Writer.TryWrite("Ответ"); await Next(events.Reader, BrainEventKind.Answer);
        brain.Speech(true, "Следующий вопрос про индексы", true);
        await brain.AutoDelayReady.WaitAsync(Deadline); var automatic = brain.AutoWork;
        brain.Paused = true; clock.Advance(TimeSpan.FromSeconds(1));
        await automatic.WaitAsync(Deadline);
        Assert.False(fake.Calls.Reader.TryRead(out _)); Assert.False(first.Token.IsCancellationRequested);
        first.Chunks.Writer.TryWrite(" завершён"); first.Chunks.Writer.TryComplete();
        await Next(events.Reader, BrainEventKind.Done);
        Assert.Equal("Ответ завершён", Assert.Single(brain.Snapshot(), e => e.Kind == "Твоя подсказка").Text);
    }

    [Fact]
    public void Echo_filter_keeps_short_and_reworded_candidate_answers()
    {
        var fake = new ScriptedClient(); var clock = new ManualClock();
        using var brain = new Brain(fake, () => "", () => "test", clock);
        brain.Speech(true, "Расскажите как через индекс можно ускорить поиск по большой таблице", true);
        brain.Speech(false, "Да через индекс", true);
        brain.Speech(false, "По большой таблице поиск можно ускорить через индекс и проверить план выполнения", true);
        Assert.Equal(2, brain.Snapshot().Count(e => e.Kind == "Я"));
    }

    [Fact]
    public void Echo_filter_drops_only_recent_long_verbatim_replay()
    {
        const string phrase = "Расскажите как через индекс можно ускорить поиск по большой таблице";
        var fake = new ScriptedClient(); var clock = new ManualClock();
        using var brain = new Brain(fake, () => "", () => "test", clock);
        brain.Speech(true, phrase, true); brain.Speech(false, phrase, true);
        Assert.DoesNotContain(brain.Snapshot(), e => e.Kind == "Я");
        clock.Now += TimeSpan.FromSeconds(4); brain.Speech(false, phrase, true);
        Assert.Single(brain.Snapshot(), e => e.Kind == "Я");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_empty_summary_backs_off_and_preserves_all_entries(bool empty)
    {
        var fake = new ScriptedClient(); var clock = new ManualClock();
        using var brain = new Brain(fake, () => "", () => "test", clock);
        for (var i = 0; i < 36; i++) brain.Speech(false, "старая запись " + i, true);
        clock.Now += TimeSpan.FromMinutes(13); brain.Speech(false, "ещё запись", true);
        var first = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        first.Chunks.Writer.TryComplete(empty ? null : new IOException("API unavailable"));
        await brain.SummaryWork.WaitAsync(Deadline);
        Assert.Equal(37, brain.Snapshot().Length);
        for (var i = 0; i < 10; i++) brain.Speech(false, "новая запись " + i, true);
        clock.Now += TimeSpan.FromSeconds(29); brain.Speech(false, "не пора", true);
        Assert.False(fake.Calls.Reader.TryRead(out _));
        clock.Now += TimeSpan.FromSeconds(1); brain.Speech(false, "пора повторить", true);
        var second = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.True(second.Request.Summary);
        second.Chunks.Writer.TryComplete(new IOException("API still unavailable")); await brain.SummaryWork.WaitAsync(Deadline);
        clock.Now += TimeSpan.FromSeconds(59); brain.Speech(false, "ещё рано", true);
        Assert.False(fake.Calls.Reader.TryRead(out _));
        clock.Now += TimeSpan.FromSeconds(1); brain.Speech(false, "повтор после минуты", true);
        var third = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        third.Chunks.Writer.TryWrite("Точный конспект"); third.Chunks.Writer.TryComplete();
        await brain.SummaryWork.WaitAsync(Deadline);
        Assert.DoesNotContain(brain.Snapshot(), e => e.Text == "старая запись 0");
        Assert.Contains(brain.Snapshot(), e => e.Text == "повтор после минуты");
        brain.Ask("Проверка контекста"); var next = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.Contains("Точный конспект", next.Request.Context); next.Chunks.Writer.TryComplete();
    }

    [Fact]
    public async Task Observer_can_wait_for_another_thread_to_read_brain_without_deadlock()
    {
        var fake = new ScriptedClient(); using var brain = new Brain(fake, () => "", () => "test");
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        brain.Changed += e =>
        {
            if (e.Kind != BrainEventKind.Thinking) return;
            try { Task.Run(brain.Snapshot).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); observed.TrySetResult(); }
            catch (Exception error) { observed.TrySetException(error); }
        };
        await Task.Run(() => brain.Ask("Вопрос")).WaitAsync(Deadline);
        await observed.Task.WaitAsync(Deadline);
        var call = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline); call.Chunks.Writer.TryComplete();
    }

    [Fact]
    public async Task Profile_and_model_callbacks_run_without_the_state_lock()
    {
        var fake = new ScriptedClient(); Brain? brain = null;
        string ReadState() { Task.Run(() => brain!.Snapshot()).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); return "test"; }
        using (brain = new Brain(fake, ReadState, ReadState))
        {
            brain.Ask("Вопрос"); var call = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Assert.Equal("test", call.Request.Model); Assert.Contains("Профиль кандидата:\ntest", call.Request.System);
            call.Chunks.Writer.TryComplete();
        }
    }

    [Fact]
    public async Task Throwing_observer_does_not_abort_generation_or_other_observers()
    {
        var fake = new ScriptedClient(); using var brain = new Brain(fake, () => "", () => "test");
        var events = Channel.CreateUnbounded<BrainEvent>();
        brain.Changed += _ => throw new InvalidOperationException("observer");
        brain.Changed += e => events.Writer.TryWrite(e);
        brain.Ask("Вопрос"); var call = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        call.Chunks.Writer.TryWrite("Ответ"); call.Chunks.Writer.TryComplete();
        await Next(events.Reader, BrainEventKind.Done);
        Assert.Equal("Ответ", Assert.Single(brain.Snapshot(), e => e.Kind == "Твоя подсказка").Text);
    }

    [Theory]
    [InlineData("SKIP")]
    [InlineData("**SKIP**")]
    [InlineData(" `skip` ")]
    [InlineData("__SKIP__.")]
    public void Skip_parser_holds_every_fragment_and_hides_only_the_complete_sentinel(string text)
    {
        for (var length = 1; length <= text.Length; length++)
            Assert.Equal(AutoDecision.Wait, AutoReply.Decide(text[..length], complete: false));
        Assert.Equal(AutoDecision.Skip, AutoReply.Decide(text, complete: true));
    }

    [Theory]
    [InlineData("SKIP LOCKED позволяет пропускать заблокированные строки")]
    [InlineData("**SQL** — язык запросов")]
    [InlineData("S")]
    public void Skip_parser_does_not_swallow_real_answers(string text) =>
        Assert.Equal(AutoDecision.Answer, AutoReply.Decide(text, complete: true));
}
