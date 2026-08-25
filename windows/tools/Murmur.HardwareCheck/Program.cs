using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Platform.Windows;
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

if (stage is "all" or "hook") failures += HookCheck();
if (stage is "all" or "audio") failures += MicrophoneCheck();
if (stage is "all" or "inject") failures += InjectionCheck();
if (stage is "all" or "model") failures += ModelCheck();

Console.WriteLine();
Console.WriteLine(failures == 0 ? "hardware-check: PASS" : $"hardware-check: {failures} FAILED");
return failures == 0 ? 0 : 1;

static int HookCheck()
{
    Console.WriteLine("== keyboard hook ==");

    using var hook = new PushToTalkHook { Key = PushToTalkKey.RightControl };

    var pressed = 0;
    var released = 0;
    hook.Pressed += (_, _) => Interlocked.Increment(ref pressed);
    hook.Released += (_, _) => Interlocked.Increment(ref released);

    if (!hook.Start()) return Check("hook installs", false);
    var failures = Check("hook installs", true);

    // The hook ignores only events carrying its own InjectedTag, so untagged SendInput travels
    // exactly the path a physical keypress does.
    Keyboard.Tap(Keyboard.VkRightControl, holdMs: 250);
    Thread.Sleep(400);

    failures += Check($"press fired (got {pressed})", pressed == 1);
    failures += Check($"release fired (got {released})", released == 1);

    // The OS re-fires key-down while a key is held; only the first is a press.
    pressed = released = 0;
    Keyboard.Down(Keyboard.VkRightControl);
    Keyboard.Down(Keyboard.VkRightControl);
    Keyboard.Up(Keyboard.VkRightControl);
    Thread.Sleep(400);

    failures += Check($"held key counts as one press (got {pressed})", pressed == 1);

    // Left Ctrl shares a scan code with Right Ctrl and differs only by the extended flag, so
    // this is the case a naive Normalize gets wrong.
    pressed = 0;
    Keyboard.Tap(Keyboard.VkLeftControl, holdMs: 80);
    Thread.Sleep(300);

    failures += Check($"left ctrl does not trigger dictation (got {pressed})", pressed == 0);

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

        // Right Ctrl is an extended key. Without the flag the hook's Normalize sees a neutral
        // VK_CONTROL and resolves it to *left* Ctrl, so the check would silently test nothing.
        if (key is VkRightControl) flags |= KeyEventExtended;

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
