using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

namespace EasyOcrSharp.Internal;

/// <summary>
/// The decoding half of TrOCR's RoBERTa-style byte-level BPE tokenizer: turns generated token ids back
/// into text. Encoding is not implemented and not needed — the decoder is primed with a single start
/// token id, never with tokenized text.
/// <para>
/// Byte-level BPE never stores raw bytes in its vocabulary (a JSON vocab could not hold control bytes),
/// so GPT-2 established a reversible byte → printable-character map: the 188 already-printable bytes
/// stand for themselves and the remaining 68 are shifted into U+0100…U+0143. A space therefore appears
/// as <c>Ġ</c> (U+0120 = 256 + 32) at the start of every word-initial token, and the UTF-8 bytes of
/// "é" (C3 A9) appear as <c>Ã©</c>. Decoding is the exact inverse: map every character of every token
/// back to its byte, concatenate, and interpret the whole buffer as UTF-8. That single step restores
/// word boundaries and multi-byte characters at once — no special <c>Ġ</c> case, and no risk of tearing
/// a multi-byte character that BPE split across two tokens.
/// </para>
/// </summary>
internal sealed class TrOcrTokenizer
{
    /// <summary>
    /// Tokens that carry no text and must never reach the output. TrOCR's decoder emits
    /// <c>&lt;s&gt;</c> before the first real token and <c>&lt;/s&gt;</c> to stop, so at minimum these
    /// two have to be filtered out.
    /// </summary>
    private static readonly FrozenSet<string> SpecialTokens =
        new[] { "<s>", "</s>", "<pad>", "<unk>", "<mask>" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>byte → the printable character that stands for it in a byte-level BPE vocabulary.</summary>
    private static readonly char[] ByteToChar = BuildByteToChar();

    /// <summary>The inverse of <see cref="ByteToChar"/>, used for decoding.</summary>
    private static readonly FrozenDictionary<char, byte> CharToByte = BuildCharToByte();

    private readonly string?[] _tokens;
    private readonly FrozenSet<int> _specialIds;

    private TrOcrTokenizer(string?[] tokens, FrozenSet<int> specialIds)
    {
        _tokens = tokens;
        _specialIds = specialIds;
    }

    /// <summary>Number of id slots in the vocabulary (the highest known id plus one).</summary>
    public int Count => _tokens.Length;

    /// <summary>
    /// Ids of the tokens that carry no text (<c>&lt;s&gt;</c>, <c>&lt;/s&gt;</c>, <c>&lt;pad&gt;</c>,
    /// <c>&lt;unk&gt;</c>, <c>&lt;mask&gt;</c>). Skipped when decoding, and excluded from the confidence
    /// average so a fixed opening <c>&lt;s&gt;</c> cannot inflate — or deflate — a reading's score.
    /// </summary>
    public IReadOnlySet<int> SpecialTokenIds => _specialIds;

    /// <summary>Returns the raw (byte-level encoded) vocabulary entry for an id, or null if unknown.</summary>
    public string? TokenAt(int id) => id >= 0 && id < _tokens.Length ? _tokens[id] : null;

    /// <summary>
    /// Loads a tokenizer sidecar from disk. See <see cref="FromJson"/> for the accepted shapes.
    /// </summary>
    /// <param name="path">Path to the vocabulary JSON file.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="EasyOcrSharpException">The file is not a vocabulary this decoder understands.</exception>
    public static TrOcrTokenizer FromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Handwriting tokenizer file '{path}' was not found.", path);

        try
        {
            return FromJson(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new EasyOcrSharpException($"Handwriting tokenizer file '{path}' is not valid JSON.", ex);
        }
    }

    /// <summary>
    /// Parses a vocabulary in any of the three shapes the HuggingFace tooling produces:
    /// a <c>vocab.json</c> object mapping token → id, a full <c>tokenizer.json</c> (whose
    /// <c>model.vocab</c> object is used), or a JSON array listing the tokens in id order.
    /// </summary>
    /// <param name="json">The sidecar's contents.</param>
    /// <exception cref="EasyOcrSharpException">No vocabulary could be found in the document.</exception>
    public static TrOcrTokenizer FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array) return FromOrderedTokens(root);
        if (root.ValueKind != JsonValueKind.Object)
            throw new EasyOcrSharpException("Handwriting tokenizer vocabulary must be a JSON object or array.");

        // A full tokenizer.json nests the vocabulary under "model"; some dumps expose it as "vocab".
        if (root.TryGetProperty("model", out var model)
            && model.ValueKind == JsonValueKind.Object
            && model.TryGetProperty("vocab", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            return FromTokenToIdMap(nested);
        }
        if (root.TryGetProperty("vocab", out var vocab) && vocab.ValueKind == JsonValueKind.Object)
        {
            return FromTokenToIdMap(vocab);
        }
        return FromTokenToIdMap(root);
    }

    private static TrOcrTokenizer FromTokenToIdMap(JsonElement map)
    {
        var byId = new Dictionary<int, string>();
        int maxId = -1;
        foreach (var property in map.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int id) || id < 0)
                continue;
            byId[id] = property.Name;
            if (id > maxId) maxId = id;
        }
        if (maxId < 0)
            throw new EasyOcrSharpException("Handwriting tokenizer vocabulary contains no token → id entries.");

        var tokens = new string?[maxId + 1];
        foreach (var (id, token) in byId) tokens[id] = token;
        return new TrOcrTokenizer(tokens, BuildSpecialIds(tokens));
    }

    private static TrOcrTokenizer FromOrderedTokens(JsonElement array)
    {
        var tokens = new List<string?>();
        foreach (var element in array.EnumerateArray())
        {
            tokens.Add(element.ValueKind == JsonValueKind.String ? element.GetString() : null);
        }
        if (tokens.Count == 0)
            throw new EasyOcrSharpException("Handwriting tokenizer vocabulary is empty.");

        var ordered = tokens.ToArray();
        return new TrOcrTokenizer(ordered, BuildSpecialIds(ordered));
    }

    private static FrozenSet<int> BuildSpecialIds(string?[] tokens)
    {
        var ids = new HashSet<int>();
        for (int id = 0; id < tokens.Length; id++)
        {
            if (tokens[id] is { } token && SpecialTokens.Contains(token)) ids.Add(id);
        }
        return ids.ToFrozenSet();
    }

    /// <summary>
    /// Decodes generated token ids into text. Unknown ids are skipped rather than throwing, so a decoder
    /// whose vocabulary is one entry larger than the sidecar degrades to a missing character instead of
    /// failing the whole page.
    /// </summary>
    /// <param name="ids">The generated token ids, in order.</param>
    /// <param name="skipSpecialTokens">
    /// When true (the default) tokens in <see cref="SpecialTokenIds"/> contribute nothing. Pass false to
    /// see the raw generation, which is useful when diagnosing an unexpected reading.
    /// </param>
    public string Decode(IEnumerable<int> ids, bool skipSpecialTokens = true)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var bytes = new List<byte>(64);
        foreach (var id in ids)
        {
            if (skipSpecialTokens && _specialIds.Contains(id)) continue;
            if (TokenAt(id) is not { } token) continue;

            foreach (var ch in token)
            {
                if (CharToByte.TryGetValue(ch, out var b))
                {
                    bytes.Add(b);
                }
                else
                {
                    // Not a byte-level character (a vocabulary that stores literal text, or a stray
                    // symbol): emit the character's own UTF-8 bytes so it still round-trips.
                    bytes.AddRange(Encoding.UTF8.GetBytes(ch.ToString()));
                }
            }
        }

        return Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes));
    }

    /// <summary>
    /// Builds GPT-2's <c>bytes_to_unicode</c> table: bytes <c>!</c>–<c>~</c>, <c>¡</c>–<c>¬</c> and
    /// <c>®</c>–<c>ÿ</c> map to themselves; the remaining 68 bytes take the code points U+0100 upwards,
    /// in ascending byte order. Exposed to the recognizer only through <see cref="CharToByte"/>.
    /// </summary>
    private static char[] BuildByteToChar()
    {
        var map = new char[256];
        var direct = new bool[256];
        for (int b = '!'; b <= '~'; b++) direct[b] = true;
        for (int b = 0xA1; b <= 0xAC; b++) direct[b] = true;
        for (int b = 0xAE; b <= 0xFF; b++) direct[b] = true;

        int shifted = 0;
        for (int b = 0; b < 256; b++)
        {
            map[b] = direct[b] ? (char)b : (char)(256 + shifted);
            if (!direct[b]) shifted++;
        }
        return map;
    }

    private static FrozenDictionary<char, byte> BuildCharToByte()
    {
        var inverse = new Dictionary<char, byte>(256);
        for (int b = 0; b < 256; b++) inverse[ByteToChar[b]] = (byte)b;
        return inverse.ToFrozenDictionary();
    }
}
