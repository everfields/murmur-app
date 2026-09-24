using System.Runtime.CompilerServices;
using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

public sealed class ToggleShortcutTests
{
    [Fact]
    public async Task First_activation_records_release_does_nothing_second_activation_transcribes()
    {
        var key = new FakeHotkeySource { IsToggle = true };
        var audio = new ControlledCapture();
        var injector = new RecordingTextInjector();
        await using var engine = new DictationEngine(audio, key, new FakeTranscriber("hola"), injector, () => []);
        engine.Start().ShouldBeTrue();

        key.Press();
        await audio.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        key.Release();
        engine.State.ShouldBe(DictationState.Recording);
        injector.Injected.ShouldBeEmpty();

        key.Press();
        await WaitForState(engine, DictationState.Idle);
        injector.Injected.ShouldBe(["hola"]);
    }

    [Fact]
    public async Task Button_and_shortcut_share_recording_state()
    {
        var key = new FakeHotkeySource { IsToggle = true };
        var audio = new ControlledCapture();
        var injector = new RecordingTextInjector();
        await using var engine = new DictationEngine(audio, key, new FakeTranscriber("done"), injector, () => []);

        engine.TogglePushToTalk();
        await audio.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        key.Press();
        await WaitForState(engine, DictationState.Idle);

        key.Press();
        engine.State.ShouldBe(DictationState.Recording);
        engine.TogglePushToTalk();
        await WaitForState(engine, DictationState.Idle);
        injector.Injected.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Activations_during_transcription_do_not_start_another_recording()
    {
        var key = new FakeHotkeySource { IsToggle = true };
        var audio = new ControlledCapture();
        var transcriber = new PausedTranscriber();
        await using var engine = new DictationEngine(audio, key, transcriber, new RecordingTextInjector(), () => []);

        key.Press();
        await audio.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        key.Press();
        await transcriber.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        key.Press();
        key.Release();
        engine.State.ShouldBe(DictationState.Transcribing);
        transcriber.Finish.SetResult("done");
        await WaitForState(engine, DictationState.Idle);

        key.Press();
        engine.State.ShouldBe(DictationState.Recording);
        key.Press();
        await WaitForState(engine, DictationState.Idle);
    }

    [Fact]
    public async Task Conflicting_shortcut_reports_failure_and_record_button_still_works()
    {
        var key = new FakeHotkeySource { IsToggle = true, CanStart = false };
        var audio = new ControlledCapture();
        await using var engine = new DictationEngine(audio, key, new FakeTranscriber("done"), new RecordingTextInjector(), () => []);
        engine.Start().ShouldBeFalse();
        engine.HotkeyError.ShouldNotBeNull().ShouldContain("already in use");
        engine.TogglePushToTalk();
        await audio.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        engine.State.ShouldBe(DictationState.Recording);
        engine.TogglePushToTalk();
        await WaitForState(engine, DictationState.Idle);

        engine.ChangeHotkey(new FakeHotkeySource { IsToggle = true });
        engine.HotkeyError.ShouldBeNull();
    }

    [Fact]
    public async Task Changing_shortcut_finishes_recording_and_detaches_the_old_key()
    {
        var oldKey = new FakeHotkeySource();
        var audio = new ControlledCapture();
        var injector = new RecordingTextInjector();
        await using var engine = new DictationEngine(audio, oldKey, new FakeTranscriber("done"), injector, () => []);
        engine.Start();
        oldKey.Press();
        await audio.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var newKey = new FakeHotkeySource { IsToggle = true };
        engine.ChangeHotkey(newKey);
        await WaitForState(engine, DictationState.Idle);
        oldKey.IsRunning.ShouldBeFalse();
        newKey.IsRunning.ShouldBeTrue();
        oldKey.Press();
        engine.State.ShouldBe(DictationState.Idle);
        newKey.Press();
        newKey.Release();
        engine.State.ShouldBe(DictationState.Recording);
        newKey.Press();
        await WaitForState(engine, DictationState.Idle);
        injector.Injected.Count.ShouldBe(2);
    }

    private static async Task WaitForState(DictationEngine engine, DictationState state)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? sender, EventArgs e)
        {
            if (engine.State == state) reached.TrySetResult();
        }
        engine.Changed += Observe;
        try
        {
            Observe(null, EventArgs.Empty);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { engine.Changed -= Observe; }
    }

    private sealed class ControlledCapture : IAudioCapture
    {
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsCapturing { get; private set; }

        public async IAsyncEnumerable<AudioChunk> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            IsCapturing = true;
            try
            {
                yield return new AudioChunk(new float[] { 0.5f, -0.5f });
                Delivered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally { IsCapturing = false; }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PausedTranscriber : ITranscriber
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsReady => true;
        public ValueTask<bool> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask<string> TranscribeAsync(ReadOnlyMemory<float> samples, IReadOnlyList<string> biasPhrases, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return new ValueTask<string>(Finish.Task);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
