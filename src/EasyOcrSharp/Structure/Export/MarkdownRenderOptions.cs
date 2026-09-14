namespace EasyOcrSharp.Structure;

/// <summary>
/// Options controlling how <see cref="StructureResult.ToMarkdown(MarkdownRenderOptions)"/> and
/// <see cref="StructureMarkdownExtensions.ConcatenateMarkdownPages(IEnumerable{StructureResult}, MarkdownRenderOptions)"/>
/// render the analyzed document. The defaults reproduce Python PP-StructureV3's Markdown output
/// (<c>MarkdownConverter.convert</c> + <c>result_v2.py</c>): page furniture is omitted, tables are emitted
/// as <c>&lt;table border="1"&gt;</c>, and multi-page documents are joined by paragraph-continuation flags
/// rather than an explicit separator.
/// </summary>
public sealed record MarkdownRenderOptions
{
    /// <summary>
    /// The shared default option set (Python-parity rendering).
    /// </summary>
    public static MarkdownRenderOptions Default { get; } = new();

    /// <summary>
    /// The block types omitted from Markdown by default, mirroring PP-StructureV3's
    /// <c>markdown_ignore_labels</c> (<c>number</c>, <c>footnote</c>, <c>header</c>, <c>header_image</c>,
    /// <c>footer</c>, <c>footer_image</c>, <c>aside_text</c>). The label map folds
    /// <c>header_image</c>/<c>footer_image</c> into <see cref="StructureBlockType.Header"/> /
    /// <see cref="StructureBlockType.Footer"/>, so the five members below cover all seven labels.
    /// </summary>
    public static IReadOnlyCollection<StructureBlockType> DefaultIgnoredBlockTypes { get; } = new[]
    {
        StructureBlockType.Header,
        StructureBlockType.Footer,
        StructureBlockType.PageNumber,
        StructureBlockType.Footnote,
        StructureBlockType.Aside,
    };

    /// <summary>
    /// Block types excluded from the rendered Markdown (page furniture by default — headers, footers, page
    /// numbers, footnotes and margin notes). Supply an empty collection to render every block, or a custom
    /// set to tune what is kept. Ignored blocks still participate in the geometric paragraph-continuation
    /// analysis (as in Python), they just emit no text.
    /// </summary>
    public IReadOnlyCollection<StructureBlockType> IgnoredBlockTypes { get; init; } = DefaultIgnoredBlockTypes;

    /// <summary>
    /// When <c>true</c> (the default, Python's <c>pretty=True</c>), recovered tables are emitted as
    /// <c>&lt;table border="1"&gt;</c> so they render with visible grid lines in Markdown viewers; when
    /// <c>false</c>, the bare recovered <c>&lt;table&gt;</c> fragment is emitted (Python's
    /// <c>simplify_table</c> path — the recognizer's <c>&lt;html&gt;&lt;body&gt;</c> wrapper is always
    /// stripped either way).
    /// </summary>
    public bool PrettyTables { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, figure/chart/seal blocks embed their image crop as a <c>data:</c> URI instead of an
    /// empty placeholder — provided the block carries image bytes. The current pipeline does not retain
    /// per-block crops on <see cref="StructureBlock"/>, so this flag presently keeps the
    /// <c>![Figure]()</c>-style placeholder either way; it exists so the rendered form is stable once block
    /// images become available. Default <c>false</c>.
    /// </summary>
    public bool EmbedImages { get; init; }

    /// <summary>
    /// Explicit separator inserted between concatenated pages. <c>null</c> (the default) selects
    /// Python-parity joining: pages are joined with a blank line, except when the previous page ends
    /// mid-paragraph and the next begins mid-paragraph, in which case they are joined with a single space
    /// (or nothing when either boundary character is CJK). Set to
    /// <see cref="StructureMarkdownExtensions.PageSeparator"/> to restore the pre-2.1 horizontal-rule
    /// (<c>---</c>) page breaks, or to any custom string.
    /// </summary>
    public string? PageSeparator { get; init; }

    /// <summary>
    /// Whether <paramref name="type"/> is excluded from the rendered Markdown.
    /// </summary>
    internal bool IsIgnored(StructureBlockType type)
        => IgnoredBlockTypes is { } ignored && ignored.Contains(type);
}
