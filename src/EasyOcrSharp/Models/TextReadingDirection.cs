namespace EasyOcrSharp.Models;

/// <summary>
/// The direction in which detected text is assembled into a reading order — which column comes first, and
/// which fragment comes first within a row.
/// </summary>
/// <remarks>
/// <para>
/// This governs <b>geometric</b> reading order only: the sequence in which detected boxes are concatenated
/// into <see cref="OcrResult.FullText"/> and into paragraph groups. It is not the Unicode Bidirectional
/// Algorithm. Each recognized line is already a logical-order string — the recognizer emits the characters
/// in the order they are read, not the order they are painted — so applying bidi is the renderer's job, and
/// text this library returns is correct input for one.
/// </para>
/// <para>
/// What it fixes: an Arabic or Hebrew page is read right-to-left, so the physically <i>right-most</i> column
/// is the first one, and within a row band the right-most fragment comes first. Ordering such a page
/// left-to-right silently produces a correct set of lines in the wrong sequence — a two-column Arabic
/// newspaper page starts on the wrong column, and a label at x≈600 with its value at x≈100 concatenates as
/// "value label".
/// </para>
/// </remarks>
public enum TextReadingDirection
{
    /// <summary>
    /// Derive the direction from the requested languages: right-to-left when every requested language is
    /// written in a right-to-left script (<c>ar</c>, <c>fa</c>, <c>ur</c>, <c>ug</c>, <c>he</c>, <c>yi</c>,
    /// <c>ps</c>, <c>sd</c>, <c>dv</c>), left-to-right otherwise. This is the default.
    /// <para>
    /// A mixed request such as <c>["ar", "en"]</c> resolves to left-to-right: a document deliberately
    /// combining the two is most often a Latin-majority page with Arabic passages, and guessing wrong on a
    /// bilingual page is worse than leaving the established order alone. Set the direction explicitly for a
    /// mixed-language page whose overall flow is right-to-left.
    /// </para>
    /// </summary>
    Auto = 0,

    /// <summary>Force left-to-right assembly, whatever the languages.</summary>
    LeftToRight = 1,

    /// <summary>Force right-to-left assembly, whatever the languages.</summary>
    RightToLeft = 2,
}
