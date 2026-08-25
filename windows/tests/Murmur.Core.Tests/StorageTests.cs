using System.Text;
using Murmur.Core;
using Murmur.Dictionary;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>
/// The dictionary's plain-text file format.
/// </summary>
/// <remarks>
/// This format is shared with the macOS build — the same <c>dictionary.txt</c> is meant to
/// work on both — so round-tripping is a compatibility guarantee, not just tidiness.
/// </remarks>
public sealed class DictionaryFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"murmur-dict-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Parses_terms_corrections_comments_and_disabled_entries()
    {
        var entries = DictionaryFile.Parse("""
            # Murmur dictionary
            # a plain comment is ignored

            Anthropic
            cloud code -> Claude Code
            # off: whisper flow -> Wispr Flow
            """);

        entries.Count.ShouldBe(3);

        entries[0].Kind.ShouldBe(EntryKind.Term);
        entries[0].Write.ShouldBe("Anthropic");
        entries[0].IsEnabled.ShouldBeTrue();

        entries[1].Kind.ShouldBe(EntryKind.Correction);
        entries[1].Hear.ShouldBe("cloud code");
        entries[1].Write.ShouldBe("Claude Code");

        // A disabled entry survives as a "# off:" comment rather than vanishing — otherwise
        // switching a rule off would quietly delete it on the next save.
        entries[2].Kind.ShouldBe(EntryKind.Correction);
        entries[2].Hear.ShouldBe("whisper flow");
        entries[2].IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Round_trips_through_the_file()
    {
        var file = new DictionaryFile(_path);
        file.Add(DictionaryEntry.Term("Anthropic"));
        file.Add(DictionaryEntry.Correction("cloud code", "Claude Code"));
        file.Add(DictionaryEntry.Correction("whisper flow", "Wispr Flow") with { IsEnabled = false });

        var reopened = new DictionaryFile(_path);

        reopened.Entries.Count.ShouldBe(3);
        reopened.Entries[1].Hear.ShouldBe("cloud code");
        reopened.Entries[2].IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Malformed_lines_are_skipped_rather_than_throwing()
    {
        // A hand-edited file will contain mistakes. One bad line must not lose the rest.
        var entries = DictionaryFile.Parse("Anthropic\n -> missing left side\nleft side -> \nVercel");

        entries.Count.ShouldBe(2);
        entries.Select(e => e.Write).ShouldBe(["Anthropic", "Vercel"]);
    }

    [Fact]
    public void Search_matches_both_sides_case_insensitively()
    {
        var file = new DictionaryFile(_path);
        file.Add(DictionaryEntry.Correction("cloud code", "Claude Code"));
        file.Add(DictionaryEntry.Term("Vercel"));

        file.Search("CLAUDE").Count.ShouldBe(1);
        file.Search("cloud").Count.ShouldBe(1);
        file.Search("verc").Count.ShouldBe(1);
        file.Search("").Count.ShouldBe(2);
    }

    [Fact]
    public void Search_ignores_accents_as_well_as_case()
    {
        // Nobody types dead keys into a filter box. Searching "seccion" has to find
        // "sección", or a Spanish dictionary becomes unsearchable by its own author.
        var file = new DictionaryFile(_path);
        file.Add(DictionaryEntry.Term("Andújar"));
        file.Add(DictionaryEntry.Correction("sección", "Sección"));
        file.Add(DictionaryEntry.Term("año"));

        file.Search("andujar").Count.ShouldBe(1);
        file.Search("ANDUJAR").Count.ShouldBe(1);
        file.Search("seccion").Count.ShouldBe(1);
        file.Search("ano").Count.ShouldBe(1);

        // Symmetric: the accented query must still find the accented entry.
        file.Search("ANDÚJAR").Count.ShouldBe(1);
        file.Search("Año").Count.ShouldBe(1);
    }

    [Fact]
    public void Search_matches_an_entry_stored_in_decomposed_form()
    {
        // The macOS build shares this file, and macOS hands back NFD. An ordinal search
        // compares code units and would miss this entirely.
        var file = new DictionaryFile(_path);
        file.Add(DictionaryEntry.Term("Andújar".Normalize(NormalizationForm.FormD)));

        file.Search("Andújar").Count.ShouldBe(1);
        file.Search("andujar").Count.ShouldBe(1);
    }

    [Fact]
    public void The_saved_file_has_no_byte_order_mark()
    {
        // The file is documented as byte-compatible with the macOS dictionary.txt and is
        // meant to be hand-edited; an EF BB BF preamble breaks the first and puzzles the
        // second. Encoding.UTF8 (the static property) would emit one.
        var file = new DictionaryFile(_path);
        file.Add(DictionaryEntry.Term("Andújar"));

        StartsWithBom(_path).ShouldBeFalse();
        File.ReadAllBytes(_path)[0].ShouldBe((byte)'#', "the header comment is the very first byte");
    }

    [Fact]
    public void A_file_written_with_a_byte_order_mark_by_an_older_build_still_loads()
    {
        // Existing installs have a BOM-prefixed dictionary. Reading must tolerate it,
        // otherwise the first entry silently gains a U+FEFF and stops matching.
        File.WriteAllText(
            _path,
            "Anthropic" + Environment.NewLine + "cloud code -> Claude Code" + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var file = new DictionaryFile(_path);

        file.Entries.Count.ShouldBe(2);
        file.Entries[0].Write.ShouldBe("Anthropic");
        file.Entries[1].Hear.ShouldBe("cloud code");
    }

    [Fact]
    public void Saving_over_a_file_that_had_a_byte_order_mark_drops_it()
    {
        File.WriteAllText(_path, "Anthropic" + Environment.NewLine, new UTF8Encoding(true));

        var file = new DictionaryFile(_path);
        file.Add(DictionaryEntry.Term("Vercel"));

        StartsWithBom(_path).ShouldBeFalse();
        new DictionaryFile(_path).Entries.Select(e => e.Write).ShouldBe(["Anthropic", "Vercel"]);
    }

    private static bool StartsWithBom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    [Fact]
    public void Accented_entries_survive_the_file_round_trip_unescaped()
    {
        var file = new DictionaryFile(_path);
        file.Add(DictionaryEntry.Correction("reunion con andujar", "Reunión con Andújar"));

        File.ReadAllText(_path).ShouldContain("Reunión con Andújar");
        new DictionaryFile(_path).Entries[0].Write.ShouldBe("Reunión con Andújar");
    }

    [Fact]
    public void Update_and_remove_persist()
    {
        var file = new DictionaryFile(_path);
        var entry = DictionaryEntry.Term("Anthropc");
        file.Add(entry);

        file.Update(entry with { Write = "Anthropic" });
        new DictionaryFile(_path).Entries[0].Write.ShouldBe("Anthropic");

        file.Remove(entry.Id);
        new DictionaryFile(_path).Entries.ShouldBeEmpty();
    }
}

/// <summary>Transcript history persistence.</summary>
public sealed class TranscriptStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"murmur-hist-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static TranscriptRecord Record(string text) => new()
    {
        At = DateTimeOffset.UtcNow,
        AudioSeconds = 2,
        ProcessingSeconds = 0.2,
        Text = text,
    };

    [Fact]
    public void Records_round_trip_newest_first()
    {
        var store = new TranscriptStore(_path);
        store.Add(Record("first"));
        store.Add(Record("second"));

        var reopened = new TranscriptStore(_path);

        reopened.Records.Count.ShouldBe(2);
        reopened.Records[0].Text.ShouldBe("second", "newest first is how the list reads");
    }

    [Fact]
    public void Corrections_survive_the_round_trip()
    {
        var store = new TranscriptStore(_path);
        store.Add(Record("Claude Code") with
        {
            Corrections = [new AppliedCorrection("cloud code", "Claude Code", 2)],
        });

        var reopened = new TranscriptStore(_path);
        var corrections = reopened.Records[0].Corrections;

        corrections.ShouldNotBeNull();
        corrections[0].To.ShouldBe("Claude Code");
        corrections[0].Count.ShouldBe(2);
    }

    [Fact]
    public void A_corrupt_line_does_not_destroy_the_rest_of_the_history()
    {
        var store = new TranscriptStore(_path);
        store.Add(Record("good one"));

        File.AppendAllText(_path, "{ this is not json" + Environment.NewLine);
        store.Add(Record("good two"));

        var reopened = new TranscriptStore(_path);
        reopened.Records.Count.ShouldBe(2, "one bad line must cost one record, not the file");
    }

    [Fact]
    public void Delete_removes_only_the_named_record_and_persists()
    {
        var store = new TranscriptStore(_path);
        store.Add(Record("keep me"));
        store.Add(Record("delete me"));

        var doomed = store.Records.Single(r => r.Text == "delete me");
        store.Remove(doomed.Id);

        new TranscriptStore(_path).Records.Select(r => r.Text).ShouldBe(["keep me"]);
    }

    [Fact]
    public void Appending_after_a_delete_still_reads_back_in_order()
    {
        // Deleting rewrites the file; a later append must not scramble the ordering.
        var store = new TranscriptStore(_path);
        store.Add(Record("one"));
        store.Add(Record("two"));
        store.Remove(store.Records.Single(r => r.Text == "one").Id);
        store.Add(Record("three"));

        new TranscriptStore(_path).Records.Select(r => r.Text).ShouldBe(["three", "two"]);
    }

    /// <summary>Everything a Spanish dictation throws at the encoder, in one string.</summary>
    private const string Spanish = "¿Cómo estás, Andrés? «Sí» — año, niño, Ñu. ¡Qué vergüenza!";

    [Fact]
    public void Spanish_text_round_trips_and_is_stored_unescaped()
    {
        var store = new TranscriptStore(_path);
        store.Add(Record(Spanish));

        var raw = File.ReadAllText(_path);

        // The history file is meant to be readable in any text editor. The default
        // JavaScript encoder escapes everything above ASCII, turning this line into a run
        // of backslash-u00XX escapes — correct, but unreadable and ~2.6x the bytes.
        raw.ShouldContain(Spanish);
        raw.ShouldNotContain("\\u00");
        raw.ShouldNotContain("\\u2014", customMessage: "the em dash must survive as a literal too");

        new TranscriptStore(_path).Records[0].Text.ShouldBe(Spanish, "and it must still parse back");
    }

    [Fact]
    public void Unescaped_output_still_round_trips_through_the_source_generator()
    {
        // Swapping the encoder means deriving a fresh options instance; if that lost the
        // source-generated resolver it would fall back to reflection and break under
        // trimming. Exercising every write path is the cheap way to notice.
        var store = new TranscriptStore(_path);
        store.Add(Record(Spanish) with
        {
            Corrections = [new AppliedCorrection("año pasado", "Año Pasado", 1)],
        });
        store.Add(Record("Sección segunda"));

        // Remove() rewrites the whole file, which is a different serializer call site.
        store.Remove(store.Records.Single(r => r.Text == "Sección segunda").Id);
        store.Add(Record("Ñu"));

        var reopened = new TranscriptStore(_path);

        reopened.Records.Select(r => r.Text).ShouldBe(["Ñu", Spanish]);
        reopened.Records[1].Corrections!.Single().To.ShouldBe("Año Pasado");
        File.ReadAllText(_path).ShouldNotContain("\\u00");
    }

    [Fact]
    public void Search_ignores_accents_in_either_direction()
    {
        var store = new TranscriptStore(_path);
        store.Add(Record("Reunión con Andújar"));
        store.Add(Record("reunion sin andujar"));

        // Unaccented query, accented text.
        store.Search("andujar").Count.ShouldBe(2);

        // Accented, upper-case query against unaccented text — the same rule, mirrored.
        store.Search("ANDÚJAR").Count.ShouldBe(2);
        store.Search("Reunión").Count.ShouldBe(2);
    }

    [Fact]
    public void Search_matches_text_stored_in_decomposed_form()
    {
        var store = new TranscriptStore(_path);
        store.Add(Record("Andújar".Normalize(NormalizationForm.FormD)));

        store.Search("Andújar").Count.ShouldBe(1, "NFD on disk must match an NFC query");
        store.Search("andujar").Count.ShouldBe(1);
    }

    [Fact]
    public void Search_still_excludes_records_that_do_not_match()
    {
        // Ignoring diacritics must not turn the filter into a pass-through.
        var store = new TranscriptStore(_path);
        store.Add(Record("Reunión con Andújar"));
        store.Add(Record("Comprar leche"));

        store.Search("andujar").Single().Text.ShouldBe("Reunión con Andújar");
        store.Search("zaragoza").ShouldBeEmpty();
    }

    [Fact]
    public void Clear_empties_the_file()
    {
        var store = new TranscriptStore(_path);
        store.Add(Record("gone"));
        store.Clear();

        new TranscriptStore(_path).Records.ShouldBeEmpty();
    }
}

/// <summary>Settings persistence.</summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"murmur-set-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Defaults_to_right_ctrl_not_right_alt()
    {
        // Right Alt is AltGr on most European layouts; binding push-to-talk there breaks
        // typing @, €, \ and |. This default is a correctness decision, not a preference.
        new AppSettings(_path).Data.PushToTalkKey.ShouldBe(0xA3);
    }

    [Fact]
    public void Settings_round_trip()
    {
        var settings = new AppSettings(_path);
        settings.Update(settings.Data with { PushToTalkKey = 0x7C, InjectText = false });

        var reopened = new AppSettings(_path);
        reopened.Data.PushToTalkKey.ShouldBe(0x7C);
        reopened.Data.InjectText.ShouldBeFalse();
    }

    [Fact]
    public void An_accented_model_directory_round_trips_and_is_stored_unescaped()
    {
        // settings.json is small enough that people open it to check what the app thinks.
        // A path escaped to "C:\\Modelos\\Espa\u00F1ol" defeats that.
        const string directory = @"C:\Modelos\Español\ñu";

        var settings = new AppSettings(_path);
        settings.Update(settings.Data with { ModelDirectory = directory });

        var raw = File.ReadAllText(_path);
        raw.ShouldContain("Español");
        raw.ShouldNotContain("\\u00");

        new AppSettings(_path).Data.ModelDirectory.ShouldBe(directory);
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults_rather_than_failing_to_launch()
    {
        File.WriteAllText(_path, "{ not json at all");

        new AppSettings(_path).Data.PushToTalkKey.ShouldBe(0xA3);
    }
}
