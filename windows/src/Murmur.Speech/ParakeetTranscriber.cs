using System.Runtime.InteropServices;

using Murmur.Abstractions;
using SherpaOnnx;

namespace Murmur.Speech;

/// <summary>
/// NVIDIA Parakeet TDT, running locally through sherpa-onnx.
/// </summary>
/// <remarks>
/// <para>
/// Windows has no counterpart to Apple's <c>SpeechAnalyzer</c>, so unlike the macOS build —
/// where Parakeet is an optional upgrade — this is the only engine, and the app cannot
/// transcribe until the model files are downloaded. See <c>docs/PARAKEET-WINDOWS.md</c>.
/// </para>
/// <para>
/// <b>CPU only, deliberately.</b> sherpa-onnx ships no GPU package at all — setting a CUDA
/// provider silently falls back to CPU. DirectML is several releases behind, forbids parallel
/// inference, and wants fixed tensor shapes, which a variable-length audio model cannot
/// provide. With int8 weights on four threads, v3 was measured at 6–8× real time on a Core
/// Ultra 7 165H — a 5-second utterance comes back in under a second, which is the figure that
/// matters for dictation. None of the GPU options is worth the dependency for that.
/// </para>
/// <para>
/// <b>Biasing is not supported by this engine.</b> sherpa-onnx's offline recogniser has no
/// contextual-strings equivalent to Apple's <c>AnalysisContext</c>, so the dictionary's
/// correction pass carries the whole job on Windows. That pass was always the guarantee and
/// biasing only ever the nudge, so behaviour matches — the Windows build simply has one fewer
/// chance to get a name right before correction.
/// </para>
/// </remarks>
public sealed class ParakeetTranscriber : ITranscriber
{
    /// <summary>
    /// Threads for inference.
    /// </summary>
    /// <remarks>
    /// Four to eight is a plateau — on a Core Ultra 7 165H, v3 measured 7.1× real time at four
    /// threads and 8.0× at eight. Past eight it collapses (4.4× at twelve, 3.1× at sixteen),
    /// because oversubscribing a hybrid CPU spills the work onto efficiency cores. Four sits at
    /// the bottom of the plateau and is the safe choice on a machine we cannot measure.
    /// </remarks>
    private const int Threads = 4;

    /// <summary>
    /// Feature dimension. <b>Must be 128</b> — the library defaults to 80, which is wrong for
    /// this model.
    /// </summary>
    private const int FeatureDim = 128;

    /// <summary>
    /// Model family. <b>Required.</b> Without it the model fails to load.
    /// </summary>
    private const string ModelType = "nemo_transducer";

    private readonly string _modelDirectory;
    private OfflineRecognizer? _recognizer;

    /// <summary>Points the engine at a folder of model files.</summary>
    /// <param name="modelDirectory">
    /// Must contain <c>encoder.int8.onnx</c>, <c>decoder.int8.onnx</c>,
    /// <c>joiner.int8.onnx</c> and <c>tokens.txt</c>.
    /// </param>
    public ParakeetTranscriber(string modelDirectory) => _modelDirectory = modelDirectory;

    /// <summary>
    /// A Parakeet release the app knows how to describe.
    /// </summary>
    /// <remarks>
    /// The engine treats every release identically — same four files, same
    /// <c>nemo_transducer</c> configuration, no language switch to set, because the model
    /// identifies the spoken language itself. What differs is what the user can actually
    /// dictate, and that is invisible from the files alone. Naming the releases here lets the
    /// app tell someone whose model only speaks English <i>why</i> their Spanish came out as
    /// nonsense, rather than leaving them to guess.
    /// </remarks>
    /// <param name="Folder">
    /// The directory name under <c>models\</c>, and the only thing distinguishing one release
    /// from another on disk.
    /// </param>
    /// <param name="Name">A short human label for the release, for the settings window.</param>
    /// <param name="Languages">What this release can transcribe, phrased for a user to read.</param>
    /// <param name="IsMultilingual">
    /// Whether anything but English will work. Drives the nudge shown to someone still on the
    /// English-only build.
    /// </param>
    public sealed record ModelVariant(string Folder, string Name, string Languages, bool IsMultilingual);

    /// <summary>The known releases, best first.</summary>
    /// <remarks>
    /// v3 leads because it is a strict superset in practice: same encoder size and the same
    /// speed, a larger vocabulary, and 24 more languages. There is no reason to prefer v2 once
    /// v3 is present, so a machine carrying both silently gets the better one.
    /// </remarks>
    public static IReadOnlyList<ModelVariant> Variants { get; } =
    [
        new ModelVariant(
            "parakeet-v3",
            "Parakeet v3",
            "25 European languages, including Spanish",
            IsMultilingual: true),
        new ModelVariant(
            "parakeet-v2",
            "Parakeet v2",
            "English only",
            IsMultilingual: false),
    ];

    /// <summary>Where the model is looked for, in order.</summary>
    /// <remarks>
    /// <para>
    /// <c>%LOCALAPPDATA%</c> first: it needs no administrator rights, so the app can download
    /// and update the model itself even when installed under Program Files.
    /// </para>
    /// <para>
    /// Variant-major, not location-major: every location for v3 is tried before any location
    /// for v2. A user who installed the multilingual model gets it even if an old English-only
    /// copy is still sitting next to the executable — the alternative would silently downgrade
    /// them to English on the strength of where a stale folder happens to live.
    /// </para>
    /// </remarks>
    public static IEnumerable<string> DefaultSearchPaths()
    {
        foreach (var variant in Variants)
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Murmur", "models", variant.Folder);

            // AppContext.BaseDirectory, not Assembly.Location — the latter returns an empty
            // string in a single-file app, which silently resolves paths against the current
            // directory instead.
            yield return Path.Combine(AppContext.BaseDirectory, "models", variant.Folder);
        }
    }

    /// <summary>Finds a directory containing a complete model, or null.</summary>
    public static string? Locate() => DefaultSearchPaths().FirstOrDefault(IsComplete);

    /// <summary>Identifies which known release lives in <paramref name="directory"/>, or null.</summary>
    /// <remarks>
    /// Null is an ordinary answer, not a failure: <c>ModelDirectory</c> can be pointed anywhere,
    /// and a hand-placed folder is still perfectly loadable. It only means the app has nothing
    /// trustworthy to say about which languages that model covers, so it should say nothing.
    /// </remarks>
    /// <param name="directory">A model directory; only its final path segment is examined.</param>
    public static ModelVariant? VariantOf(string directory)
    {
        var folder = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Variants.FirstOrDefault(v => string.Equals(v.Folder, folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether <paramref name="directory"/> holds every required file.</summary>
    /// <remarks>
    /// Worth checking before loading: a truncated download fails with an opaque protobuf
    /// parse error that reads like a corrupt build rather than a missing byte range.
    /// </remarks>
    public static bool IsComplete(string directory) =>
        RequiredFiles.All(f => File.Exists(Path.Combine(directory, f)));

    /// <summary>The files the engine needs.</summary>
    public static IReadOnlyList<string> RequiredFiles { get; } =
    [
        "encoder.int8.onnx",
        "decoder.int8.onnx",
        "joiner.int8.onnx",
        "tokens.txt",
    ];

    /// <inheritdoc />
    public bool IsReady => _recognizer is not null;

    /// <inheritdoc />
    public ValueTask<bool> LoadAsync(CancellationToken cancellationToken)
    {
        if (_recognizer is not null) return ValueTask.FromResult(true);
        if (!IsComplete(_modelDirectory)) return ValueTask.FromResult(false);

        // Must happen before the config is built: every path below crosses into native code.
        var directory = ResolveForNativeLayer(_modelDirectory);
        if (directory is null) return ValueTask.FromResult(false);

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = AudioChunk.SampleRate;
        config.FeatConfig.FeatureDim = FeatureDim;

        config.ModelConfig.Transducer.Encoder = Path.Combine(directory, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(directory, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(directory, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(directory, "tokens.txt");
        config.ModelConfig.ModelType = ModelType;
        config.ModelConfig.NumThreads = Threads;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";

        _recognizer = new OfflineRecognizer(config);
        return ValueTask.FromResult(true);
    }

    /// <summary>
    /// Rewrites a model directory into a form sherpa-onnx can actually open, or returns null
    /// if there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>sherpa-onnx takes its paths as ANSI, not Unicode.</b> Every path field on
    /// <c>OfflineModelConfig</c> is marshalled as <c>LPStr</c>, so the model directory is
    /// narrowed through the system code page on the way into native code. A Spanish user whose
    /// Windows account is <c>José</c> or <c>Muñoz</c> has a <c>%LOCALAPPDATA%</c> containing
    /// those letters, and anything the code page cannot represent becomes <c>?</c>.
    /// </para>
    /// <para>
    /// Measured, not theorised: a directory named <c>José-Muñoz</c> reached the native layer as
    /// <c>Jos?-Mu?oz</c> and failed with "tokens.txt does not exist". The failure is worse than
    /// it looks — the recogniser is constructed anyway, holding a null handle, so
    /// <see cref="IsReady"/> answers true and the app only falls over on the first dictation,
    /// with a <see cref="NullReferenceException"/> from inside the binding.
    /// </para>
    /// <para>
    /// The 8.3 short name is the way out: it is ASCII by construction, so it survives the
    /// narrowing intact. It is not guaranteed to exist — 8.3 generation can be switched off per
    /// volume — which is why a still-unrepresentable path returns null and is reported as "model
    /// not loaded" rather than being handed to native code to fail obscurely later.
    /// </para>
    /// </remarks>
    private static string? ResolveForNativeLayer(string directory)
    {
        // The overwhelmingly common case, and the only one on a machine with an ASCII user name.
        if (directory.All(char.IsAscii)) return directory;

        // Nothing to do off Windows: there is no short-name concept, and the app does not ship
        // there. Returning the path unchanged keeps this project building and testing on macOS,
        // which is the reason it targets plain net10.0.
        if (!OperatingSystem.IsWindows()) return directory;

        // Called twice on purpose: with no buffer the function reports the size it needs,
        // including the terminator, which avoids guessing at a limit.
        var required = GetShortPathNameW(directory, null, 0);
        if (required == 0) return null;

        var buffer = new char[required];
        var written = GetShortPathNameW(directory, buffer, required);

        // On success the return value excludes the terminator, so it must land strictly inside
        // the buffer we just sized. Anything else means the path changed underneath us.
        if (written == 0 || written >= required) return null;

        var shortPath = new string(buffer, 0, (int)written);

        // 8.3 generation disabled on the volume returns the long path unchanged, which leaves
        // us exactly where we started.
        return shortPath.All(char.IsAscii) ? shortPath : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[]? shortPath, uint bufferLength);

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="biasPhrases"/> is accepted and ignored — see the class remarks. Audio
    /// longer than the encoder can handle is the caller's problem; <c>AudioSegmenter</c>
    /// splits it before this is reached.
    /// </remarks>
    public ValueTask<string> TranscribeAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        CancellationToken cancellationToken)
    {
        if (_recognizer is null || samples.Length == 0) return ValueTask.FromResult(string.Empty);

        cancellationToken.ThrowIfCancellationRequested();

        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(AudioChunk.SampleRate, samples.ToArray());
        _recognizer.Decode(stream);

        return ValueTask.FromResult(stream.Result.Text?.Trim() ?? string.Empty);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _recognizer?.Dispose();
        _recognizer = null;
        return ValueTask.CompletedTask;
    }
}
