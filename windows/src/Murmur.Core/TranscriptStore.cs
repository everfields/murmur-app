using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Murmur.Dictionary;

namespace Murmur.Core;

/// <summary>One saved dictation.</summary>
public sealed record TranscriptRecord
{
    /// <summary>Stable identity, so one entry can be deleted without matching on its text.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>When the key was released.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>How long the key was held.</summary>
    public double AudioSeconds { get; init; }

    /// <summary>Release to finished text — the wait actually felt.</summary>
    public double ProcessingSeconds { get; init; }

    /// <summary>The final text, after corrections.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Corrections that fired, if any.</summary>
    public IReadOnlyList<AppliedCorrection>? Corrections { get; init; }
}

/// <summary>
/// Transcript history, appended to a JSONL file.
/// </summary>
/// <remarks>
/// <para>
/// One JSON object per line rather than one big array: appending a line is cheap and a
/// truncated write costs one record rather than the whole file. Deleting requires a rewrite,
/// which is fine — it is rare and the file is small.
/// </para>
/// <para>
/// Serialization is source-generated (<see cref="TranscriptJsonContext"/>) so this survives
/// trimming and single-file publishing, where reflection-based JSON quietly stops working.
/// </para>
/// </remarks>
public sealed class TranscriptStore
{
    /// <summary>
    /// The source-generated context, re-bound to options that leave non-ASCII text alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="JsonSourceGenerationOptionsAttribute"/> has no <c>Encoder</c> knob, so the generated
    /// <c>Default</c> instance is stuck with <see cref="JavaScriptEncoder.Default"/> — which
    /// escapes every character outside ASCII. A Spanish transcript then lands on disk as
    /// <c>¿Cómo estás?</c>: it round-trips, but the file stops being readable
    /// by a human and Spanish text costs roughly 2.6x the bytes.
    /// </para>
    /// <para>
    /// Copying <c>Default.Options</c> and handing the copy back to a new context instance
    /// keeps the source-generated resolver in play, so trimming and single-file publishing
    /// still work. Constructing bare options instead would silently fall back to reflection.
    /// </para>
    /// <para>
    /// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> is safe <i>here</i>: the
    /// "unsafe" in the name is about emitting <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c>
    /// unescaped, which only matters when JSON is interpolated into HTML or a
    /// <c>&lt;script&gt;</c> block. This output goes to a local file, is read back by this
    /// same serializer, and is never handed to a browser.
    /// </para>
    /// </remarks>
    private static readonly TranscriptJsonContext Json = new(
        new JsonSerializerOptions(TranscriptJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    private readonly string _path;
    private readonly List<TranscriptRecord> _records = [];

    /// <summary>Opens (and creates if needed) the history at <paramref name="path"/>.</summary>
    public TranscriptStore(string path)
    {
        _path = path;
        Reload();
    }

    /// <summary>The default location.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Murmur", "transcripts.jsonl");

    /// <summary>Every record, newest first.</summary>
    public IReadOnlyList<TranscriptRecord> Records => _records;

    /// <summary>Raised whenever the history changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Re-reads the file.</summary>
    public void Reload()
    {
        _records.Clear();

        if (File.Exists(_path))
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // A single corrupt line must not destroy the whole history — skip it and
                // keep everything else.
                try
                {
                    var record = JsonSerializer.Deserialize(line, Json.TranscriptRecord);
                    if (record is not null) _records.Add(record);
                }
                catch (JsonException)
                {
                    // Skip.
                }
            }
        }

        _records.Reverse();   // newest first, which is how the list reads
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Appends a record.</summary>
    public void Add(TranscriptRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var line = JsonSerializer.Serialize(record, Json.TranscriptRecord);
        File.AppendAllText(_path, line + Environment.NewLine);

        _records.Insert(0, record);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes one record.</summary>
    public void Remove(Guid id)
    {
        _records.RemoveAll(r => r.Id == id);
        Rewrite();
    }

    /// <summary>Deletes everything.</summary>
    public void Clear()
    {
        _records.Clear();
        Rewrite();
    }

    /// <summary>
    /// Case- and accent-insensitive search over transcript text.
    /// </summary>
    /// <remarks>
    /// Dictating "Reunión con Andújar" and later searching <c>andujar</c> has to find it;
    /// nobody reaches for the dead keys inside a search box. See
    /// <see cref="ContainsLoose"/> for why this is not an ordinal comparison.
    /// </remarks>
    public IReadOnlyList<TranscriptRecord> Search(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return _records;

        return _records
            .Where(r => ContainsLoose(r.Text, trimmed))
            .ToList();
    }

    /// <summary>
    /// Substring test that ignores both case and diacritics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CompareOptions.IgnoreNonSpace"/> is the piece that drops the accents, so
    /// <c>andujar</c> matches "Andújar" and <c>seccion</c> matches "sección". It also makes
    /// the comparison normalization-blind for free: text stored as NFD (which is what macOS
    /// hands back from the filesystem and from some IMEs) still matches an NFC query, where
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> compares code units and misses.
    /// </para>
    /// <para>
    /// <see cref="CultureInfo.InvariantCulture"/> rather than the current culture on purpose:
    /// this is a search box, not a sort order. A user whose machine is set to Turkish should
    /// not get different history hits for <c>i</c> than one set to Spanish — locale-specific
    /// casing rules are a source of surprise here, not of correctness.
    /// </para>
    /// </remarks>
    private static bool ContainsLoose(string haystack, string needle) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            haystack, needle, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private void Rewrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Oldest first on disk, so a plain append stays correct next time.
        var lines = _records
            .AsEnumerable()
            .Reverse()
            .Select(r => JsonSerializer.Serialize(r, Json.TranscriptRecord));

        File.WriteAllLines(_path, lines);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Source-generated JSON for the transcript store.
/// </summary>
/// <remarks>
/// Reflection-based serialization breaks under trimming and is flagged by the single-file
/// analyzer. Generating it keeps the published binary honest.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TranscriptRecord))]
[JsonSerializable(typeof(AppliedCorrection))]
public sealed partial class TranscriptJsonContext : JsonSerializerContext;
