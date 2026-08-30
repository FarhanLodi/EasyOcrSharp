using System.Text;

namespace EasyOcrSharp.Structure.Engine.Recognition;

/// <summary>
/// Loads a PaddleOCR character dictionary (a <c>ppocr_keys</c> / <c>*_dict.txt</c> file, one character
/// per line) into the exact ordered CTC vocabulary the recognizer emits.
/// <para>
/// PaddleOCR's convention: the recognizer's class 0 is the CTC blank, classes 1..N are the dictionary
/// lines in order, and a trailing space character is appended as the last class. So the returned vocab is
/// <c>["blank"] + lines + [" "]</c> and its <c>Count</c> equals the model's number of output classes.
/// </para>
/// <para>
/// This differs from the EasyOCR/RapidOCR placeholder convention only in the blank token's <em>spelling</em>:
/// the reference RapidOcrNet recognizer materializes its label array as <c>["#"] + lines + [" "]</c>, where
/// <c>"#"</c> is a stand-in for the blank at index 0. The index is identical (0) in both — Paddle prepends a
/// dedicated blank class — so decoding logic that drops index 0 is portable between them.
/// </para>
/// </summary>
internal static class CharacterDictionary
{
    /// <summary>
    /// The token placed at index 0 of every Paddle recognizer vocabulary (the CTC blank).
    /// </summary>
    public const string Blank = "blank";

    /// <summary>
    /// Reads the newline-delimited dictionary file at <paramref name="dictPath"/> and returns the Paddle
    /// vocabulary: <c>["blank"]</c> + the file's lines (in order) + a trailing <c>" "</c>.
    /// </summary>
    /// <param name="dictPath">Path to the ppocr dictionary file (UTF-8, one character/token per line).</param>
    /// <returns>The ordered vocabulary; index 0 is the blank, the last entry is a space.</returns>
    /// <exception cref="ArgumentException"><paramref name="dictPath"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">No dictionary file exists at <paramref name="dictPath"/>.</exception>
    public static IReadOnlyList<string> Load(string dictPath)
    {
        return FromLines(LoadLines(dictPath));
    }

    /// <summary>
    /// Reads the raw dictionary lines from <paramref name="dictPath"/> in CTC index order, without applying
    /// any blank/space convention. Use with <see cref="BuildVocab"/> when the model's output class count is
    /// known, so the vocabulary can be matched to the network exactly (community ONNX exports disagree on
    /// whether the file already includes the blank/space classes).
    /// </summary>
    public static IReadOnlyList<string> LoadLines(string dictPath)
    {
        if (string.IsNullOrEmpty(dictPath))
            throw new ArgumentException("Dictionary path must be provided.", nameof(dictPath));
        if (!File.Exists(dictPath))
            throw new FileNotFoundException("Character dictionary file not found.", dictPath);

        // ReadAllLines splits on \r\n / \n / \r and drops a single trailing empty from a final newline,
        // matching PaddleOCR's own loader. We preserve order and content (including interior blank lines).
        return File.ReadAllLines(dictPath, Encoding.UTF8);
    }

    /// <summary>
    /// Builds the CTC vocabulary so that its length equals the recognizer's output class count, auto-detecting
    /// which blank/space convention the dictionary file follows. Community PP-OCRv5 exports are inconsistent:
    /// the default <c>ppocrv5_dict.txt</c> is the <em>complete</em> class list already (index 0 is the blank),
    /// the per-script dicts (<c>cyrillic_dict.txt</c>, <c>latin_dict.txt</c>, …) carry the blank as an empty
    /// first line but leave the trailing space off, and a plain upstream <c>ppocr</c> dict omits both.
    /// Matching the file's line count against <paramref name="numClasses"/> — and, when it is short by exactly
    /// one class, sniffing whether the first line is empty — picks the right construction and avoids the
    /// off-by-one that shifts every decoded character.
    /// </summary>
    /// <param name="lines">Raw dictionary lines (see <see cref="LoadLines"/>).</param>
    /// <param name="numClasses">The recognizer's number of output classes (last output dim); ≤0 means unknown.</param>
    public static IReadOnlyList<string> BuildVocab(IReadOnlyList<string> lines, int numClasses)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // Unknown class count: fall back to the canonical PaddleOCR convention.
        if (numClasses <= 0)
            return FromLines(lines);

        // The file is already the full class list (index 0 is the blank slot) — use it verbatim.
        if (lines.Count == numClasses)
            return new List<string>(lines);

        // The file is one class short of the network. Two different dictionary shapes land here, and
        // confusing them shifts every decoded character by one class:
        //   * the per-script PP-OCRv5 dicts (latin, cyrillic, arabic, devanagari, korean, japan, th, el,
        //     te, ta, eslav) begin with an *empty* line, which is the blank slot itself — so the class they
        //     omit is the trailing space, not the blank; and
        //   * a dictionary whose first line is a real token omits only the leading blank.
        // The first line tells the two apart.
        if (lines.Count + 1 == numClasses)
        {
            var v = new List<string>(numClasses) { Blank };
            if (lines.Count > 0 && lines[0].Length == 0)
            {
                // Blank slot already occupies index 0: keep classes 1..N-1 aligned with the file and add the
                // trailing space class the file leaves off.
                for (int i = 1; i < lines.Count; i++)
                    v.Add(lines[i]);
                v.Add(" ");
            }
            else
            {
                v.AddRange(lines);
            }
            return v;
        }

        // File omits both the leading blank and the trailing space class (the canonical PaddleOCR layout).
        if (lines.Count + 2 == numClasses)
            return FromLines(lines);

        // Mismatch we don't recognize: build the canonical vocab, then pad/truncate to the class count so
        // indexing stays in-bounds (a wrong glyph is preferable to an out-of-range crash mid-batch).
        var fallback = new List<string>(FromLines(lines));
        if (fallback.Count > numClasses)
            fallback.RemoveRange(numClasses, fallback.Count - numClasses);
        else
            while (fallback.Count < numClasses) fallback.Add(string.Empty);
        return fallback;
    }

    /// <summary>
    /// Builds a per-class "selectable" mask over an already-built CTC <paramref name="vocab"/> that the
    /// decoder consults to honor a recognition <c>Allowlist</c>/<c>Blocklist</c>. The mask is indexed by
    /// class index (parallel to <paramref name="vocab"/>) and is intended to be built once per recognition
    /// call so the decode hot loop stays allocation-free.
    /// <para>
    /// Semantics (matching <see cref="Models.RecognitionOptions.Allowlist"/> /
    /// <see cref="Models.RecognitionOptions.Blocklist"/>):
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Returns <c>null</c> when both <paramref name="allowlist"/> and <paramref name="blocklist"/> are
    /// null/empty — the caller then decodes without any masking (byte-identical to the unfiltered path).
    /// </description></item>
    /// <item><description>
    /// <b>Allowlist</b>: when non-empty, only classes whose vocab token is in the allowlist are selectable.
    /// The CTC <see cref="Blank"/> (index 0) is <em>always</em> selectable regardless of the allowlist —
    /// removing it would break best-path decoding. The literal space class stays selectable when it is part
    /// of the dictionary and the caller did not explicitly block it (an allowlist that omits space still lets
    /// the model separate words; block space explicitly to suppress it).
    /// </description></item>
    /// <item><description>
    /// <b>Blocklist</b>: any class whose vocab token is in the blocklist is made non-selectable. The blocklist
    /// is applied after the allowlist, so it wins on conflict (a token in both lists is blocked). The blank is
    /// never blocked even if it somehow appears in the list, so decoding always terminates.
    /// </description></item>
    /// </list>
    /// Matching is by exact token string, so multi-character dictionary tokens (PaddleOCR dicts are one token
    /// per line, occasionally multi-char) are matched as a whole rather than per character.
    /// </summary>
    /// <param name="vocab">The CTC vocabulary (index 0 is the blank) the mask is parallel to.</param>
    /// <param name="allowlist">Tokens to permit, or null/empty for "no allowlist".</param>
    /// <param name="blocklist">Tokens to forbid, or null/empty for "no blocklist".</param>
    /// <returns>
    /// A <c>bool[]</c> of length <c><paramref name="vocab"/>.Count</c> where <c>true</c> means the class may be
    /// emitted, or <c>null</c> when no filtering is requested.
    /// </returns>
    public static bool[]? BuildSelectableMask(
        IReadOnlyList<string> vocab,
        IReadOnlyCollection<string>? allowlist,
        IReadOnlyCollection<string>? blocklist)
    {
        ArgumentNullException.ThrowIfNull(vocab);

        bool hasAllow = allowlist is { Count: > 0 };
        bool hasBlock = blocklist is { Count: > 0 };
        if (!hasAllow && !hasBlock)
            return null;

        // Ordinal sets so matching is culture-independent and O(1) per token.
        var allow = hasAllow ? new HashSet<string>(allowlist!, StringComparer.Ordinal) : null;
        var block = hasBlock ? new HashSet<string>(blocklist!, StringComparer.Ordinal) : null;
        bool spaceBlocked = block is not null && block.Contains(" ");

        var mask = new bool[vocab.Count];
        for (int i = 0; i < vocab.Count; i++)
        {
            string token = vocab[i];

            // The CTC blank (index 0) must always survive — without it the best-path decode cannot separate
            // characters and effectively stops emitting. It is never subject to allow/block.
            if (i == 0)
            {
                mask[i] = true;
                continue;
            }

            bool selectable;
            if (allow is not null)
            {
                // Allowlist active: permit only listed tokens, plus the space class (so word separation keeps
                // working) unless the caller explicitly blocked space.
                selectable = allow.Contains(token) || (token == " " && !spaceBlocked);
            }
            else
            {
                selectable = true;
            }

            // Blocklist wins on conflict.
            if (block is not null && block.Contains(token))
                selectable = false;

            mask[i] = selectable;
        }

        return mask;
    }

    /// <summary>
    /// Builds the Paddle vocabulary directly from an in-memory ordered set of dictionary lines, applying
    /// the same <c>["blank"] + lines + [" "]</c> convention as <see cref="Load(string)"/>.
    /// </summary>
    /// <param name="lines">The dictionary entries in CTC label order (excluding blank and trailing space).</param>
    /// <returns>The ordered vocabulary; index 0 is the blank, the last entry is a space.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is null.</exception>
    public static IReadOnlyList<string> FromLines(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // ["blank"] (index 0, the CTC blank) + dictionary lines (indices 1..N) + " " (the last class).
        var vocab = new List<string>(lines.Count + 2) { Blank };
        vocab.AddRange(lines);
        vocab.Add(" ");
        return vocab;
    }
}
