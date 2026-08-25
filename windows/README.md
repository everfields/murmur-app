# Murmur for Windows

The Windows port of Murmur — push-to-talk dictation, on-device.

> **Status: runs on real Windows hardware; nobody has dictated with it yet.** On this
> machine — Lenovo ThinkPad, Core Ultra 7 165H, 32 GB RAM, Windows 11 build 26100, x64,
> .NET SDK 10.0.204 — the full solution *including* `Murmur.Platform.Windows` builds at zero
> warnings with `-warnaserror`, all tests pass, `--selftest` reports all 11 checks ok against
> the real platform layer, and Parakeet transcribes English, Spanish, French and German test
> audio correctly. `Murmur.HardwareCheck` additionally proves the keyboard hook fires, the
> microphone opens, and `SendInput` types Spanish into a foreground window byte-for-byte. What
> still has *not* happened is a human holding the key and speaking — one link, and it is the
> last one. See [Honesty](#honesty).

---

## Why this is a rewrite, not a port

Almost every layer of the macOS app is Apple-specific:

| Layer | macOS | Windows |
|---|---|---|
| UI | SwiftUI | Avalonia |
| Audio capture | `AVAudioEngine` | WASAPI via NAudio |
| **Default speech engine** | `SpeechAnalyzer` (ships with macOS 26) | **nothing equivalent exists** |
| Parakeet | FluidAudio → CoreML | sherpa-onnx → ONNX Runtime |
| Hotkey | `CGEventTap` | `SetWindowsHookEx(WH_KEYBOARD_LL)` |
| Text injection | Accessibility API | `SendInput` |

The consequence that shapes everything: **Windows has no counterpart to Apple's
`SpeechAnalyzer`.** On macOS, Parakeet is the optional upgrade. On Windows it is the only
engine, and the app cannot transcribe until the model is downloaded —
see [`docs/PARAKEET-WINDOWS.md`](../docs/PARAKEET-WINDOWS.md).

The app prefers the **multilingual v3 model** (25 European languages, Spanish among them) and
falls back to the English-only v2: it searches `models\parakeet-v3\` before
`models\parakeet-v2\`, in `%LOCALAPPDATA%\Murmur\` and then beside the executable. There is no
language setting to choose — the model identifies the spoken language itself. Download
routes, sizes and the corporate-proxy workarounds are all in that document.

What *is* genuinely shared is the dictionary's behaviour, and it is shared as a contract
rather than as code: [`shared/dictionary-test-vectors.json`](../shared/dictionary-test-vectors.json).
Both implementations run those vectors in CI. Changing correction semantics starts there.

---

## Decisions, and why

**Avalonia, not WPF or WinUI 3.** WPF cannot be run or UI-tested on macOS, so every mistake
would cost a full CI round-trip. Avalonia's headless test platform runs on macOS in ~100 ms,
including simulated keyboard input and real pixel capture. Win32 interop is unaffected —
hooks, `SendInput` and WASAPI are P/Invoke, not UI-framework code. WinUI 3 was rejected
outright: Microsoft's own docs contradict each other on whether unpackaged single-file
publishing works, with open bugs reporting an exe that won't launch.

**.NET 10, not .NET 8.** .NET 8 reaches end-of-life on **2026-11-10**.

**Right Ctrl is the default hotkey, not Right Alt.** Right Alt is AltGr on German, Polish,
UK, Nordic and most Latin-American layouts — it is how those users type `@`, `€`, `\`, `|`.
Binding push-to-talk there would break basic typing for a large fraction of users. Right
Ctrl produces no character on any layout.

**The hotkey is observed, never swallowed.** The macOS build consumes Right Option because
on macOS that key types characters. On Windows, suppression buys nothing and risks a much
worse failure: if the key-down is swallowed but the key-up escapes — a hook that timed out
mid-gesture, or focus crossing into an elevated window — the target app believes Ctrl is
held down forever.

**CPU-only inference.** sherpa-onnx ships no GPU package; DirectML is five versions behind
and forbids the variable tensor shapes this model requires; CUDA would force every user to
install a toolkit. Measured on a Core Ultra 7 165H with int8 weights and the shipped 4
threads: **6–8× real time**, ~750–875 ms for a 5.3 s clip, ~725 MB resident. Sub-second for a
dictation-length utterance is enough; a GPU dependency is not worth buying more.

**Four inference threads, not more.** Measured on that CPU, 4–8 threads is a flat plateau
(7.1× to 8.0×) and past 8 it collapses — 12 threads 4.4×, 16 threads 3.1× — because
oversubscribing a hybrid CPU spills work onto the efficiency cores. 4 sits at the bottom of
the plateau and cannot oversubscribe a smaller machine, which is the right default for a knob
nobody will turn.

**Three pinned versions that would break at "latest":**

| Package | Pinned | Why |
|---|---|---|
| `NAudio` | **2.3.0** | 3.x targets .NET 9+ and will not restore |
| `Avalonia.Headless.XUnit` | **11.3.20** | 12.x requires xUnit **v3**, a different package line |
| `org.k2fsa.sherpa.onnx` | 1.13.5 | Bundles ONNX Runtime; never also reference `Microsoft.ML.OnnxRuntime` |

---

## Layout

```
windows/
├─ Directory.Build.props          strict analysis, applied to every project
├─ Directory.Packages.props       central version pinning
├─ global.json                    SDK pin
├─ src/
│  ├─ Murmur.Dictionary/          corrections + biasing          net10.0
│  ├─ Murmur.Abstractions/        the four platform interfaces   net10.0
│  ├─ Murmur.Core/                engine, segmenter, storage     net10.0
│  ├─ Murmur.Speech/              Parakeet via sherpa-onnx       net10.0
│  ├─ Murmur.Testing/             fakes for the interfaces       net10.0
│  ├─ Murmur.App/                 Avalonia UI                    net10.0
│  └─ Murmur.Platform.Windows/    the ONLY Win32 code            net10.0-windows
├─ tests/
│  ├─ Murmur.Dictionary.Tests/    the shared vectors             24 tests
│  ├─ Murmur.Core.Tests/          engine, chunking, storage      38 tests
│  └─ Murmur.App.Tests/           headless Avalonia UI           18 tests
└─ tools/
   └─ Murmur.HardwareCheck/       hook, mic, injection           net10.0-windows
```

**Two projects target `-windows`:** the platform layer, and the hardware check that exercises
it. Everything else is platform-neutral, so `CA1416`
turns an accidental Win32 call into a build error — and, more usefully, the whole app
builds, runs and tests on macOS.

`Murmur.App` loads the platform layer **by reflection** rather than referencing it. A direct
reference would drag the UI onto `net10.0-windows` and destroy the local loop. The published
self-test verifies that reflection works from inside the single-file bundle, because that is
where the arrangement would otherwise fail — silently, at the moment the user first pressed
the key.

Keeping the platform layer logic-free is deliberate: anything that lives there is code CI
cannot exercise. Retries, debouncing, device-change handling all belong in the neutral
projects, behind an interface.

---

## Building

**On Windows** — everything, including the platform layer:

```bash
cd windows
dotnet build Murmur.sln --no-incremental -warnaserror   # 0 warnings
dotnet test  Murmur.sln                                 # 68 passed, 0 failed
```

Then `Murmur.App --selftest` — 11 checks, and the only way to exercise the reflection-loaded
platform layer without a keyboard and a microphone.

**On macOS or Linux** — use the solution filter. `Murmur.Platform.Windows` targets
`net10.0-windows` and cannot compile off Windows; the filter omits it and everything else
builds and tests normally, including the full UI suite:

```bash
cd windows
dotnet test Murmur.CrossPlatform.slnf -c Release      # ~0.5s, all 68 tests
```

`--no-incremental` is not optional in CI. Roslyn does not re-emit analyzer warnings on an
incremental build, so `-warnaserror` would pass on cached results and prove nothing.

---

## <a id="honesty"></a>Honesty about what is verified

**Verified on real Windows hardware** — Lenovo ThinkPad, Core Ultra 7 165H, 32 GB RAM,
Windows 11 build 26100, x64, .NET SDK 10.0.204:

- `dotnet build Murmur.sln --no-incremental -warnaserror` succeeds for the whole solution at
  **zero warnings**, `Murmur.Platform.Windows` included — the one project no macOS or Linux
  machine can compile.
- `dotnet test Murmur.sln` reports **68 passed, 0 failed** (24 dictionary, 26 core, 18 UI).
- `Murmur.App --selftest` reports **all 11 checks ok** against the real platform layer:
  the Windows platform assembly loads from the bundle, audio capture constructs, the text
  injector constructs, the hotkey source constructs on Right Ctrl, and the model resolves as
  `…\AppData\Local\Murmur\models\parakeet-v3 (Parakeet v3)`.
- **Parakeet transcribes.** The v3 model's own `test_wavs/` — English, Spanish, French,
  German — all came back correct, accents included, with no configuration change between
  languages.

**Verified by `Murmur.HardwareCheck`**, on the same machine. This is the tool that closed most
of the gap below, and it needs nobody at the keyboard — it synthesises keystrokes through the
same `SendInput` path Windows uses for physical keys, and injects into a window it owns:

```bash
cd windows
dotnet run --project tools/Murmur.HardwareCheck -c Release        # or: ... -- hook|audio|inject|model
```

- **The low-level keyboard hook fires on real key events.** Press and release both arrive, a
  held key counts as one press rather than a stream of auto-repeats, and Left Ctrl — which
  shares a scan code with Right Ctrl and differs only by the extended flag — correctly does not
  trigger dictation.
- **WASAPI opens the real microphone** and delivers non-silent 16 kHz mono audio, with the
  blocked-microphone heuristic agreeing that the device is live.
- **`SendInput` delivers Spanish byte-for-byte** into a real foreground window:
  `¿Cómo estás, Andrés? «Sí» — año, niño, Ñu, ¡vale!` arrives exactly as sent. A 299-character
  multi-line transcript also arrives intact, which exercises the other branch of `InjectAsync`.

That is the "never run on real hardware" gap closed for *build, tests, process startup, the
model, the hook, the microphone and injection*. What remains is the one link no synthetic input
can stand in for — see below.

**One measured problem, not yet fixed.** Capture opens the audio device inside `CaptureAsync`,
which runs on key-down, and the first chunk arrives **430–743 ms later**. That is roughly the
first word of every utterance, lost. Fixing it means holding the device open and discarding
samples while idle, which trades a permanently-lit microphone indicator and some battery for
those words — a product decision, so it is recorded here rather than silently taken.

**Verified in CI, every push:** the same test suite, plus a self-contained ~116 MB
single-file executable that CI publishes and then **runs**, with the binary reporting back
that the dictionary works, the source-generated JSON round-trips, and the Windows platform
layer loads and constructs out of the bundle.

**Verified on macOS, in ~0.5s:** the same tests, via the solution filter. The UI genuinely
runs headless here, which is why bugs like a `Render` method mutating a property get caught
while writing them rather than three CI round-trips later.

**Known divergences between the two regex engines**, measured across 30 cases — 9 differed.
The two that affect this code are both handled: culture-sensitive case-insensitive matching
(fixed by `CultureInvariant`) and NFC/NFD mismatch (fixed by normalizing both sides). Two
that are *not* fixable are simply avoided: ICU folds `ß` to `ss` and .NET does not, and
.NET's `.` splits surrogate pairs. Neither is reachable from the patterns this code builds.

**Still unverified — one link, and it needs a person.** Every stage of the pipeline has now run
on real hardware, but never with real *speech* in it: microphone audio containing a human voice
has not been transcribed. Audio capture is proven, and transcription is proven from files, but
the join between them is not.

Acoustic loopback was tried and does not substitute: playing the model's own `es.wav` through
the speakers while the hook held the key produced a peak level of 0.011 against 0.163 for
ordinary room noise. Capture requests `Role.Communications`, and that pipeline applies echo
cancellation — Windows is removing the speaker signal on purpose. A real voice is the only way.

Also still unverified, and harder to stage: the OS microphone-privacy block, and a device
unplugged mid-capture.

**The next step is one short spoken utterance into Notepad.** Everything around it is now
proven, so if that works, the app works.
