using Microsoft.Extensions.Time.Testing;
using Notely.Core;
using Xunit;

namespace Notely.Tests;

public sealed partial class CoreTests
{
    [Theory]
    [InlineData(-900, 50, 0, 50)]
    [InlineData(1800, 50, 1120, 50)]
    [InlineData(50, -600, 50, 0)]
    [InlineData(50, 1040, 50, 740)]
    [InlineData(-1, -1, 0, 0)]
    [InlineData(1920, 1080, 1120, 740)]
    public void Entire_window_stays_in_work_area_including_corners(int x, int y, int expectedX, int expectedY)
    {
        Assert.Equal(new WindowBounds(expectedX, expectedY, 800, 300),
            new WindowBounds(x, y, 800, 300).Fit(new(0, 0, 1920, 1040)));
    }

    [Fact]
    public void Window_bounds_support_negative_monitors_taskbar_and_oversized_legacy_settings()
    {
        var work = new WindowBounds(-1920, -1080, 1920, 1040);
        Assert.Equal(new WindowBounds(-1920, -1080, 1920, 1040), new WindowBounds(-5000, 5000, 4000, 2000).Fit(work));
        Assert.Equal(new WindowBounds(-800, -340, 800, 300), new WindowBounds(10, 10, 800, 300).Fit(work));
        Assert.Equal(new WindowBounds(40, 30, 800, 300), new WindowBounds(-50, -50, 800, 300).Fit(new(40, 30, 1880, 1010)));
        Assert.Throws<ArgumentException>(() => work.Fit(new(0, 0, 0, 100)));
    }

    [Theory]
    [InlineData("{\"type\":\"SpeechStarted\"}")]
    [InlineData("{\"type\":\"Results\",\"is_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"и ещё\"}]}}")]
    public async Task Resumed_speech_prevents_a_request_for_a_pause_inside_a_question(string json)
    {
        var clock = new FakeTimeProvider(); var fake = new ScriptedClient();
        using var brain = new Brain(fake, () => "", () => "test", clock);
        brain.Speech(true, "Как устроены индексы", true);
        await brain.AutoDelayReady.WaitAsync(Deadline); var interrupted = brain.AutoWork;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var activity = DeepgramProtocol.Parse(json)!;
        Assert.True(activity.Started); Assert.False(activity.End); Assert.Empty(activity.Text);
        brain.SpeechStarted();
        clock.Advance(TimeSpan.FromSeconds(1));
        await interrupted.WaitAsync(Deadline);
        Assert.False(fake.Calls.Reader.TryRead(out _));
        brain.Speech(true, "и когда они мешают записи", true);
        await brain.AutoDelayReady.WaitAsync(Deadline);
        clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.False(fake.Calls.Reader.TryRead(out _));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var call = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.Contains("Как устроены индексы и когда они мешают записи", call.Request.Context);
        Assert.DoesNotContain("и ещё", call.Request.Context);
        call.Chunks.Writer.TryWrite("SKIP"); call.Chunks.Writer.TryComplete();
        await brain.AutoWork.WaitAsync(Deadline);
    }

    [Fact]
    public async Task Utterance_end_recovers_pending_question_after_false_speech_start_without_duplicate_calls()
    {
        var clock = new FakeTimeProvider(); var fake = new ScriptedClient();
        using var brain = new Brain(fake, () => "", () => "test", clock);
        brain.Speech(true, "Что такое REST API", true);
        await brain.AutoDelayReady.WaitAsync(Deadline);
        brain.SpeechStarted();
        brain.Speech(true, "", true);
        await brain.AutoDelayReady.WaitAsync(Deadline); clock.Advance(TimeSpan.FromMilliseconds(150));
        var call = await fake.Calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        call.Chunks.Writer.TryWrite("SKIP"); call.Chunks.Writer.TryComplete();
        await brain.AutoWork.WaitAsync(Deadline);
        brain.Speech(true, "", true); clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(fake.Calls.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("{\"type\":\"UtteranceEnd\",\"last_word_end\":-1}")]
    [InlineData("{\"type\":\"UtteranceEnd\",\"last_word_end\":\"invalid\"}")]
    [InlineData("{\"type\":\"Results\",\"is_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"\"}]}}")]
    [InlineData("{\"type\":\"Results\",\"is_final\":\"true\",\"channel\":{\"alternatives\":[{\"transcript\":\"test\"}]}}")]
    public void Stale_empty_or_malformed_speech_events_do_not_trigger_processing(string json) => Assert.Null(DeepgramProtocol.Parse(json));

    [Fact]
    public void Deepgram_uses_conversation_endpointing_and_one_second_backup_not_an_extra_wait()
    {
        var url = DeepgramProtocol.Url([]).Query;
        Assert.Contains("endpointing=400", url); Assert.Contains("utterance_end_ms=1000", url);
        Assert.Contains("interim_results=true", url); Assert.Contains("vad_events=true", url);
        Assert.Equal(new SpeechResult("", true), DeepgramProtocol.Parse("{\"type\":\"UtteranceEnd\",\"last_word_end\":1.5}"));
    }
}
