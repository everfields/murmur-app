# Getting the speech model on Windows

Windows has **no equivalent to Apple's `SpeechAnalyzer`**. On macOS that framework ships with
the OS, manages its own model assets, and needs no download. There is nothing comparable on
Windows — so Parakeet is not the optional upgrade it is on macOS, it is the *only* engine.
The app cannot transcribe until these files are on disk.

This page is written to be followed by a person **or handed to a coding agent verbatim**.

---

## What you are downloading

**NVIDIA Parakeet TDT 0.6B**, converted to ONNX and quantized to int8, run through
[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx).

| | |
|---|---|
| Working set once loaded | **~725 MB**, ~808 MB peak — see [Memory](#memory) |
| Model load | **7.5–10 s**, once per process launch — see [Speed](#speed) |
| Speed | **~6–8× real time** on a Core Ultra 7 165H; a 5 s utterance comes back in well under a second |
| Network | Needed **once**, for the download. Transcription is fully offline. |
| Licence | Model weights **CC-BY-4.0**; sherpa-onnx **Apache-2.0**. Commercial use permitted with attribution — see [Licence](#licence). |

Two variants:

| Repo | Languages | `tokens.txt` | Download |
|---|---|---|---|
| `csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8` | **25 European languages**, Spanish among them | 8193 entries | **~670 MB** |
| `csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8` | **English only** | 1025 entries | **~661 MB** |

**Take v3 unless you are certain you will only ever dictate in English.** The app looks for
`parakeet-v3` **first** and only then `parakeet-v2`, so a machine carrying both gets the
multilingual one.

The encoder — where nearly all the compute and nearly all the bytes go — is the same size in
both. What the bigger vocabulary costs is the two small files:

| File | v3 | v2 |
|---|---|---|
| `encoder.int8.onnx` | 652 MB | 652 MB |
| `decoder.int8.onnx` | **11.8 MB** | 7.3 MB |
| `joiner.int8.onnx` | **6.4 MB** | 1.7 MB |
| `tokens.txt` | 94 KB | 9 KB |

That difference matters in exactly one place — the size check below, which has to know which
variant you chose or it will call a perfectly good v3 download corrupt.

### There is no language setting

You will go looking for one. There isn't one, in this app or in sherpa-onnx: the
configuration for v3 is identical to the one for v2 — same `nemo_transducer` model type,
same `FeatureDim = 128`, no language or locale field anywhere. **The model identifies the
spoken language itself.** Switching languages means switching model folders, and nothing
else. If Spanish comes out as English-sounding nonsense, you are running v2.

Verified on this hardware (Core Ultra 7 165H, Windows 11 26100) with the v3 model and the
exact configuration below: the Spanish, French, German and English clips shipped in the
model's own `test_wavs/` all transcribed correctly, with no configuration change of any kind
between them. The Spanish clip came back as

> No preguntes que puede hacer tu país por ti, pregunta qué puedes hacer tú por tu país.

with the accented characters correct as code points (U+00ED, U+00E9), not merely correct on
screen — a console that mangles the rendering will make a correct transcript look broken, so
check the bytes before concluding the model is at fault.

---

## Where the files go

```
%LOCALAPPDATA%\Murmur\models\parakeet-v3\
    encoder.int8.onnx     ~652 MB
    decoder.int8.onnx     ~11.8 MB
    joiner.int8.onnx      ~6.4 MB
    tokens.txt            ~94 KB
```

Four files, flat in the folder. No subdirectories, no renaming.

Search order is **variant-major** — every location for v3 before any location for v2:

1. `%LOCALAPPDATA%\Murmur\models\parakeet-v3\`
2. `<app>\models\parakeet-v3\` (`AppContext.BaseDirectory`)
3. `%LOCALAPPDATA%\Murmur\models\parakeet-v2\`
4. `<app>\models\parakeet-v2\`

That order is deliberate: a stale English-only copy sitting next to the executable must not
shadow a multilingual one you just installed under `%LOCALAPPDATA%`. Settings → Model shows
the resolved path, which variant it recognised, and whether the files were found.

> **Do not put these inside the app folder if you install to `Program Files`.** That location
> needs administrator rights to write, so the app cannot download or update the model itself.

---

## Pick a download route

> **If your network blocks `huggingface.co`, go straight to
> [Option B — GitHub mirror](#option-b).** On a corporate network the Hugging Face domain is
> a common target for web filtering; observed on one such network, the download did not fail
> outright but returned a **302 redirect to a `globalservs.com` block page**, so `curl` cheerfully
> wrote an HTML error page into `encoder.int8.onnx`. `github.com` was allowed on the same
> network, and the sherpa-onnx project publishes the identical models there.

Options A, C and D use Hugging Face and are the shortest path on an unfiltered network.

---

## Option A — PowerShell, no extra tools *(recommended)*

Nothing to install. Paste this into PowerShell:

```powershell
$dir  = "$env:LOCALAPPDATA\Murmur\models\parakeet-v3"
$base = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main"

New-Item -ItemType Directory -Force $dir | Out-Null

foreach ($f in "encoder.int8.onnx","decoder.int8.onnx","joiner.int8.onnx","tokens.txt") {
    Write-Host "Downloading $f ..."
    curl.exe -L --fail --progress-bar -o "$dir\$f" "$base/$f"
}

Get-ChildItem $dir | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
```

For the English-only build, change `parakeet-v3` → `parakeet-v2` and `...-v3-int8` →
`...-v2-int8`.

> **`curl.exe`, with the `.exe`.** Bare `curl` in PowerShell is an alias for
> `Invoke-WebRequest`, which is a different program. And if you do use
> `Invoke-WebRequest`, you **must** pass `-OutFile` — without it PowerShell buffers the
> whole 650 MB in memory.

Expected output (PowerShell's `MB` is mebibytes, so the numbers run a few percent below the
decimal sizes quoted above):

```
Name                MB
----                --
decoder.int8.onnx    11.3
encoder.int8.onnx   622.0
joiner.int8.onnx      6.1
tokens.txt            0.1
```

`--fail` makes `curl` exit non-zero on an HTTP error rather than writing the error body into
the file. It does not save you from a filter that answers with a *successful* block page —
that is the 302 case above, and it is why the size check below exists.

---

## <a id="option-b"></a>Option B — GitHub mirror *(use this if Hugging Face is blocked)*

The sherpa-onnx project publishes the same models as **GitHub release assets**, one archive
per model, under the `asr-models` release tag:

```powershell
$tmp = "$env:TEMP\parakeet"
$dir = "$env:LOCALAPPDATA\Murmur\models\parakeet-v3"
$url = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2"

New-Item -ItemType Directory -Force $tmp, $dir | Out-Null

curl.exe -L --fail --progress-bar -o "$tmp\model.tar.bz2" $url    # ~487 MB

tar -xjf "$tmp\model.tar.bz2" -C $tmp

$src = "$tmp\sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"
foreach ($f in "encoder.int8.onnx","decoder.int8.onnx","joiner.int8.onnx","tokens.txt") {
    Move-Item -Force "$src\$f" "$dir\$f"
}

Get-ChildItem $dir | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
```

The v2 archive is the same URL with `v3` → `v2`.

Four things worth knowing:

- **`tar` is built into Windows 11** and handles `.tar.bz2` with `-xjf`. No 7-Zip, no WinRAR,
  nothing to install. The archive is ~487 MB compressed, against ~670 MB extracted.
- **The archive does not extract into the layout the app wants.** It produces a folder named
  after the model containing the four files *plus* a `test_wavs/` directory. The four files
  have to be moved into `%LOCALAPPDATA%\Murmur\models\parakeet-v3\`.
- **Keep `test_wavs/`.** It holds `en.wav`, `es.wav`, `fr.wav` and `de.wav`, and it is the
  fastest way to prove an install works without saying a word — see
  [Prove it transcribes](#prove).
- **`curl.exe` may fail with `schannel: CRYPT_E_NO_REVOCATION_CHECK`** behind a
  TLS-inspecting proxy. See below.

### `CRYPT_E_NO_REVOCATION_CHECK`

A TLS-inspecting proxy re-signs traffic with its own certificate, and that certificate
usually carries no reachable CRL or OCSP responder. Schannel cannot confirm the certificate
has not been revoked, and `curl.exe` treats "cannot check" as fatal. The workaround is:

```powershell
curl.exe -L --fail --ssl-no-revoke --progress-bar -o "$tmp\model.tar.bz2" $url
```

**Understand what you are giving up.** `--ssl-no-revoke` disables the *revocation* check
only — the certificate chain is still validated against the trust store, so this is not
`--insecure` and it does not accept an untrusted issuer. What it stops detecting is a
certificate that was issued legitimately and later revoked. On a link you already know is
being inspected by your own employer's proxy, that is a small, bounded loss. Do not carry the
flag over into scripts that talk to anything else.

---

## Option C — Hugging Face CLI

The command is **`hf`**. `huggingface-cli` is deprecated.

```powershell
pip install -U "huggingface_hub[cli]"

hf download csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8 `
  --include "encoder.int8.onnx" "decoder.int8.onnx" "joiner.int8.onnx" "tokens.txt" `
  --local-dir "$env:LOCALAPPDATA\Murmur\models\parakeet-v3"
```

**No Hugging Face token is required.** These repositories are public. If you are prompted to
log in, you have a typo in the repo name.

---

## Option D — git

```powershell
git lfs install
git clone https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8
```

Clones extra files you don't need (fp32 weights, test audio) — roughly 3 GB. Fine if you
already have `git-lfs`; otherwise use Option A. Note this still goes to `huggingface.co`, so
it is no help if that domain is filtered.

---

## Verifying it worked

**Set `$variant` to the one you actually downloaded.** v3's decoder and joiner are several
times the size of v2's, so a table hardcoded to one variant reports the other as corrupt.

```powershell
$variant = "v3"                 # "v3" (multilingual) or "v2" (English only)
$dir     = "$env:LOCALAPPDATA\Murmur\models\parakeet-$variant"

$sizes = @{
  v3 = @{                       # exact bytes, as downloaded
    "encoder.int8.onnx" = 652184281
    "decoder.int8.onnx" = 11845275
    "joiner.int8.onnx"  =  6355277
    "tokens.txt"        =    93939
  }
  v2 = @{
    "encoder.int8.onnx" = 652183000    # approximate, ±1 MB
    "decoder.int8.onnx" =   7257753
    "joiner.int8.onnx"  =   1739080
    "tokens.txt"        =      9384
  }
}
$expect = $sizes[$variant]

foreach ($k in $expect.Keys) {
    $p = Join-Path $dir $k
    if (-not (Test-Path $p)) { Write-Host "MISSING  $k" -ForegroundColor Red; continue }
    $got = (Get-Item $p).Length
    $ok = [math]::Abs($got - $expect[$k]) -lt 2MB
    Write-Host ("{0,-20} {1,12:N0} bytes  {2}" -f $k, $got, $(if($ok){"OK"}else{"SIZE MISMATCH"}))
}
```

The ±2 MB tolerance exists for the v2 encoder figure, which is approximate. It is wide enough
to pass a file that is *slightly* wrong and narrow enough to catch every failure actually seen
— truncation, and a block page written in place of the model.

**A truncated download is the most common failure and it does not announce itself** — a
partial `encoder.int8.onnx` fails at model-load time with an opaque protobuf parse error.
Check the sizes before reporting a bug.

`tokens.txt` should be plain text, one `token id` pair per line, starting:

```
<unk> 0
▁t 1
▁th 2
```

It is also the fastest way to tell the variants apart if you have lost track: **8193 lines is
v3, 1025 is v2.**

```powershell
(Get-Content "$dir\tokens.txt").Count
```

If it opens as HTML, your download was intercepted by a proxy — see
[Option B](#option-b).

### <a id="prove"></a>Prove it transcribes, without speaking

The GitHub tarball ([Option B](#option-b)) ships a `test_wavs/` directory alongside the four
model files: `en.wav`, `es.wav`, `fr.wav`, `de.wav`. Feeding those through the recogniser is
the fastest end-to-end check there is — it exercises the model, the tokens file and your
sherpa-onnx native libraries, and it needs no microphone, no permissions and no hotkey.

All four transcribed correctly here on the v3 model with the configuration in
[For a coding agent](#for-a-coding-agent), unchanged between languages. If English comes back
clean and Spanish does not, you have the v2 model in a folder named `parakeet-v3`.

Two of those clips are **not** 16 kHz — `es.wav` is 22050 Hz and `en.wav` is 24000 Hz — and
they still work: sherpa-onnx logs that it built a resampler and transcribes normally. Do not
"fix" a sample-rate mismatch you see in a log line; it is the library telling you it handled
it.

---

## <a id="memory"></a>Memory

**Measured here, v3 int8 on a Core Ultra 7 165H: 725 MB resident once the model is loaded,
808 MB peak across several dictation-length utterances.** That is the int8 weights plus the
inference runtime's memory arena — the model is resident for as long as the app is, by
design, because reloading it costs the better part of ten seconds.

An earlier figure of ~2 GB for a short utterance circulated in this document. It was not
reproduced on this machine and has been replaced by the numbers above.

| Audio | Peak RAM | |
|---|---|---|
| Dictation-length utterances | **808 MB** | measured, v3, this machine |
| 60 s | 2.5 GB | **not re-measured** on this hardware |
| 300 s | 4.3 GB | **not re-measured** on this hardware |

The two longer-clip rows are carried over from earlier measurements on other hardware and
possibly the other variant. Treat them only as evidence that the footprint grows with clip
length, not as figures for v3. The direction is what matters: dictation is cheap, an hour-long
recording is not.

**At ~800 MB an 8 GB machine is fine.** The app is an ordinary desktop-application tenant,
not a special case.

### The 400-second ceiling

The exported encoder carries a fixed relative-position table sized for **5000 frames**. At
80 ms per frame that is **400 seconds**, and beyond it inference *fails* rather than
degrading:

```
Non-zero status code returned while running Add node.
Attempting to broadcast an axis by a dimension other than 1. 126 by 5126
```

The app splits audio into 30–60 second segments on silence boundaries, so you will not hit
this dictating normally. It matters if you ever point the app at a long recording.

---

## <a id="speed"></a>Speed, hardware and acceleration

All figures below were measured on **one machine** — Lenovo ThinkPad, Intel Core Ultra 7 165H
(16 cores / 22 logical), 32 GB RAM, Windows 11 26100, x64 — with the v3 int8 model. Your
numbers will differ; the shape of the results is the transferable part.

**Transcription: ~6–8× real time.** With the shipped 4-thread setting, warm runs on a 5.33 s
clip took **747–874 ms**, i.e. 6.2–7.1× real time. For dictation that is the number that
matters: a five-second utterance is back in well under a second.

> An earlier "~40× real time" claim appeared in this document and in code comments. It is not
> what this hardware does. If you see 40× quoted anywhere in this repo, it is stale.

**Model load: 7.5–10.3 s, once per process.** 10.3 s on a cold file cache, 7.5–8.1 s on
later process starts. This is why the first transcription after launching the app feels
broken and the rest do not.

### Threads

Measured on `es.wav`, warm, best of three:

| Threads | Real-time factor |
|---|---|
| 2 | 5.1× |
| 4 | 7.1× |
| 6 | 7.6× |
| 8 | **8.0×** |
| 12 | 4.4× |
| 16 | 3.1× |

**4 to 8 is a plateau; past 8 it falls off a cliff.** This is a hybrid CPU — oversubscribing
spills work onto the efficiency cores, and the fast cores then wait on the slow ones. The app
ships **4 threads**, the bottom of the plateau: it gives up a few percent against 8 on a
machine like this one and cannot oversubscribe a smaller CPU, which is the right trade for a
setting nobody will tune.

Note that this replaces an earlier claim that eight threads measured *slower* than four. On
this CPU eight was the fastest configuration tested.

### No GPU

**CPU only. This is deliberate, not a limitation we forgot to lift.**

- sherpa-onnx ships **no GPU package**. Setting a CUDA provider silently falls back to CPU.
- The DirectML runtime is five minor versions behind mainline, forbids parallel inference,
  and wants fixed tensor shapes — which this model, with its variable-length audio input,
  cannot provide.
- CUDA would force every user to install a matching CUDA toolkit.

At sub-second latency for a dictation-length utterance, none of it is worth the dependency.

**ARM64 Windows:** install the ARM64 build of the app. The x64 build runs under emulation
and transcription will be dramatically slower.

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| "Model not found" | Files are in the wrong folder. Settings → Model shows the exact path being checked. |
| Protobuf / parse error on load | Truncated download. Re-run the size check above. |
| Download redirects to a block page (a `globalservs.com` or similar 302) | Your network filters `huggingface.co`. Use the GitHub mirror, [Option B](#option-b). |
| `curl` fails with `schannel: CRYPT_E_NO_REVOCATION_CHECK` | A TLS-inspecting proxy whose certificate has no reachable revocation endpoint. Retry with `--ssl-no-revoke` — [what that costs](#option-b). |
| `SIZE MISMATCH` on `decoder.int8.onnx` / `joiner.int8.onnx` only | The size check is set to the wrong variant. v3's are 11.8 MB / 6.4 MB, v2's 7.3 MB / 1.7 MB. |
| Spanish (or any non-English) comes out as English-sounding nonsense | You are running v2, which is English-only. Install v3; there is no language setting to change. |
| Loads, but every transcript is empty | Windows is blocking microphone access. Settings → Privacy & security → Microphone → **Let desktop apps access your microphone**. WASAPI returns *silence*, not an error, when this is off. |
| First transcription after launch takes ten seconds | Model load: **7.5–10 s** measured, cold file cache at the high end. Once per process launch, not once per utterance. |
| Consistently slow | You may be running the x64 build on an ARM64 machine — or have raised the thread count past 8, which measured *worse* than 4. See [Speed](#speed). |
| Log line about a resampler / a sample rate that isn't 16 kHz | Normal. sherpa-onnx resamples for you; verified with a 22050 Hz and a 24000 Hz clip. |
| Fails on audio over ~7 minutes | The 400-second ceiling above. |

---

## <a id="licence"></a>Licence and attribution

**Model weights: CC-BY-4.0.** Commercial use is permitted. If you redistribute the model
inside a product you must:

1. **Credit NVIDIA**, name the model — **`parakeet-tdt-0.6b-v3`**, the multilingual release
   the app prefers, and `parakeet-tdt-0.6b-v2` if you also ship the English-only one — and
   link both the model card and <https://creativecommons.org/licenses/by/4.0/>.
2. **State that it was modified** — the int8 quantization and ONNX export are modifications.
3. **Add no further restrictions.** If your EULA forbids extracting bundled files, it must
   carve out the model weights.

A `THIRD-PARTY-NOTICES.txt` plus a line in the About box satisfies this.

**sherpa-onnx** is Apache-2.0. **ONNX Runtime** is MIT.

> Not legal advice. CC-BY-4.0 is a content licence rather than a software one and carries no
> patent grant. If you are shipping this commercially, have counsel confirm.

---

## For a coding agent

Minimum to reproduce a working setup:

```
Repo:    csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8   (HuggingFace, public, no token)
Mirror:  https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/
             sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2   (if HF is filtered)
Files:   encoder.int8.onnx, decoder.int8.onnx, joiner.int8.onnx, tokens.txt
Target:  %LOCALAPPDATA%\Murmur\models\parakeet-v3\
NuGet:   org.k2fsa.sherpa.onnx 1.13.5
         org.k2fsa.sherpa.onnx.runtime.win-x64 1.13.5   (or .win-arm64)
```

Substitute `v2` for `v3` throughout to get the English-only model. **The configuration below
is identical either way** — the variant is a choice of folder, nothing more.

Config that actually works — every field verified against running transcriptions, and this
exact configuration produced correct English, Spanish, French and German output from the v3
model on real Windows hardware:

```csharp
var cfg = new OfflineRecognizerConfig();
cfg.FeatConfig.SampleRate  = 16000;
cfg.FeatConfig.FeatureDim  = 128;                  // NOT the default 80
cfg.ModelConfig.Transducer.Encoder = $"{dir}/encoder.int8.onnx";
cfg.ModelConfig.Transducer.Decoder = $"{dir}/decoder.int8.onnx";
cfg.ModelConfig.Transducer.Joiner  = $"{dir}/joiner.int8.onnx";
cfg.ModelConfig.Tokens     = $"{dir}/tokens.txt";
cfg.ModelConfig.ModelType  = "nemo_transducer";    // REQUIRED — omit it and loading fails
cfg.ModelConfig.NumThreads = 4;
cfg.ModelConfig.Provider   = "cpu";
cfg.DecodingMethod         = "greedy_search";
```

Five things that will cost an hour each if missed:

1. **`ModelType = "nemo_transducer"` is mandatory.** Without it the model will not load.
2. **`FeatureDim = 128`**, not the default 80. Same for v3 — do not go looking for a
   multilingual-specific feature config, or for a language field. Neither exists.
3. **Set a `RuntimeIdentifier`** (`-r win-x64`). Without one, native libraries land in
   `runtimes/<rid>/native/` and you get `DllNotFoundException` at runtime.
4. **`WaveReader` is not in the NuGet package** — it's an example helper. Write your own WAV
   parsing, or feed `float[]` samples in `[-1, 1]` directly.
5. **The mirror archive does not extract into the target layout.** `.tar.bz2` gives you a
   folder named after the model, containing the four files and a `test_wavs/` directory. Move
   the four files up into `models\parakeet-v3\`; pointing the app at the extracted folder
   works too, but then the app cannot tell which variant it is and says nothing about
   languages.

Feed **16 kHz mono float32**. That is what the app captures — WASAPI shared mode will
resample for you if you request that format before opening the device — and it avoids a
second resampling pass. It is not a hard requirement: `AcceptWaveform` takes the sample rate
as an argument and sherpa-onnx builds a resampler when it differs, verified here with the
model's own 22050 Hz and 24000 Hz test clips.
