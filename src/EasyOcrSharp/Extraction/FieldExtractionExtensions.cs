using System.Text;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Extraction;

/// <summary>
/// Turns "text out" into "fields out": finds a label (an <em>anchor</em>) somewhere on the page and reads
/// the value from the geometry around it, with no model, no template and no LLM. Works on any
/// <see cref="OcrResult"/> — the per-line polygons OCR already produces are all it needs.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm, per <see cref="FieldDefinition"/>: fuzzy-match every anchor against every window of
/// words on every line; for each hit, collect the value candidates the definition's
/// <see cref="FieldDefinition.Direction"/> allows — the remainder of the anchor's own line, and the lines
/// sitting right/below/left/above it within <see cref="FieldDefinition.MaxDistance"/> line-heights;
/// discard whatever fails <see cref="FieldDefinition.ValuePattern"/>; score the rest on anchor
/// similarity, distance, alignment and pattern coverage; and keep the one
/// <see cref="FieldDefinition.Occurrence"/> asks for.
/// </para>
/// <para>
/// Distances are measured in multiples of the anchor line's own height, never in pixels, so the same
/// definitions work at any DPI. When a result carries no usable boxes (or a line's box is empty) the
/// geometric directions are skipped and only the inline <c>"Total: 42.00"</c> path runs, so extraction
/// degrades instead of failing. Everything here is a pure function of the result — no I/O, no models, no
/// reflection.
/// </para>
/// </remarks>
public static class FieldExtractionExtensions
{
    /// <summary>
    /// Extracts the requested fields from a recognized page. Definitions that match nothing are simply
    /// absent from the result, so the list is never padded with empty values.
    /// </summary>
    /// <param name="result">The OCR result to read.</param>
    /// <param name="definitions">The fields to look for — hand-written, or from <see cref="FieldPresets"/>.</param>
    /// <param name="options">Matching and scoring options. Null uses <see cref="FieldExtractionOptions.Default"/>.</param>
    /// <returns>One <see cref="ExtractedField"/> per definition that matched, in definition order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="definitions"/> is null.</exception>
    public static IReadOnlyList<ExtractedField> ExtractFields(
        this OcrResult result,
        IEnumerable<FieldDefinition> definitions,
        FieldExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(definitions);

        var defs = definitions as IReadOnlyList<FieldDefinition> ?? definitions.ToArray();
        if (defs.Count == 0 || result.Lines.Count == 0)
        {
            return Array.Empty<ExtractedField>();
        }

        var extractor = new Extractor(result.Lines, options ?? FieldExtractionOptions.Default);
        var fields = new List<ExtractedField>(defs.Count);
        foreach (var definition in defs)
        {
            if (definition is null) continue;
            if (extractor.Extract(definition) is { } field) fields.Add(field);
        }

        return fields;
    }

    /// <summary>
    /// Extracts the requested fields and projects them onto a plain name → value dictionary — the shape
    /// most callers want to hand straight to their own model binder. Fields that matched nothing are
    /// absent; when two definitions share a name the first match wins.
    /// </summary>
    /// <param name="result">The OCR result to read.</param>
    /// <param name="definitions">The fields to look for.</param>
    /// <param name="options">Matching and scoring options. Null uses <see cref="FieldExtractionOptions.Default"/>.</param>
    /// <returns>A dictionary of field name to extracted value.</returns>
    public static IReadOnlyDictionary<string, string> ExtractFieldValues(
        this OcrResult result,
        IEnumerable<FieldDefinition> definitions,
        FieldExtractionOptions? options = null)
        => ExtractFields(result, definitions, options).ToValueMap();

    /// <summary>
    /// Projects extracted fields onto a name → value dictionary (ordinal keys). The first field with a
    /// given name wins, so a preset list stays stable no matter how many definitions share a name.
    /// </summary>
    /// <param name="fields">The extracted fields.</param>
    /// <returns>A dictionary of field name to extracted value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is null.</exception>
    public static IReadOnlyDictionary<string, string> ToValueMap(this IEnumerable<ExtractedField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (field is not null) map.TryAdd(field.Name, field.Value);
        }
        return map;
    }

    // ---- internals ----

    /// <summary>A word of a line, normalized for comparison, with its extent in the original text.</summary>
    private readonly record struct Token(string Text, int Start, int End);

    /// <summary>An anchor phrase, pre-normalized once per extraction call rather than once per line.</summary>
    private readonly record struct Needle(string Original, string Normalized, int TokenCount);

    /// <summary>Where an anchor was found on a line, and where its value would start.</summary>
    private readonly record struct AnchorHit(double Similarity, int ValueStart, string Anchor);

    /// <summary>One scored value candidate. <c>ValueLine</c> is -1 for an inline (same-line) value.</summary>
    private readonly record struct Candidate(
        int AnchorLine,
        int ValueLine,
        FieldMatchKind Kind,
        string Value,
        double Score,
        double Distance,
        double Similarity,
        string Anchor);

    /// <summary>A recognized line plus everything the matcher needs to reason about it.</summary>
    private sealed class LineContext
    {
        public required OcrLine Line { get; init; }
        public required int Index { get; init; }
        public required OcrBoundingBox Box { get; init; }
        public required double Height { get; init; }
        public required Token[] Tokens { get; init; }
    }

    /// <summary>
    /// Holds the per-call state (normalized lines, options, pre-split trim/separator sets) so the
    /// matching helpers stay small and allocate nothing per candidate.
    /// </summary>
    private sealed class Extractor
    {
        private readonly LineContext[] _lines;
        private readonly FieldExtractionOptions _options;
        private readonly char[] _trimChars;
        private readonly char[] _inlineChars;

        public Extractor(IReadOnlyList<OcrLine> lines, FieldExtractionOptions options)
        {
            _options = options;
            _trimChars = options.ValueTrimCharacters.ToCharArray();
            _inlineChars = (options.InlineSeparators + " \t\r\n\u00A0").ToCharArray();
            _lines = BuildContexts(lines);
        }

        public ExtractedField? Extract(FieldDefinition definition)
        {
            var needles = BuildNeedles(definition.Anchors);
            int patternCount = definition.AnchorPatterns?.Count ?? 0;
            if (needles.Length == 0 && patternCount == 0) return null;

            double minSimilarity = Math.Clamp(
                definition.MinAnchorSimilarity ?? _options.MinAnchorSimilarity, 0.0, 1.0);
            bool inlineAllowed = _options.AllowInlineValues
                && (definition.Direction & FieldDirection.SameLine) != 0;
            bool geometryAllowed = (definition.Direction & ~FieldDirection.SameLine) != 0;

            bool found = false;
            Candidate winner = default;

            foreach (var anchor in _lines)
            {
                if (!TryMatchAnchor(anchor, definition, needles, minSimilarity, out var hit)) continue;

                if (inlineAllowed && TryInline(anchor, hit, definition, out var inline))
                {
                    Consider(ref found, ref winner, inline, definition.Occurrence);
                }

                if (!geometryAllowed || anchor.Box.IsEmpty) continue;

                foreach (var other in _lines)
                {
                    if (other.Index == anchor.Index || string.IsNullOrWhiteSpace(other.Line.Text)) continue;
                    if (!TryClassify(anchor, other, definition, out var kind, out double distance, out double offset)) continue;

                    string? value = ExtractValue(other.Line.Text, definition, out double coverage);
                    if (value is null) continue;

                    double score = Score(hit.Similarity, distance, offset, coverage, definition, kind);
                    Consider(ref found, ref winner,
                        new Candidate(anchor.Index, other.Index, kind, value, score, distance, hit.Similarity, hit.Anchor),
                        definition.Occurrence);
                }
            }

            return found && winner.Score >= _options.MinScore ? Materialize(winner, definition) : null;
        }

        // ---- candidate construction ----

        private bool TryInline(LineContext line, in AnchorHit hit, FieldDefinition definition, out Candidate candidate)
        {
            candidate = default;
            string text = line.Line.Text;
            if (hit.ValueStart >= text.Length) return false;

            string remainder = text[hit.ValueStart..].TrimStart(_inlineChars);
            if (remainder.Length == 0) return false;

            string? value = ExtractValue(remainder, definition, out double coverage);
            if (value is null) return false;

            double score = Score(hit.Similarity, 0, 0, coverage, definition, FieldMatchKind.Inline);
            candidate = new Candidate(line.Index, -1, FieldMatchKind.Inline, value, score, 0, hit.Similarity, hit.Anchor);
            return true;
        }

        /// <summary>
        /// Decides whether <paramref name="other"/> is a legal value for an anchor on
        /// <paramref name="anchor"/>, and if so how far off-axis and how far away it is — both in
        /// multiples of the anchor line's height. Lines with no geometry are never classified.
        /// </summary>
        private static bool TryClassify(
            LineContext anchor,
            LineContext other,
            FieldDefinition definition,
            out FieldMatchKind kind,
            out double distance,
            out double offset)
        {
            kind = FieldMatchKind.Inline;
            distance = 0;
            offset = 0;

            var a = anchor.Box;
            var b = other.Box;
            if (a.IsEmpty || b.IsEmpty) return false;

            double h = anchor.Height;
            if (h <= 0) return false;

            double slack = 0.25 * h;
            double verticalOffset = Math.Abs(b.CenterY - a.CenterY) / h;
            double horizontalOffset = HorizontalGap(a, b) / h;
            bool sameRow = verticalOffset <= definition.SearchBand;
            bool aligned = horizontalOffset <= definition.SearchBand;

            if (sameRow && (definition.Direction & FieldDirection.Right) != 0 && b.MinX >= a.MaxX - slack)
            {
                kind = FieldMatchKind.Right;
                distance = Math.Max(0, b.MinX - a.MaxX) / h;
                offset = verticalOffset;
            }
            else if (sameRow && (definition.Direction & FieldDirection.Left) != 0 && b.MaxX <= a.MinX + slack)
            {
                kind = FieldMatchKind.Left;
                distance = Math.Max(0, a.MinX - b.MaxX) / h;
                offset = verticalOffset;
            }
            else if (aligned && (definition.Direction & FieldDirection.Below) != 0 && b.MinY >= a.MaxY - 0.35 * h)
            {
                kind = FieldMatchKind.Below;
                distance = Math.Max(0, b.MinY - a.MaxY) / h;
                offset = horizontalOffset;
            }
            else if (aligned && (definition.Direction & FieldDirection.Above) != 0 && b.MaxY <= a.MinY + 0.35 * h)
            {
                kind = FieldMatchKind.Above;
                distance = Math.Max(0, a.MinY - b.MaxY) / h;
                offset = horizontalOffset;
            }
            else
            {
                return false;
            }

            return distance <= definition.MaxDistance;
        }

        /// <summary>Cleans a candidate's text and applies the value pattern, reporting how much of the
        /// candidate that pattern covered (1.0 when there is no pattern).</summary>
        private string? ExtractValue(string text, FieldDefinition definition, out double coverage)
        {
            coverage = 1.0;
            string trimmed = text.Trim();
            if (trimmed.Length == 0) return null;

            if (definition.ValuePattern is { } pattern)
            {
                var match = pattern.Match(trimmed);
                if (!match.Success) return null;

                var named = match.Groups["value"];
                string raw = named.Success ? named.Value : match.Value;
                coverage = Math.Clamp((double)match.Length / trimmed.Length, 0.0, 1.0);
                string patterned = raw.Trim(_trimChars);
                return patterned.Length == 0 ? null : patterned;
            }

            string value = trimmed.Trim(_trimChars);
            return value.Length == 0 ? null : value;
        }

        private double Score(
            double similarity,
            double distance,
            double offset,
            double coverage,
            FieldDefinition definition,
            FieldMatchKind kind)
        {
            double maxDistance = Math.Max(definition.MaxDistance, 1e-6);
            double band = Math.Max(definition.SearchBand, 1e-6);

            double distanceScore = 1.0 - Math.Clamp(distance / maxDistance, 0.0, 1.0);
            double alignmentScore = 1.0 - Math.Clamp(offset / band, 0.0, 1.0);
            // No pattern is a mild negative: a pattern that covers the whole candidate is real evidence
            // that the line is the value and not, say, the next label along.
            double patternScore = definition.ValuePattern is null
                ? 0.85
                : 0.5 + (0.5 * Math.Clamp(coverage, 0.0, 1.0));

            double wa = Math.Max(0, _options.AnchorWeight);
            double wd = Math.Max(0, _options.DistanceWeight);
            double wl = Math.Max(0, _options.AlignmentWeight);
            double wp = Math.Max(0, _options.PatternWeight);
            double total = wa + wd + wl + wp;
            if (total <= 0) return 0;

            double mean = ((wa * Math.Clamp(similarity, 0, 1))
                + (wd * distanceScore)
                + (wl * alignmentScore)
                + (wp * patternScore)) / total;

            return Math.Clamp(mean * DirectionBias(kind), 0.0, 1.0);
        }

        /// <summary>
        /// All else being equal, a value inline with its label is more certainly the value than one to the
        /// right, which is more certain than one below, and reading leftwards or upwards is a last resort.
        /// </summary>
        private static double DirectionBias(FieldMatchKind kind) => kind switch
        {
            FieldMatchKind.Inline => 1.00,
            FieldMatchKind.Right => 0.98,
            FieldMatchKind.Below => 0.94,
            FieldMatchKind.Left => 0.86,
            _ => 0.82,
        };

        private static void Consider(ref bool found, ref Candidate current, in Candidate candidate, FieldOccurrence occurrence)
        {
            if (!found)
            {
                found = true;
                current = candidate;
                return;
            }

            bool better = occurrence switch
            {
                FieldOccurrence.First => candidate.AnchorLine < current.AnchorLine
                    || (candidate.AnchorLine == current.AnchorLine && candidate.Score > current.Score),
                FieldOccurrence.Last => candidate.AnchorLine > current.AnchorLine
                    || (candidate.AnchorLine == current.AnchorLine && candidate.Score > current.Score),
                _ => candidate.Score > current.Score,
            };

            if (better) current = candidate;
        }

        /// <summary>Turns the winning candidate into the public result, joining continuation lines when
        /// <see cref="FieldDefinition.MaxValueLines"/> allows it.</summary>
        private ExtractedField Materialize(in Candidate candidate, FieldDefinition definition)
        {
            var anchor = _lines[candidate.AnchorLine];
            var valueLines = new List<OcrLine>(1);
            string value = candidate.Value;
            OcrBoundingBox box;

            if (candidate.ValueLine < 0)
            {
                valueLines.Add(anchor.Line);
                box = anchor.Box;
            }
            else
            {
                var first = _lines[candidate.ValueLine];
                valueLines.Add(first.Line);
                box = first.Box;

                if (definition.MaxValueLines > 1
                    && candidate.Kind == FieldMatchKind.Below
                    && definition.ValuePattern is null)
                {
                    var builder = new StringBuilder(value);
                    var previous = first;
                    for (int i = first.Index + 1; i < _lines.Length && valueLines.Count < definition.MaxValueLines; i++)
                    {
                        var next = _lines[i];
                        if (string.IsNullOrWhiteSpace(next.Line.Text) || next.Box.IsEmpty || previous.Box.IsEmpty) break;
                        if (HorizontalGap(previous.Box, next.Box) > definition.SearchBand * anchor.Height) break;
                        double gap = next.Box.MinY - previous.Box.MaxY;
                        if (gap < -0.35 * previous.Height || gap > 1.5 * previous.Height) break;

                        string continuation = next.Line.Text.Trim().Trim(_trimChars);
                        if (continuation.Length == 0) break;

                        builder.Append(' ').Append(continuation);
                        valueLines.Add(next.Line);
                        box = Union(box, next.Box);
                        previous = next;
                    }
                    value = builder.ToString();
                }
            }

            double confidence = 0;
            foreach (var line in valueLines) confidence += line.Confidence;
            if (valueLines.Count > 0) confidence /= valueLines.Count;

            return new ExtractedField
            {
                Name = definition.Name,
                Value = value,
                Confidence = confidence,
                Score = candidate.Score,
                MatchKind = candidate.Kind,
                MatchedAnchor = candidate.Anchor,
                AnchorSimilarity = candidate.Similarity,
                Distance = candidate.Distance,
                AnchorLine = anchor.Line,
                ValueLines = valueLines,
                BoundingBox = box,
            };
        }

        // ---- anchor matching ----

        private static bool TryMatchAnchor(
            LineContext line,
            FieldDefinition definition,
            Needle[] needles,
            double minSimilarity,
            out AnchorHit hit)
        {
            hit = default;
            double best = 0;

            foreach (var needle in needles)
            {
                for (int i = 0; i + needle.TokenCount <= line.Tokens.Length; i++)
                {
                    string window = Join(line.Tokens, i, needle.TokenCount);
                    double similarity = Similarity(needle.Normalized, window, minSimilarity);
                    if (similarity < minSimilarity || similarity <= best) continue;

                    best = similarity;
                    hit = new AnchorHit(similarity, line.Tokens[i + needle.TokenCount - 1].End, needle.Original);
                }
            }

            if (definition.AnchorPatterns is { Count: > 0 } patterns && best < 1.0)
            {
                foreach (var pattern in patterns)
                {
                    if (pattern is null) continue;
                    var match = pattern.Match(line.Line.Text);
                    if (!match.Success) continue;

                    best = 1.0;
                    hit = new AnchorHit(1.0, match.Index + match.Length, match.Value);
                    break;
                }
            }

            return best > 0;
        }

        private static Needle[] BuildNeedles(IReadOnlyList<string>? anchors)
        {
            if (anchors is not { Count: > 0 }) return Array.Empty<Needle>();

            var needles = new List<Needle>(anchors.Count);
            foreach (var anchor in anchors)
            {
                if (string.IsNullOrWhiteSpace(anchor)) continue;
                var tokens = Tokenize(anchor);
                if (tokens.Length == 0) continue;
                needles.Add(new Needle(anchor, Join(tokens, 0, tokens.Length), tokens.Length));
            }
            return needles.ToArray();
        }

        private static LineContext[] BuildContexts(IReadOnlyList<OcrLine> lines)
        {
            // Lines whose box is degenerate borrow the page's median line height so distances stay
            // meaningful instead of collapsing to a division by zero.
            var heights = new List<double>(lines.Count);
            foreach (var line in lines)
            {
                double h = Resolve(line).Height;
                if (h > 0) heights.Add(h);
            }

            double fallback = 1;
            if (heights.Count > 0)
            {
                heights.Sort();
                fallback = heights[heights.Count / 2];
            }

            var contexts = new LineContext[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var box = Resolve(line);
                contexts[i] = new LineContext
                {
                    Line = line,
                    Index = i,
                    Box = box,
                    Height = box.Height > 0 ? box.Height : fallback,
                    Tokens = Tokenize(line.Text),
                };
            }
            return contexts;

            static OcrBoundingBox Resolve(OcrLine line)
                => line.BoundingBox.IsEmpty && line.BoundingPolygon.Count > 0
                    ? OcrBoundingBox.FromPoints(line.BoundingPolygon)
                    : line.BoundingBox;
        }

        // ---- text utilities ----

        /// <summary>Splits text into whitespace-delimited words, dropping punctuation and case but keeping
        /// each word's extent in the original string so an inline value can be sliced off after it.</summary>
        private static Token[] Tokenize(string? text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<Token>();

            var tokens = new List<Token>();
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;

                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;

                string normalized = Normalize(text.AsSpan(start, i - start));
                if (normalized.Length > 0) tokens.Add(new Token(normalized, start, i));
            }
            return tokens.ToArray();
        }

        private static string Normalize(ReadOnlySpan<char> word)
        {
            Span<char> buffer = word.Length <= 128 ? stackalloc char[128] : new char[word.Length];
            int n = 0;
            foreach (char c in word)
            {
                if (char.IsLetterOrDigit(c)) buffer[n++] = char.ToLowerInvariant(c);
            }
            return n == 0 ? string.Empty : new string(buffer[..n]);
        }

        private static string Join(Token[] tokens, int start, int count)
        {
            if (count == 1) return tokens[start].Text;

            var builder = new StringBuilder();
            for (int i = 0; i < count; i++) builder.Append(tokens[start + i].Text);
            return builder.ToString();
        }

        /// <summary>
        /// Normalized edit-distance similarity in 0–1, where 1 is identical. <paramref name="minimum"/> is
        /// only an optimization: the length difference alone is a lower bound on the edit distance, so a
        /// pair that cannot possibly reach the caller's threshold is rejected without the DP pass.
        /// </summary>
        private static double Similarity(string a, string b, double minimum)
        {
            if (a.Length == 0 || b.Length == 0) return a.Length == b.Length ? 1.0 : 0.0;
            if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;

            int max = Math.Max(a.Length, b.Length);
            if (1.0 - ((double)Math.Abs(a.Length - b.Length) / max) < minimum) return 0.0;

            return 1.0 - ((double)EditDistance(a, b) / max);
        }

        /// <summary>Levenshtein distance, two rolling rows (Wagner–Fischer).</summary>
        private static int EditDistance(string a, string b)
        {
            if (b.Length > a.Length) (a, b) = (b, a);

            int n = b.Length;
            Span<int> previous = n < 128 ? stackalloc int[128] : new int[n + 1];
            Span<int> current = n < 128 ? stackalloc int[128] : new int[n + 1];

            for (int j = 0; j <= n; j++) previous[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                char ai = a[i - 1];
                for (int j = 1; j <= n; j++)
                {
                    int cost = ai == b[j - 1] ? 0 : 1;
                    int deletion = previous[j] + 1;
                    int insertion = current[j - 1] + 1;
                    int substitution = previous[j - 1] + cost;
                    current[j] = Math.Min(Math.Min(deletion, insertion), substitution);
                }

                var swap = previous;
                previous = current;
                current = swap;
            }

            return previous[n];
        }

        /// <summary>Horizontal gap between two boxes' x-ranges; 0 whenever they overlap at all.</summary>
        private static double HorizontalGap(in OcrBoundingBox a, in OcrBoundingBox b)
            => Math.Max(0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));

        private static OcrBoundingBox Union(in OcrBoundingBox a, in OcrBoundingBox b)
        {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            return new OcrBoundingBox(
                Math.Min(a.MinX, b.MinX),
                Math.Min(a.MinY, b.MinY),
                Math.Max(a.MaxX, b.MaxX),
                Math.Max(a.MaxY, b.MaxY));
        }
    }
}
