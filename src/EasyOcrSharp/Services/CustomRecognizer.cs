namespace EasyOcrSharp.Services;

/// <summary>
/// Registers a user-supplied CRNN recognizer (a locally exported ONNX model plus its character set)
/// for one or more language codes — EasyOCR's custom <c>recog_network</c> / fine-tuned models. Add
/// instances to <see cref="EasyOcrServiceOptions.CustomRecognizers"/>. When a requested language
/// matches a custom recognizer it is used instead of (and takes precedence over) the built-in pack,
/// and its model is loaded straight from disk — never downloaded.
/// </summary>
public sealed record CustomRecognizer
{
    /// <summary>Unique name for this recognizer (used for caching and diagnostics).</summary>
    public required string Name { get; init; }

    /// <summary>Absolute path to the exported recognizer ONNX file on disk.</summary>
    public required string ModelPath { get; init; }

    /// <summary>
    /// The exact ordered character set the model emits (the CTC label order, excluding the blank at
    /// index 0). Provide this <i>or</i> <see cref="VocabPath"/>. Takes precedence when both are set.
    /// </summary>
    public string? Characters { get; init; }

    /// <summary>
    /// Path to a vocabulary sidecar holding the character set — either a JSON-encoded string (as the
    /// built-in packs use) or a plain UTF-8 text file. Ignored when <see cref="Characters"/> is set.
    /// </summary>
    public string? VocabPath { get; init; }

    /// <summary>Language codes this recognizer should handle (e.g. <c>["en"]</c> or a custom tag).</summary>
    public required IReadOnlyList<string> Languages { get; init; }
}
