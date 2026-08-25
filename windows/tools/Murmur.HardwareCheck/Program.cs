using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Murmur.Abstractions;
using NAudio.CoreAudioApi;
using Murmur.Core;
using Murmur.Platform.Windows;
using Murmur.Dictionary;
using Murmur.Speech;

// Checks the bindings that CI structurally cannot: the low-level keyboard hook firing on a
// real key event, WASAPI opening a real microphone, and SendInput delivering text — Spanish
// text specifically — into a real foreground window.
//
// None of it needs a person at the keyboard. The keystrokes are synthesised through the same
// SendInput path Windows uses for physical keys, and the injection target is a window this
// process owns, so nothing is typed into the user's other applications.
//
// Usage:  Murmur.HardwareCheck [hook|audio|inject|model|all]

Console.OutputEncoding = Encoding.UTF8;

var stage = args.Length > 0 ? args[0] : "all";
var failures = 0;

if (stage is "all" or "hook") failures += HookCheck(ConfiguredKey());
if (stage is "all" or "audio") failures += MicrophoneCheck();
if (stage is "all" or "inject") failures += InjectionCheck();
if (stage is "all" or "model") failures += ModelCheck();
if (stage is "all" or "devices") failures += DeviceCheck();
if (stage is "all" or "wiring") failures += WiringCheck();
if (stage is "listen") failures += ListenCheck();

Console.WriteLine();
Console.WriteLine(failures == 0 ? "hardware-check: PASS" : $"hardware-check: {failures} FAILED");
return failures == 0 ? 0 : 1;

/// <summary>The key the user actually configured, so the check tests their setup.</summary>
/// <remarks>
/// Reading the real settings file matters: "the hook works" is worthless if it was only ever
/// proven for the default key and the user rebound it to something else.
/// </remarks>
static PushToTalkKey ConfiguredKey()
{
    var configured = new AppSettings(AppSettings.DefaultPath).Data.PushToTalkKey;
    return (PushToTalkKey)configured;
}

static int HookCheck(PushToTalkKey key)
{
    Console.WriteLine("== keyboard hook ==");
    Console.WriteLine($"  configured key: {key} (0x{(int)key:X2})");

    using var hook = new PushToTalkHook { Key = key };

    var pressed = 0;
    var released = 0;
    hook.Pressed += (_, _) => Interlocked.Increment(ref pressed);
    hook.Released += (_, _) => Interlocked.Increment(ref released);

    if (!hook.Start()) return Check("hook installs", false);
    var failures = Check("hook installs", true);

    // The hook ignores only events carrying its own InjectedTag, so untagged SendInput travels
    // exactly the path a physical keypress does.
    Keyboard.Tap((int)key, holdMs: 250);
    Thread.Sleep(400);

    failures += Check($"press fired (got {pressed})", pressed == 1);
    failures += Check($"release fired (got {released})", released == 1);

    // The OS re-fires key-down while a key is held; only the first is a press.
    pressed = released = 0;
    Keyboard.Down((int)key);
    Keyboard.Down((int)key);
    Keyboard.Up((int)key);
    Thread.Sleep(400);

    failures += Check($"held key counts as one press (got {pressed})", pressed == 1);

    // The left-hand twin of each modifier shares a scan code with its right-hand counterpart
    // and differs only by the extended flag, so this is the case a naive Normalize gets wrong.
    var twin = key switch
    {
        PushToTalkKey.RightControl => Keyboard.VkLeftControl,
        PushToTalkKey.RightShift => Keyboard.VkLeftShift,
        PushToTalkKey.RightAlt => Keyboard.VkLeftAlt,
        _ => 0,
    };

    if (twin != 0)
    {
        pressed = 0;
        Keyboard.Tap(twin, holdMs: 80);
        Thread.Sleep(300);

        failures += Check($"the left-hand twin does not trigger dictation (got {pressed})", pressed == 0);
    }

    hook.StopListening();
    return failures;
}

static int MicrophoneCheck()
{
    Console.WriteLine();
    Console.WriteLine("== microphone (WASAPI) ==");

    try
    {
        var capture = new WasapiAudioCapture();
        var chunks = 0;
        var samples = 0;
        var peak = 0f;
        long firstChunkMs = -1;

        var clock = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try
        {
            Task.Run(async () =>
            {
                await foreach (var chunk in capture.CaptureAsync(cts.Token))
                {
                    if (firstChunkMs < 0) firstChunkMs = clock.ElapsedMilliseconds;
                    chunks++;
                    samples += chunk.Samples.Length;
                    peak = Math.Max(peak, chunk.Rms());
                }
            }).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected: the window expired.
        }

        var seconds = samples / (float)AudioChunk.SampleRate;

        Console.WriteLine($"  {chunks} chunks, {seconds:0.00}s of audio, peak RMS {peak:0.00000}");
        Console.WriteLine($"  first chunk after {firstChunkMs} ms");

        var failures = Check("device opened and delivered audio", chunks > 0);

        // Windows feeds digital silence rather than an error when microphone access is denied,
        // so exact zeroes are the signature of a blocked device, not a quiet room.
        failures += Check($"audio is not digital silence (peak RMS {peak:0.00000})", peak > 0f);
        failures += Check("blocked-microphone heuristic agrees", !capture.LooksLikeBlockedMicrophone);

        // Capture is opened inside CaptureAsync, so this delay is charged to the user between
        // pressing the key and the microphone being live — it is speech that never gets heard.
        if (firstChunkMs > 250)
        {
            Console.WriteLine($"  NOTE: {firstChunkMs} ms of startup latency means roughly that much");
            Console.WriteLine("        of the first word is lost. Measured 430-730 ms on a ThinkPad.");
        }

        capture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return failures;
    }
    catch (Exception e)
    {
        Console.WriteLine($"  EXCEPTION {e.GetType().Name}: {e.Message}");
        return Check("microphone capture", false);
    }
}

static int InjectionCheck()
{
    Console.WriteLine();
    Console.WriteLine("== text injection (SendInput) ==");

    // Spanish is the interesting case: the Latin-1 supplement, inverted punctuation, guillemets
    // and an em dash all in one line. Every one is a single UTF-16 code unit, which is what
    // KEYEVENTF_UNICODE can carry.
    const string spanish = "¿Cómo estás, Andrés? «Sí» — año, niño, Ñu, ¡vale!";

    // Over the 200-character paste threshold and containing a newline, so this takes the other
    // branch of InjectAsync entirely.
    var longText = string.Join('\n', Enumerable.Repeat(spanish, 6));

    var typed = string.Empty;
    var typedLong = string.Empty;
    Exception? error = null;

    var thread = new Thread(() =>
    {
        try
        {
            var box = new TextBox { Multiline = true, Dock = DockStyle.Fill };
            var form = new Form
            {
                Text = "Murmur hardware check",
                Width = 600,
                Height = 220,
                TopMost = true,
                StartPosition = FormStartPosition.CenterScreen,
            };
            form.Controls.Add(box);

            form.Shown += async (_, _) =>
            {
                Foreground.Take(form.Handle);
                form.Activate();
                box.Focus();
                await Task.Delay(600).ConfigureAwait(true);

                Console.WriteLine($"  owns the foreground: {Foreground.Current() == form.Handle}");

                var injector = new SendInputTextInjector();

                await injector.InjectAsync(spanish, CancellationToken.None).ConfigureAwait(true);
                await Task.Delay(800).ConfigureAwait(true);
                typed = box.Text;

                box.Clear();
                await injector.InjectAsync(longText, CancellationToken.None).ConfigureAwait(true);
                await Task.Delay(1500).ConfigureAwait(true);
                typedLong = box.Text.Replace("\r\n", "\n", StringComparison.Ordinal);

                form.Close();
            };

            Application.Run(form);
        }
        catch (Exception e)
        {
            error = e;
        }
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join(TimeSpan.FromSeconds(30));

    if (error is not null)
    {
        Console.WriteLine($"  EXCEPTION {error.GetType().Name}: {error.Message}");
        return Check("injection", false);
    }

    Console.WriteLine($"  sent    \"{spanish}\"");
    Console.WriteLine($"  arrived \"{typed}\"");

    var failures = Check("short Spanish text arrives byte-for-byte", typed == spanish);
    failures += Check($"long multi-line text arrives intact ({typedLong.Length}/{longText.Length} chars)",
        typedLong == longText);

    return failures;
}

static int ModelCheck()
{
    Console.WriteLine();
    Console.WriteLine("== speech model ==");

    var directory = ParakeetTranscriber.Locate();
    if (directory is null)
    {
        Console.WriteLine("  no model installed — see docs/PARAKEET-WINDOWS.md");
        return Check("model located", false);
    }

    var variant = ParakeetTranscriber.VariantOf(directory);
    Console.WriteLine($"  {directory}");
    Console.WriteLine($"  {variant?.Name ?? "unrecognised"} — {variant?.Languages ?? "unknown coverage"}");

    var transcriber = new ParakeetTranscriber(directory);
    var load = Stopwatch.StartNew();
    var loaded = transcriber.LoadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    load.Stop();

    Console.WriteLine($"  loaded in {load.ElapsedMilliseconds} ms, working set {Environment.WorkingSet / (1024 * 1024)} MB");

    var failures = Check("model loads", loaded && transcriber.IsReady);

    if (variant is { IsMultilingual: false })
    {
        Console.WriteLine("  NOTE: this model is English-only. Dictating Spanish needs parakeet-v3.");
    }

    // A second of silence: this proves the native layer is genuinely open rather than holding a
    // null handle, which is how an unreadable model path used to fail.
    var text = transcriber
        .TranscribeAsync(new float[AudioChunk.SampleRate], [], CancellationToken.None)
        .AsTask().GetAwaiter().GetResult();

    failures += Check($"decodes without throwing (silence gave \"{text}\")", true);

    transcriber.DisposeAsync().AsTask().GetAwaiter().GetResult();
    return failures;
}

/// <summary>
/// Lists every capture device and records a moment from each.
/// </summary>
/// <remarks>
/// <c>WasapiAudioCapture</c> asks for the default <b>Communications</b> endpoint, which is a
/// different setting from the default Console endpoint and is frequently pointed somewhere
/// else entirely — a disconnected headset, a webcam, a virtual cable. That produces exactly the
/// symptom this exists to diagnose: recording starts, the app looks healthy, and every
/// transcript comes back empty.
/// </remarks>
static int DeviceCheck()
{
    Console.WriteLine();
    Console.WriteLine("== capture devices ==");

    using var enumerator = new MMDeviceEnumerator();

    var communications = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    var console = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

    Console.WriteLine($"  default Communications : {communications.FriendlyName}");
    Console.WriteLine($"  default Console        : {console.FriendlyName}");

    if (communications.ID != console.ID)
    {
        Console.WriteLine("  NOTE: these differ. The app follows Communications.");
    }

    Console.WriteLine();

    var loudest = string.Empty;
    var loudestPeak = 0f;

    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
    {
        var peak = Listen(device.ID, TimeSpan.FromSeconds(2));
        var marker = device.ID == communications.ID ? " <-- the app uses this" : string.Empty;

        Console.WriteLine($"  {peak:0.00000}  {device.FriendlyName}{marker}");

        if (peak > loudestPeak)
        {
            loudestPeak = peak;
            loudest = device.FriendlyName;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"  loudest: {loudest} ({loudestPeak:0.00000})");
    Console.WriteLine("  Speak while this runs. A device that never rises above ~0.01 is not hearing you.");

    return 0;
}

/// <summary>Records from one device and returns the peak RMS seen.</summary>
static float Listen(string deviceId, TimeSpan duration)
{
    var peak = 0f;

    try
    {
        var capture = new WasapiAudioCapture(deviceId);
        using var cts = new CancellationTokenSource(duration);

        try
        {
            Task.Run(async () =>
            {
                await foreach (var chunk in capture.CaptureAsync(cts.Token))
                {
                    peak = Math.Max(peak, chunk.Rms());
                }
            }).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        capture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    catch (Exception e)
    {
        Console.WriteLine($"    (could not open: {e.GetType().Name})");
    }

    return peak;
}

/// <summary>
/// Arms the real engine and reports, live, where a dictation stops working.
/// </summary>
/// <remarks>
/// Every other check exercises one binding in isolation. This is the whole path with a human in
/// it — hold the configured key, speak, release — printing the level as it hears you and the
/// transcript when it finishes. It is the fastest way to tell "never heard you" apart from
/// "heard you and failed to type".
/// </remarks>
static int ListenCheck()
{
    Console.WriteLine("== live dictation ==");

    var directory = ParakeetTranscriber.Locate();
    if (directory is null) return Check("model located", false);

    var key = ConfiguredKey();
    var capture = new WasapiAudioCapture();
    var hook = new PushToTalkHook { Key = key };
    var transcriber = new ParakeetTranscriber(directory);
    var injected = new List<string>();

    var engine = new DictationEngine(
        capture, hook, transcriber, new CollectingInjector(injected), () => []);

    DictationResult? result = null;
    engine.Completed += (_, r) => result = r;

    Console.WriteLine("  loading model...");
    transcriber.LoadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

    var failures = Check("model loaded", transcriber.IsReady);
    failures += Check("hook armed", engine.Start());

    Console.WriteLine();
    Console.WriteLine($"  HOLD {key} AND SPEAK. 30 seconds. Ctrl+C to stop.");
    Console.WriteLine();

    var peak = 0f;
    var everRecorded = false;

    for (var i = 0; i < 300; i++)
    {
        Thread.Sleep(100);

        if (engine.State == DictationState.Recording)
        {
            everRecorded = true;
            peak = Math.Max(peak, engine.Level);
            Console.Write($"\r  recording... level {engine.Level:0.0000}  peak {peak:0.0000}   ");
        }

        if (result is not null) break;
    }

    Console.WriteLine();
    Console.WriteLine();

    failures += Check("the key started a recording", everRecorded);
    failures += Check($"the microphone heard something (peak {peak:0.0000})", peak > 0.01f);

    if (result is null)
    {
        Console.WriteLine("  no transcript produced.");
        Console.WriteLine(peak > 0.01f
            ? "  It heard audio but decoded nothing — check the model, or speak closer."
            : "  It heard nothing. Run the 'devices' check: the wrong microphone is likely selected.");
        failures++;
    }
    else
    {
        Console.WriteLine($"  heard: \"{result.Text}\"");
        Console.WriteLine($"  {result.AudioDuration.TotalSeconds:0.0}s audio decoded in {result.ProcessingTime.TotalSeconds:0.00}s");
        failures += Check("text was handed to the injector", injected.Count == 1);
    }

    engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
    return failures;
}

/// <summary>
/// Exercises the collaborators the app has and this tool does not, against the real files.
/// </summary>
/// <remarks>
/// <c>ProcessAsync</c> is reached through a fire-and-forget <c>_ = EndAsync()</c>, so anything
/// it throws is swallowed whole: the state machine still returns to Idle and every visible
/// signal looks correct. The dictionary lookup and the history write are the two things it does
/// that a bare engine does not, and both read real files on disk, so both are checked here
/// against the user's actual data rather than a temp directory.
/// </remarks>
static int WiringCheck()
{
    Console.WriteLine();
    Console.WriteLine("== app wiring (real files) ==");

    var failures = 0;

    Console.WriteLine($"  dictionary: {DictionaryFile.DefaultPath}");
    try
    {
        var dictionary = new DictionaryFile(DictionaryFile.DefaultPath);
        var entries = dictionary.Entries;
        Console.WriteLine($"  {entries.Count} entries");

        // The engine calls both of these on every utterance, before it transcribes.
        var bias = DictionaryCorrector.BiasPhrases(entries);
        var corrector = new DictionaryCorrector(entries);
        var (text, _) = corrector.Apply("probando uno dos tres, ¿qué tal Andújar?");

        Console.WriteLine($"  bias phrases: {bias.Count}, correction pass returned {text.Length} chars");
        failures += Check("dictionary loads and corrects", true);
    }
    catch (Exception e)
    {
        Console.WriteLine($"  EXCEPTION {e.GetType().Name}: {e.Message}");
        failures += Check("dictionary loads and corrects", false);
    }

    Console.WriteLine($"  history: {TranscriptStore.DefaultPath}");
    try
    {
        var store = new TranscriptStore(TranscriptStore.DefaultPath);
        var before = store.Records.Count;

        store.Add(new TranscriptRecord
        {
            At = DateTimeOffset.Now,
            AudioSeconds = 1,
            ProcessingSeconds = 0.1,
            Text = "hardware-check ¿probando? año",
        });

        var after = new TranscriptStore(TranscriptStore.DefaultPath).Records.Count;
        Console.WriteLine($"  {before} records before, {after} after");

        failures += Check("history accepts a record", after == before + 1);

        // Leave no litter in the user's real history.
        var written = store.Records.FirstOrDefault(r => r.Text.StartsWith("hardware-check", StringComparison.Ordinal));
        if (written is not null) store.Remove(written.Id);
    }
    catch (Exception e)
    {
        Console.WriteLine($"  EXCEPTION {e.GetType().Name}: {e.Message}");
        failures += Check("history accepts a record", false);
    }

    return failures;
}

static int Check(string name, bool passed)
{
    Console.WriteLine($"  [{(passed ? "ok" : "FAIL")}] {name}");
    return passed ? 0 : 1;
}

/// <summary>Takes the foreground, working around Windows' foreground lock.</summary>
internal static class Foreground
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const int SwShow = 5;

    public static IntPtr Current() => GetForegroundWindow();

    /// <summary>
    /// Attaches to the current foreground thread's input queue before asking for focus.
    /// </summary>
    /// <remarks>
    /// Windows refuses <c>SetForegroundWindow</c> from a process that has never held the
    /// foreground, which a console-launched checker never has. Sharing an input queue with the
    /// thread that does hold it lifts the restriction. Without this the synthetic keystrokes go
    /// to whatever the user had open — which is both a wrong result and rude.
    /// </remarks>
    public static void Take(IntPtr window)
    {
        var foreground = GetForegroundWindow();
        var theirs = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var ours = GetCurrentThreadId();

        if (theirs != ours) AttachThreadInput(ours, theirs, true);

        ShowWindow(window, SwShow);
        BringWindowToTop(window);
        SetForegroundWindow(window);

        if (theirs != ours) AttachThreadInput(ours, theirs, false);
    }
}

/// <summary>Synthesises keyboard input, untagged, so the app's own hook sees it.</summary>
internal static class Keyboard
{
    public const int VkRightControl = 0xA3;
    public const int VkLeftControl = 0xA2;
    public const int VkRightShift = 0xA1;
    public const int VkLeftShift = 0xA0;
    public const int VkLeftAlt = 0xA4;

    private const uint InputKeyboard = 1;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MapVkToVsc = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // Laid out to match Win32's INPUT, whose union is sized by the largest member (MOUSEINPUT)
    // rather than by KEYBDINPUT. The trailing padding is what makes Marshal.SizeOf agree with
    // the 40 bytes SendInput expects on x64; without it the call silently does nothing.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public KeyboardInput Keyboard;
        public int PadA;
        public int PadB;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    public static void Tap(int key, int holdMs)
    {
        Down(key);
        Thread.Sleep(holdMs);
        Up(key);
    }

    public static void Down(int key) => Send(key, up: false);

    public static void Up(int key) => Send(key, up: true);

    private static void Send(int key, bool up)
    {
        var flags = up ? KeyEventKeyUp : 0;

        // Right Ctrl and Right Alt are extended keys; without the flag the hook's Normalize
        // sees a neutral VK_CONTROL/VK_MENU and resolves it to the *left* one, so the check
        // would silently test nothing. Right Shift is deliberately absent: Microsoft documents
        // it as NOT extended, identified instead by scan code 0x36, which MapVirtualKey gives us.
        if (key is VkRightControl or 0xA5) flags |= KeyEventExtended;

        var input = new Input
        {
            Type = InputKeyboard,
            Keyboard = new KeyboardInput
            {
                VirtualKey = (ushort)key,
                ScanCode = (ushort)MapVirtualKey((uint)key, MapVkToVsc),
                Flags = flags,
                Time = 0,
                ExtraInfo = IntPtr.Zero,
            },
        };

        var sent = SendInput(1, [input], Marshal.SizeOf<Input>());
        if (sent != 1)
        {
            Console.WriteLine($"  !! SendInput failed: {Marshal.GetLastWin32Error()}");
        }
    }
}

/// <summary>Records what would have been typed, instead of typing it.</summary>
internal sealed class CollectingInjector(List<string> sink) : ITextInjector
{
    public ValueTask<bool> InjectAsync(string text, CancellationToken cancellationToken)
    {
        sink.Add(text);
        return ValueTask.FromResult(true);
    }
}
