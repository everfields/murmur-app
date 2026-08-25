import Foundation

/// One correction that actually fired, kept so history can show whether the dictionary is
/// earning its place.
public struct AppliedCorrection: Codable, Hashable, Sendable {
    /// The text as the engine produced it.
    public let from: String
    /// What it was rewritten to.
    public let to: String
    /// How many times it fired in this transcript.
    public let count: Int
}

/// Rewrites transcribed text using the dictionary's correction pairs.
///
/// This is the guaranteed half of the dictionary. Engine biasing is a nudge — it raises the
/// odds of the right word and promises nothing — so anything that must be correct has to be
/// fixed here, after the fact, deterministically.
///
/// Four rules, all load-bearing:
///
/// **Longest match first.** "Claude Code" is applied before "Claude", so the longer rule
/// isn't pre-empted by a shorter one that overlaps it.
///
/// **Whole matches only.** Every pattern is fenced by word boundaries, so a rule for
/// "cloud code" can never touch "Cloudflare" or the ordinary word "cloud".
///
/// **Glued words still match.** Engines run words together — "CloudCode", "cloud-code" — so
/// the gap between the parts of a phrase is matched as *optional* whitespace or hyphens
/// rather than a literal space.
///
/// **Diacritics are folded, both ways.** "nunez", "nuñez" and "núñez" are one and the same
/// trigger, and any of them matches any of those spellings in the text. Without this a Spanish
/// user has to hand-enumerate every accented variant of every name, because the engine writes
/// "Andújar" while their rule says "andujar". The replacement is always inserted exactly as the
/// user wrote it.
public struct DictionaryCorrector: Sendable {
    private let rules: [Rule]

    private struct Rule: Sendable {
        let regex: NSRegularExpression
        let replacement: String
        let trigger: String
    }

    public init(entries: [DictionaryEntry]) {
        // Longest trigger first. Sorting by the trigger's length is what makes "Claude Code"
        // win over "Claude" — once the longer rule has rewritten the span, the shorter one
        // no longer sees the text it would have matched.
        let corrections = entries
            .filter { $0.isEnabled && $0.kind == .correction }
            .filter { !$0.hear.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .sorted { $0.hear.count > $1.hear.count }

        rules = corrections.compactMap { entry in
            guard let regex = Self.makeRegex(for: entry.hear) else { return nil }
            return Rule(
                regex: regex,
                // NFC the replacement as well. It is injected verbatim and every shorter rule
                // then runs over the result, so a decomposed replacement — a dictionary.txt
                // authored on macOS, where Cocoa hands back NFD readily — would seed combining
                // marks into text the later rules have to match against.
                replacement: NSRegularExpression.escapedTemplate(
                    for: entry.write.precomposedStringWithCanonicalMapping
                ),
                trigger: entry.hear
            )
        }
    }

    public var isEmpty: Bool { rules.isEmpty }

    /// Applies every rule in order.
    ///
    /// - Returns: the rewritten text, plus one `AppliedCorrection` per rule that fired.
    public func apply(to text: String) -> (text: String, applied: [AppliedCorrection]) {
        guard !text.isEmpty else { return (text, []) }

        // Normalize to NFC before matching. macOS hands back decomposed (NFC vs NFD) strings
        // in several places — a filesystem read of the dictionary being the obvious one — and
        // "café" decomposed is five scalars where composed is four. The pattern and the text
        // must be in the same form or an accented trigger silently never matches. The Windows
        // implementation normalizes identically; this is part of the shared contract.
        var result = text.precomposedStringWithCanonicalMapping

        // Normalizing *before* the empty-rule-set check, not after, is deliberate. The normal
        // form of the text this app stores and injects must not depend on whether the user
        // happens to own any rules: a default install has an empty dictionary and still has to
        // emit NFC, or the same dictation produces different bytes for different users.
        guard !rules.isEmpty else { return (result, []) }

        var applied: [AppliedCorrection] = []

        for rule in rules {
            let range = NSRange(result.startIndex..., in: result)
            let matches = rule.regex.numberOfMatches(in: result, range: range)
            guard matches > 0 else { continue }

            // Record what the engine actually produced, not the rule's trigger — seeing the
            // real mishearing is the point, and it can differ from the trigger in case or
            // spacing ("CloudCode" matched by "cloud code").
            let firstMatch = rule.regex.firstMatch(in: result, range: range)
            let heard = firstMatch
                .flatMap { Range($0.range, in: result) }
                .map { String(result[$0]) } ?? rule.trigger

            result = rule.regex.stringByReplacingMatches(
                in: result,
                range: range,
                withTemplate: rule.replacement
            )

            applied.append(AppliedCorrection(
                from: heard,
                to: rule.replacement.replacingOccurrences(of: "\\", with: ""),
                count: matches
            ))
        }

        return (result, applied)
    }

    /// Builds the pattern for one trigger phrase.
    ///
    /// The parts are joined with `[\s\-]*` — zero or more spaces or hyphens — which is what
    /// catches "CloudCode" and "Cloud-Code" alongside the spaced form.
    ///
    /// Every letter is then widened into a character class covering its accented relatives, so
    /// that matching ignores diacritics in both directions. See ``expandDiacritics(_:)`` for
    /// why the pattern is widened rather than the text.
    ///
    /// The fences are lookarounds on letters, digits and combining marks rather than `\b`. `\b`
    /// would treat a trailing hyphen or apostrophe as a boundary and let a rule bite into a
    /// longer word; requiring that no letter, digit or mark sits on either side is the stricter
    /// guarantee, and it's what keeps "cloud code" off "Cloudflare".
    private static func makeRegex(for trigger: String) -> NSRegularExpression? {
        // NFC here too, matching `apply(to:)` — a trigger typed into the UI and a trigger read
        // back from the dictionary file can arrive in different normal forms.
        let parts = trigger
            .precomposedStringWithCanonicalMapping
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .split(whereSeparator: { $0 == " " || $0 == "-" || $0 == "\t" })
            .map { Self.expandDiacritics(NSRegularExpression.escapedPattern(for: String($0))) }

        guard !parts.isEmpty else { return nil }

        let body = parts.joined(separator: "[\\s\\-]*")

        // \p{M} sits in the fence alongside letters and digits because a combining mark is part
        // of the word it hangs off. NFC leaves a mark standing wherever no precomposed form
        // exists, and a decomposed "café" reaching the matcher would otherwise let a trigger of
        // "cafe" bite off the base letters — U+0301 is not a letter, so the old fence saw a
        // word boundary where a reader sees the middle of a word.
        let pattern = "(?<![\\p{L}\\p{N}\\p{M}])\(body)(?![\\p{L}\\p{N}\\p{M}])"

        return try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
    }
}

// MARK: - Diacritic folding

private extension DictionaryCorrector {
    /// The folding table: one string per equivalence class, holding the base letter followed by
    /// every accented relative of it in the Latin-1 Supplement and Latin Extended-A blocks,
    /// lower and upper case interleaved.
    ///
    /// **Duplicated by design.** The same table, in the same order, is repeated verbatim in
    /// `DictionaryCorrector.cs`. The two platforms share no code — only
    /// `shared/dictionary-test-vectors.json` — so the two copies have to be edited together,
    /// and a change made here alone is a live bug on the other platform.
    ///
    /// Both cases are spelled out rather than left to `.caseInsensitive` to supply the uppercase
    /// halves. ICU and .NET do not fold every character identically, and listing the class in
    /// full makes both engines match the same set whatever their folding tables happen to say.
    ///
    /// Deliberate omissions. Letters with no accented relatives — b, f, m, p, q, v, x — are
    /// absent, so they are emitted literally and an ASCII trigger against ASCII text builds a
    /// pattern that behaves exactly as it did before folding existed. Multi-letter equivalences
    /// (ß, æ, œ) are out of scope: they are not one letter plus a mark. Separate letters that
    /// merely look accented (ð, þ, ŋ) are not variants of anything. And ı/İ are left out on
    /// purpose — dotless and dotted I are the one pair ICU and .NET are known to disagree about,
    /// and a character that behaves differently on the two platforms is exactly what the shared
    /// vectors exist to keep out.
    static let diacriticClasses: [String] = [
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
    ]

    /// Every scalar in ``diacriticClasses``, mapped to the class it belongs to.
    ///
    /// Keyed by `Unicode.Scalar` rather than `Character` on purpose: C# walks UTF-16 code units,
    /// and a `Character` is a whole grapheme cluster, so a trigger carrying a mark that NFC
    /// cannot compose — "e" plus two accents — would be one `Character` here and two `char`s
    /// there. Scalars keep the two walks in step.
    static let classByScalar: [Unicode.Scalar: String] = {
        var index: [Unicode.Scalar: String] = [:]
        for characterClass in DictionaryCorrector.diacriticClasses {
            for scalar in characterClass.unicodeScalars {
                precondition(index[scalar] == nil, "diacritic class table lists \(scalar) twice")
                index[scalar] = characterClass
            }
        }
        return index
    }()

    /// Widens an already-escaped literal so that every letter also matches its accented
    /// relatives: `n` becomes `[nNñÑ…]`, and `ñ` becomes that very same class. Mapping the plain
    /// and the accented letter onto one class is what makes the folding symmetric — "nunez",
    /// "nuñez" and "núñez" all build the identical pattern.
    ///
    /// Widening the *pattern* rather than folding the *text* is a deliberate choice. Stripping
    /// accents from the haystack would change its length and shift every offset after the first
    /// accented character, and `apply(to:)` reports the matched range verbatim — the span the
    /// engine actually produced — which has to remain a genuine substring of the real input.
    /// Character classes leave the input untouched, so every offset and length stays exact.
    ///
    /// The cost is real and worth stating outright: a rule for "ano" will now also fire on
    /// "año". Accent-blind matching cannot tell the two apart. For a dictionary of names and
    /// technical terms — which is what this feature is for — catching "Andújar" from a trigger
    /// of "andujar" is worth the occasional over-eager hit, and
    /// `shared/dictionary-test-vectors.json` pins that behaviour down so it stays a decision
    /// rather than a discovery.
    static func expandDiacritics(_ escaped: String) -> String {
        let scalars = Array(escaped.unicodeScalars)
        var pattern = ""
        pattern.reserveCapacity(scalars.count * 8)

        var i = 0
        while i < scalars.count {
            let scalar = scalars[i]

            // escapedPattern(for:) emits two-scalar escapes: "\." for a dot, but "\n" for a
            // newline too. The scalar after a backslash belongs to the escape, so expanding it
            // would turn "\n" into "\[nNñÑ…]" — a literal open bracket — and change the
            // pattern's meaning entirely. Copy both across untouched.
            if scalar.value == 0x5C, i + 1 < scalars.count {
                pattern.unicodeScalars.append(scalar)
                pattern.unicodeScalars.append(scalars[i + 1])
                i += 2
                continue
            }

            if let characterClass = DictionaryCorrector.classByScalar[scalar] {
                pattern += "[" + characterClass + "]"
            } else {
                pattern.unicodeScalars.append(scalar)
            }

            i += 1
        }

        return pattern
    }
}

// MARK: - Engine biasing

public extension DictionaryCorrector {
    /// The phrases to hand the speech engine as context before it transcribes.
    ///
    /// Kept deliberately short. These models drift when given a long context list — on quiet
    /// or ambiguous audio they start inventing text from the vocabulary they were primed
    /// with, which is a far worse failure than the misspelling it was meant to fix.
    public static let biasLimit = 40

    /// - Returns: the correct spellings — `.term` words and the *write* side of corrections —
    ///   most recently useful first, capped at `biasLimit`.
    public static func biasPhrases(from entries: [DictionaryEntry]) -> [String] {
        var seen = Set<String>()
        var phrases: [String] = []

        for entry in entries where entry.isEnabled {
            let phrase = entry.write.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !phrase.isEmpty, seen.insert(phrase.lowercased()).inserted else { continue }
            phrases.append(phrase)
            if phrases.count == biasLimit { break }
        }

        return phrases
    }
}
