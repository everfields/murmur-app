using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Murmur.Core;

/// <summary>Virtual-key codes the push-to-talk trigger may take.</summary>
/// <remarks>
/// <para>
/// <b>The default is <see cref="None"/>: there is no global trigger key at present.</b> Every
/// candidate so far has been a key people press for other reasons all day. Right Shift was
/// bound and then withdrawn — it is a primary typing key, so dictation fired on capital
/// letters and interrupted the user constantly. Rather than move the problem to the next
/// key, recording is started from the app until a key has been chosen deliberately.
/// </para>
/// <para>
/// The other codes stay selectable in Settings for anyone who wants one; they are simply
/// not chosen for the user.
/// </para>
/// </remarks>
public static class PushToTalkKeys
{
    /// <summary>No global trigger: recording starts from the app.</summary>
    public const int None = 0;

    /// <summary>Right Ctrl — produces no character on any keyboard layout.</summary>
    public const int RightControl = 0xA3;

    /// <summary>Caps Lock.</summary>
    public const int CapsLock = 0x14;

    /// <summary>F13 — present on many full-size and gaming keyboards, bound to nothing.</summary>
    public const int F13 = 0x7C;

    /// <summary>Right Alt — AltGr on many European layouts. Offered last, with a warning.</summary>
    public const int RightAlt = 0xA5;

    /// <summary>
    /// Right Shift, withdrawn as a trigger.
    /// </summary>
    /// <remarks>
    /// Kept as a named constant purely so <see cref="Sanitize"/> can recognise settings files
    /// written while it was on offer. Nothing may bind it.
    /// </remarks>
    public const int RetiredRightShift = 0xA1;

    /// <summary>
    /// Maps a withdrawn key onto <see cref="None"/>, leaving anything else alone.
    /// </summary>
    /// <remarks>
    /// Applied on load, because the setting outlives the release that offered it: a user who
    /// chose Right Shift has it in <c>settings.json</c>, and without this it would keep
    /// firing on every capital letter after the option had been removed from the UI. Codes
    /// this app never offered are passed through untouched — a hand-edited file is a
    /// deliberate act, not a stale choice.
    /// </remarks>
    /// <param name="key">The stored virtual-key code.</param>
    /// <returns>The code to actually bind.</returns>
    public static int Sanitize(int key) => key == RetiredRightShift ? None : key;
}

/// <summary>User preferences.</summary>
public sealed record SettingsData
{
    /// <summary>
    /// Virtual-key code of the push-to-talk key. Defaults to <see cref="PushToTalkKeys.None"/>.
    /// </summary>
    /// <remarks>
    /// See <see cref="PushToTalkKeys"/> for why there is no default trigger key, and why
    /// <b>Right Alt</b> is a poor choice for one: on German, Polish, UK, Nordic and most
    /// Latin-American layouts it is AltGr, the key those users type <c>@</c>, <c>€</c>,
    /// <c>\</c> and <c>|</c> with.
    /// </remarks>
    public int PushToTalkKey { get; init; } = PushToTalkKeys.None;

    /// <summary>Where the speech model lives, or null to search the default locations.</summary>
    public string? ModelDirectory { get; init; }

    /// <summary>Whether to type the transcript into the focused app.</summary>
    public bool InjectText { get; init; } = true;

    /// <summary>Whether to keep a transcript history.</summary>
    public bool KeepHistory { get; init; } = true;
}

/// <summary>Settings, persisted as JSON.</summary>
public sealed class AppSettings
{
    /// <summary>
    /// The source-generated context, re-bound to options that leave non-ASCII text alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same reasoning as <see cref="TranscriptStore"/>: <see cref="JsonSourceGenerationOptionsAttribute"/>
    /// exposes no <c>Encoder</c>, so the generated <c>Default</c> instance escapes everything
    /// above ASCII. A <see cref="SettingsData.ModelDirectory"/> such as
    /// <c>C:\Modelos\Español</c> would be written as <c>C:\\Modelos\\Espa\u00F1ol</c>, which
    /// defeats the point of a file the user is invited to open.
    /// </para>
    /// <para>
    /// Copying <c>Default.Options</c> into a new context instance keeps the source-generated
    /// resolver, so trimming and single-file publishing still resolve types without reflection.
    /// </para>
    /// <para>
    /// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> is safe here: the "unsafe"
    /// refers to emitting HTML-significant characters unescaped, and this JSON is written to
    /// a local file read back by this same serializer — it is never embedded in HTML or in a
    /// script context.
    /// </para>
    /// </remarks>
    private static readonly SettingsJsonContext Json = new(
        new JsonSerializerOptions(SettingsJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    private readonly string _path;

    /// <summary>Loads settings from <paramref name="path"/>, or defaults if absent.</summary>
    public AppSettings(string path)
    {
        _path = path;
        Data = Load(path);
    }

    /// <summary>The default location.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Murmur", "settings.json");

    /// <summary>Current values.</summary>
    public SettingsData Data { get; private set; }

    /// <summary>Raised after a successful save.</summary>
    public event EventHandler? Changed;

    /// <summary>Replaces and persists the settings.</summary>
    public void Update(SettingsData data)
    {
        Data = data;

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(data, Json.SettingsData));

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static SettingsData Load(string path)
    {
        // Corrupt or unreadable settings must never stop the app launching — defaults are
        // always a working configuration.
        try
        {
            if (!File.Exists(path)) return new SettingsData();

            var data = JsonSerializer.Deserialize(File.ReadAllText(path), Json.SettingsData)
                       ?? new SettingsData();

            // Every read goes through here, so a withdrawn key cannot reach the hook however
            // old the file is.
            var key = PushToTalkKeys.Sanitize(data.PushToTalkKey);
            return key == data.PushToTalkKey ? data : data with { PushToTalkKey = key };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsData();
        }
    }
}

/// <summary>Source-generated JSON for settings.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SettingsData))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
