using System.Text;
using System.Text.RegularExpressions;

namespace Murmur.Dictionary;

/// <summary>One correction that actually fired.</summary>
public sealed record AppliedCorrection(string From, string To, int Count);

/// <summary>
/// Rewrites transcribed text using the dictionary's correction pairs.
/// </summary>
/// <remarks>
/// <para>
/// This is the guaranteed half of the dictionary. Engine biasing is a nudge — it raises the
/// odds of the right word and promises nothing — so anything that must be correct is fixed
/// here, after the fact, deterministically.
/// </para>
/// <para>
/// A direct counterpart to <c>DictionaryCorrector.swift</c>. The two are independent
/// implementations of one contract, and <c>shared/dictionary-test-vectors.json</c> is what
/// stops them drifting. <b>Change the semantics there first.</b>
/// </para>
/// <para>Four rules, all load-bearing:</para>
/// <list type="number">
/// <item><b>Longest match first.</b> "Claude Code" is applied before "Claude", so the longer
/// rule isn't pre-empted by a shorter one that overlaps it.</item>
/// <item><b>Whole matches only.</b> Every pattern is fenced by word boundaries, so a rule for
/// "cloud code" can never touch "Cloudflare" or the ordinary word "cloud".</item>
/// <item><b>Glued words still match.</b> Engines run words together — "CloudCode",
/// "cloud-code" — so the gap between parts is matched as optional whitespace or hyphens.</item>
/// <item><b>Diacritics are folded, both ways.</b> "nunez", "nuñez" and "núñez" are one and the
/// same trigger, and any of them matches any of those spellings in the text. Without this a
/// Spanish user has to hand-enumerate every accented variant of every name, because the engine
/// writes "Andújar" while their rule says "andujar". The replacement is always inserted exactly
/// as the user wrote it.</item>
/// </list>
/// </remarks>
public sealed class DictionaryCorrector
{
    /// <summary>
    /// Guards against a pathological dictionary hanging a dictation. Matching is linear here,
    /// so this should never trigger; it exists so that a bug can't wedge the app.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly char[] PhraseSeparators = [' ', '-', '\t'];

    private readonly List<Rule> _rules;

    private sealed record Rule(Regex Regex, string Replacement, string Trigger);

    /// <summary>Compiles the enabled correction entries into an ordered rule set.</summary>
    /// <param name="entries">The dictionary. Terms and disabled entries are ignored here.</param>
    public DictionaryCorrector(IEnumerable<DictionaryEntry> entries)
    {
        // Longest trigger first. Sorting by the trigger's length is what makes "Claude Code"
        // win over "Claude" — once the longer rule has rewritten the span, the shorter one no
        // longer sees the text it would have matched.
        //
        // OrderByDescending is a *stable* sort in LINQ, so equal-length triggers keep their
        // file order on both platforms. Swift's sort is not stable, but ties can only occur
        // between triggers of identical length, which cannot overlap the same span twice —
        // so the observable result is the same either way.
        _rules = entries
            .Where(e => e.IsEnabled && e.Kind == EntryKind.Correction)
            .Where(e => !string.IsNullOrWhiteSpace(e.Hear))
            .OrderByDescending(e => e.Hear.Length)
            .Select(e => MakeRule(e.Hear, e.Write))
            .OfType<Rule>()
            .ToList();
    }

    /// <summary>True when no enabled correction entry produced a usable rule.</summary>
    public bool IsEmpty => _rules.Count == 0;

    /// <summary>Applies every rule in order.</summary>
    /// <param name="text">Raw transcribed text.</param>
    /// <returns>The rewritten text, plus one entry per rule that fired.</returns>
    public (string Text, IReadOnlyList<AppliedCorrection> Applied) Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return (text, []);

        // Normalize to NFC before matching, exactly as the Swift side does. Decomposed and
        // composed forms of the same accented word are different sequences of code points —
        // "café" is 4 or 5 depending on form — so an accented trigger silently never fires
        // unless both sides agree. This is part of the shared contract, not an optimisation.
        var result = text.Normalize(NormalizationForm.FormC);

        // Normalizing *before* the empty-dictionary check, not after, is deliberate. The normal
        // form of the text this app stores and injects must not depend on whether the user
        // happens to own any rules: a default install has an empty dictionary and still has to
        // emit NFC, or the same dictation produces different bytes for different users.
        if (_rules.Count == 0) return (result, []);

        var applied = new List<AppliedCorrection>();

        foreach (var rule in _rules)
        {
            var matches = rule.Regex.Matches(result);
            if (matches.Count == 0) continue;

            // Record what the engine actually produced, not the rule's trigger — seeing the
            // real mishearing is the point, and it can differ in case or spacing
            // ("CloudCode" matched by "cloud code").
            var heard = matches[0].Value;

            // MatchEvaluator rather than a replacement string: it makes the replacement
            // strictly literal. A plain Replace would treat "$1", "$&" and friends in the
            // user's own text as substitutions, which is a real hazard when the replacement
            // is arbitrary user input.
            result = rule.Regex.Replace(result, _ => rule.Replacement);

            applied.Add(new AppliedCorrection(heard, rule.Replacement, matches.Count));
        }

        return (result, applied);
    }

    /// <summary>Builds the pattern for one trigger phrase.</summary>
    /// <remarks>
    /// <para>
    /// Parts are joined with <c>[\s\-]*</c> — zero or more spaces or hyphens — which catches
    /// "CloudCode" and "Cloud-Code" alongside the spaced form.
    /// </para>
    /// <para>
    /// Every letter is then widened into a character class covering its accented relatives, so
    /// that matching ignores diacritics in both directions. See
    /// <see cref="ExpandDiacritics"/> for why the pattern is widened rather than the text.
    /// </para>
    /// <para>
    /// The fences are lookarounds on letters, digits and combining marks rather than
    /// <c>\b</c>. <c>\b</c> treats a trailing hyphen or apostrophe as a boundary and would let
    /// a rule bite into a longer word; requiring that no letter, digit or mark sits on either
    /// side is the stricter guarantee, and it's what keeps "cloud code" off "Cloudflare".
    /// </para>
    /// </remarks>
    private static Rule? MakeRule(string trigger, string replacement)
    {
        // NFC here too, matching Apply(): a trigger typed into the UI and one read back from
        // the dictionary file can arrive in different normal forms.
        var parts = trigger
            .Normalize(NormalizationForm.FormC)
            .Trim()
            .Split(PhraseSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => ExpandDiacritics(Regex.Escape(part)))
            .ToArray();

        if (parts.Length == 0) return null;

        var body = string.Join(@"[\s\-]*", parts);

        // \p{M} sits in the fence alongside letters and digits because a combining mark is part
        // of the word it hangs off. NFC leaves a mark standing wherever no precomposed form
        // exists, and a decomposed "café" reaching the matcher would otherwise let a trigger of
        // "cafe" bite off the base letters — U+0301 is not a letter, so the old fence saw a
        // word boundary where a reader sees the middle of a word.
        var pattern = $@"(?<![\p{{L}}\p{{N}}\p{{M}}]){body}(?![\p{{L}}\p{{N}}\p{{M}}])";

        try
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

            // NFC the replacement as well. It is injected verbatim and every shorter rule then
            // runs over the result, so a decomposed replacement — a dictionary.txt authored on
            // macOS, where Cocoa hands back NFD readily — would seed combining marks into text
            // the later rules have to match against.
            return new Rule(regex, replacement.Normalize(NormalizationForm.FormC), trigger);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ---- Diacritic folding ----

    /// <summary>
    /// The folding table: one string per equivalence class, holding the base letter followed by
    /// every accented relative of it in the Latin-1 Supplement and Latin Extended-A blocks,
    /// lower and upper case interleaved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Duplicated by design.</b> The same table, in the same order, is repeated verbatim in
    /// <c>DictionaryCorrector.swift</c>. The two platforms share no code — only
    /// <c>shared/dictionary-test-vectors.json</c> — so the two copies have to be edited
    /// together, and a change made here alone is a live bug on the other platform.
    /// </para>
    /// <para>
    /// Both cases are spelled out rather than left to <see cref="RegexOptions.IgnoreCase"/> to
    /// supply the uppercase halves. .NET and ICU do not fold every character identically, and
    /// listing the class in full makes both engines match the same set whatever their folding
    /// tables happen to say.
    /// </para>
    /// <para>
    /// Deliberate omissions. Letters with no accented relatives — b, f, m, p, q, v, x — are
    /// absent, so they are emitted literally and an ASCII trigger against ASCII text builds a
    /// pattern that behaves exactly as it did before folding existed. Multi-letter equivalences
    /// (ß, æ, œ) are out of scope: they are not one letter plus a mark. Separate letters that
    /// merely look accented (ð, þ, ŋ) are not variants of anything. And ı/İ are left out on
    /// purpose — dotless and dotted I are the one pair .NET and ICU are known to disagree
    /// about, this repo's build settings call that divergence out by name, and a character that
    /// behaves differently on the two platforms is exactly what the shared vectors exist to
    /// keep out.
    /// </para>
    /// </remarks>
    private static readonly string[] DiacriticClasses =
    [
        "aAáÁàÀâÂäÄãÃåÅāĀăĂąĄ",
        "cCçÇćĆĉĈċĊčČ",
        "dDďĎđĐ",
        "eEéÉèÈêÊëËēĒĕĔėĖęĘěĚ",
        "gGĝĜğĞġĠģĢ",
        "hHĥĤħĦ",
        "iIíÍìÌîÎïÏĩĨīĪĭĬįĮ",
        "jJĵĴ",
        "kKķĶ",
        "lLĺĹļĻľĽŀĿłŁ",
        "nNñÑńŃņŅňŇ",
        "oOóÓòÒôÔöÖõÕōŌŏŎőŐøØ",
        "rRŕŔŗŖřŘ",
        "sSśŚŝŜşŞšŠ",
        "tTţŢťŤŧŦ",
        "uUúÚùÙûÛüÜũŨūŪŭŬůŮűŰųŲ",
        "wWŵŴ",
        "yYýÝÿŸŷŶ",
        "zZźŹżŻžŽ",
    ];

    /// <summary>
    /// Every character in <see cref="DiacriticClasses"/>, mapped to the class it belongs to.
    /// </summary>
    /// <remarks>
    /// <c>ToDictionary</c> throws on a duplicate key, so a character listed under two base
    /// letters is a hard failure at first use rather than a silently wrong pattern.
    /// </remarks>
    private static readonly Dictionary<char, string> ClassByCharacter = DiacriticClasses
        .SelectMany(characterClass => characterClass.Select(c => (Character: c, Class: characterClass)))
        .ToDictionary(pair => pair.Character, pair => pair.Class);

    /// <summary>
    /// Widens an already-escaped literal so that every letter also matches its accented
    /// relatives: <c>n</c> becomes <c>[nNñÑ…]</c>, and <c>ñ</c> becomes that very same class.
    /// Mapping the plain and the accented letter onto one class is what makes the folding
    /// symmetric — "nunez", "nuñez" and "núñez" all build the identical pattern.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Widening the <i>pattern</i> rather than folding the <i>text</i> is a deliberate choice.
    /// Stripping accents from the haystack would change its length and shift every offset after
    /// the first accented character, and <see cref="Apply"/> reports <c>matches[0].Value</c> —
    /// the span the engine actually produced — which has to remain a genuine substring of the
    /// real input. Character classes leave the input untouched, so every offset and length
    /// stays exact.
    /// </para>
    /// <para>
    /// The cost is real and worth stating outright: a rule for "ano" will now also fire on
    /// "año". Accent-blind matching cannot tell the two apart. For a dictionary of names and
    /// technical terms — which is what this feature is for — catching "Andújar" from a trigger
    /// of "andujar" is worth the occasional over-eager hit, and
    /// <c>shared/dictionary-test-vectors.json</c> pins that behaviour down so it stays a
    /// decision rather than a discovery.
    /// </para>
    /// </remarks>
    private static string ExpandDiacritics(string escaped)
    {
        var builder = new StringBuilder(escaped.Length * 8);

        for (var i = 0; i < escaped.Length; i++)
        {
            var c = escaped[i];

            // Regex.Escape emits two-character escapes: "\." for a dot, but also "\n" for a
            // newline. The character after a backslash belongs to the escape, so expanding it
            // would turn "\n" into "\[nNñÑ…]" — a literal open bracket — and change the
            // pattern's meaning entirely. Copy both across untouched.
            if (c == '\\' && i + 1 < escaped.Length)
            {
                builder.Append(c).Append(escaped[i + 1]);
                i++;
                continue;
            }

            if (ClassByCharacter.TryGetValue(c, out var characterClass))
            {
                builder.Append('[').Append(characterClass).Append(']');
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    // ---- Engine biasing ----

    /// <summary>
    /// How many phrases to hand the speech engine as context.
    /// </summary>
    /// <remarks>
    /// Deliberately small. These models drift when given a long context list — on quiet or
    /// ambiguous audio they start inventing text from the vocabulary they were primed with,
    /// which is a far worse failure than the misspelling it was meant to fix.
    /// </remarks>
    public const int BiasLimit = 40;

    /// <summary>
    /// The correct spellings — Term words and the <i>write</i> side of corrections — capped
    /// at <see cref="BiasLimit"/>, de-duplicated case-insensitively, in file order.
    /// </summary>
    public static IReadOnlyList<string> BiasPhrases(IEnumerable<DictionaryEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phrases = new List<string>();

        foreach (var entry in entries.Where(e => e.IsEnabled))
        {
            var phrase = entry.Write.Trim();
            if (phrase.Length == 0 || !seen.Add(phrase)) continue;
            phrases.Add(phrase);
            if (phrases.Count == BiasLimit) break;
        }

        return phrases;
    }
}
