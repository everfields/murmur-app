using System.Buffers;
using Murmur.Abstractions;
using Murmur.Dictionary;

namespace Murmur.Core;

/// <summary>What the engine is doing right now.</summary>
public enum DictationState
{
    /// <summary>Waiting for the hotkey.</summary>
    Idle,

    /// <summary>The key is held; audio is being captured.</summary>
    Recording,

    /// <summary>The key is released; the utterance is being transcribed.</summary>
    Transcribing,
}

/// <summary>One completed dictation.</summary>
public sealed record DictationResult(
    DateTimeOffset At,
    TimeSpan AudioDuration,
    TimeSpan ProcessingTime,
    string Text,
    IReadOnlyList<AppliedCorrection> Corrections);

/// <summary>
/// The whole dictation flow: hotkey down, capture, hotkey up, transcribe, correct, inject.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately platform-neutral. It targets plain <c>net10.0</c>, so <c>CA1416</c> turns any
/// accidental Windows API call in here into a build error. Everything platform-specific
/// arrives through the four interfaces it is constructed with.
/// </para>
/// <para>
/// That is what makes the interesting behaviour testable without Windows: hand it fakes and
/// the entire path — including chunking, the correction pass and the "nothing was said"
/// case — runs on any machine, in milliseconds.
/// </para>
/// </remarks>
public sealed class DictationEngine : IAsyncDisposable
{
    private readonly IAudioCapture _capture;
    private readonly IHotkeySource _hotkey;
    private readonly ITranscriber _transcriber;
    private readonly ITextInjector _injector;
    private readonly IClock _clock;
    private readonly Func<IReadOnlyList<DictionaryEntry>> _dictionary;
    private readonly Action<string>? _trace;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _recording;
    private List<float>? _buffer;
    private DateTimeOffset _startedAt;

    /// <summary>Current state.</summary>
    public DictationState State { get; private set; } = DictationState.Idle;

    /// <summary>Most recent input level, 0…1. Drives the meter.</summary>
    public float Level { get; private set; }

    /// <summary>Raised when a dictation completes and produced text.</summary>
    public event EventHandler<DictationResult>? Completed;

    /// <summary>Raised whenever <see cref="State"/> or <see cref="Level"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Wires the engine to its platform implementations.</summary>
    /// <param name="capture">Microphone source.</param>
    /// <param name="hotkey">Push-to-talk source.</param>
    /// <param name="transcriber">Speech engine.</param>
    /// <param name="injector">Where finished text goes.</param>
    /// <param name="dictionary">
    /// Read fresh on every utterance rather than captured once, so edits take effect without
    /// a restart.
    /// </param>
    /// <param name="clock">Time source; defaults to the system clock.</param>
    /// <param name="trace">
    /// Optional diagnostic sink, called with one line per stage of a dictation.
    /// </param>
    /// <remarks>
    /// <para>
    /// <paramref name="trace"/> exists because this class is reached through a fire-and-forget
    /// <c>_ = EndAsync()</c>, so a dictation can fail — or quietly produce nothing — while every
    /// visible signal in the UI stays perfectly correct. Without it, the only way to tell "heard
    /// nothing" from "heard you and decoded nothing" from "decoded and failed to type" is to
    /// rebuild the app.
    /// </para>
    /// </remarks>
    public DictationEngine(
        IAudioCapture capture,
        IHotkeySource hotkey,
        ITranscriber transcriber,
        ITextInjector injector,
        Func<IReadOnlyList<DictionaryEntry>> dictionary,
        IClock? clock = null,
        Action<string>? trace = null)
    {
        _trace = trace;
        _capture = capture;
        _hotkey = hotkey;
        _transcriber = transcriber;
        _injector = injector;
        _dictionary = dictionary;
        _clock = clock ?? SystemClock.Instance;

        _hotkey.Pressed += OnPressed;
        _hotkey.Released += OnReleased;
    }

    /// <summary>Arms the hotkey.</summary>
    /// <returns>False if the hook could not be installed.</returns>
    public bool Start() => _hotkey.Start();

    /// <summary>
    /// Starts or stops recording from a button rather than the hotkey.
    /// </summary>
    /// <remarks>
    /// Routed through the same state machine as the hotkey, deliberately. Two independent
    /// paths into recording would eventually disagree about whether it is running.
    /// </remarks>
    public void TogglePushToTalk()
    {
        if (State == DictationState.Idle) _ = BeginAsync();
        else if (State == DictationState.Recording) _ = EndAsync();
    }

    private void OnPressed(object? sender, EventArgs e) => _ = BeginAsync();

    private void OnReleased(object? sender, EventArgs e) => _ = EndAsync();

    private async Task BeginAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != DictationState.Idle) return;

            _buffer = [];
            _startedAt = _clock.Now;
            _recording = new CancellationTokenSource();
            SetState(DictationState.Recording);
            _trace?.Invoke("begin: recording");
        }
        finally
        {
            _gate.Release();
        }

        var chunks = 0;

        try
        {
            await foreach (var chunk in _capture.CaptureAsync(_recording!.Token).ConfigureAwait(false))
            {
                chunks++;
                // Stop consuming the moment recording ends. Cancellation is cooperative, so
                // chunks already queued still arrive after EndAsync has moved on — and
                // without this guard one of them sets Level back to a reading that has
                // already been zeroed.
                if (State != DictationState.Recording) break;

                // Copied, not referenced: capture implementations are entitled to reuse
                // their buffer the moment this returns.
                _buffer?.AddRange(chunk.Samples.Span);
                Level = chunk.Rms();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: the key was released.
        }
        catch (Exception e)
        {
            // Capture runs on a fire-and-forget task too, so this would otherwise vanish.
            _trace?.Invoke($"begin: capture FAILED {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            _trace?.Invoke($"begin: capture loop ended after {chunks} chunk(s)");

            // Authoritative: this runs only once the capture loop has genuinely finished, so
            // nothing can raise the level afterwards and leave the meter stuck.
            Level = 0;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task EndAsync()
    {
        List<float>? samples;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != DictationState.Recording) return;

            await _recording!.CancelAsync().ConfigureAwait(false);
            samples = _buffer;
            _trace?.Invoke($"end: buffer holds {(samples is null ? "null" : samples.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
            _buffer = null;
            Level = 0;
            SetState(DictationState.Transcribing);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await ProcessAsync(samples).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // The caller is `_ = EndAsync()`, so without this the failure disappears entirely
            // and the app simply looks like it dictated nothing.
            _trace?.Invoke($"process: FAILED {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            _recording?.Dispose();
            _recording = null;
            SetState(DictationState.Idle);
        }
    }

    /// <summary>
    /// Loads the speech model if it is not loaded already.
    /// </summary>
    /// <remarks>
    /// Idempotent and safe to call early. Worth calling at startup rather than leaving to the
    /// first utterance, because loading Parakeet takes 7–10 seconds and that would otherwise be
    /// charged to the user's first dictation.
    /// </remarks>
    public ValueTask<bool> PrepareAsync(CancellationToken cancellationToken) =>
        _transcriber.LoadAsync(cancellationToken);

    private async Task ProcessAsync(List<float>? samples)
    {
        if (samples is null || samples.Count == 0)
        {
            _trace?.Invoke($"process: nothing captured ({samples?.Count ?? 0} samples)");
            return;
        }

        _trace?.Invoke($"process: {samples.Count} samples ({samples.Count / (double)AudioChunk.SampleRate:0.00}s)");

        // Nothing else does this, and a transcriber that was never loaded does not complain —
        // it returns empty text, so every dictation silently produces nothing while the hotkey,
        // the meter and the whole UI behave perfectly. Load is idempotent, so the cost here is
        // one branch after the first utterance.
        if (!_transcriber.IsReady && !await _transcriber.LoadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            _trace?.Invoke("process: the speech model could not be loaded");
            return;
        }

        // Measured from key release, because that is the wait the user actually feels — and
        // it is the only figure on which a streaming and a batch engine compare honestly.
        var releasedAt = _clock.Now;
        var audio = new ReadOnlyMemory<float>(samples.ToArray());

        var entries = _dictionary();
        var bias = DictionaryCorrector.BiasPhrases(entries);

        var pieces = AudioSegmenter.Split(audio);
        var transcripts = new List<string>(pieces.Count);

        foreach (var piece in pieces)
        {
            var text = await _transcriber
                .TranscribeAsync(piece, bias, CancellationToken.None)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(text)) transcripts.Add(text.Trim());
        }

        var raw = string.Join(' ', transcripts);
        _trace?.Invoke($"process: {pieces.Count} segment(s) decoded to {raw.Length} chars");

        if (string.IsNullOrWhiteSpace(raw))
        {
            _trace?.Invoke("process: decoded nothing — silence, or the wrong microphone");
            return;
        }

        // The dictionary runs last and unconditionally. Biasing only raises the odds of the
        // right word; this is the pass that guarantees it.
        var (corrected, applied) = new DictionaryCorrector(entries).Apply(raw);

        var result = new DictationResult(
            At: releasedAt,
            AudioDuration: TimeSpan.FromSeconds((double)audio.Length / AudioChunk.SampleRate),
            ProcessingTime: _clock.Now - releasedAt,
            Text: corrected,
            Corrections: applied);

        // Isolated on purpose. Subscribers here do UI and disk work, and one of them throwing
        // must not cost the user the text they just dictated — injection is the whole point of
        // the app, and it happens after this.
        try
        {
            Completed?.Invoke(this, result);
        }
        catch (Exception e)
        {
            _trace?.Invoke($"process: a Completed subscriber threw {e.GetType().Name}: {e.Message}");
        }

        var injected = await _injector.InjectAsync(corrected, CancellationToken.None).ConfigureAwait(false);
        _trace?.Invoke($"process: injected={injected} text=\"{corrected}\"");
    }

    private void SetState(DictationState state)
    {
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _hotkey.Pressed -= OnPressed;
        _hotkey.Released -= OnReleased;
        _hotkey.Dispose();

        if (_recording is not null)
        {
            await _recording.CancelAsync().ConfigureAwait(false);
            _recording.Dispose();
        }

        await _capture.DisposeAsync().ConfigureAwait(false);
        await _transcriber.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
