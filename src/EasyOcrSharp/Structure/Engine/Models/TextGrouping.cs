namespace EasyOcrSharp.Structure.Engine.Models;

/// <summary>
/// How recognized text regions are grouped in the result.
/// </summary>
internal enum TextGrouping
{
    /// <summary>
    /// One result per raw detected box (roughly per line/word). No further merging.
    /// </summary>
    Word,

    /// <summary>
    /// Adjacent boxes on the same line are merged into one result (the default).
    /// </summary>
    Line,

    /// <summary>
    /// Lines are further merged into paragraph blocks by vertical proximity.
    /// </summary>
    Paragraph,
}
