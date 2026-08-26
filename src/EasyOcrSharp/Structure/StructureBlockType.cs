namespace EasyOcrSharp.Structure;

/// <summary>
/// The semantic category of a layout region / structure block, as emitted by a PaddleOCR document-layout
/// detector (PP-DocLayout / RT-DETR). The detector's raw integer class IDs are mapped onto this stable,
/// model-agnostic enum so downstream consumers (reading-order, Markdown/JSON export) never depend on a
/// specific model's label indices. Region types a particular model does not emit simply never appear;
/// any class ID with no mapping resolves to <see cref="Unknown"/>.
/// </summary>
public enum StructureBlockType
{
    /// <summary>
    /// Generic body text / plain text region.
    /// </summary>
    Text,

    /// <summary>
    /// A section or block title / heading.
    /// </summary>
    Title,

    /// <summary>
    /// The document's top-level title.
    /// </summary>
    DocTitle,

    /// <summary>
    /// A paragraph of text (when the model distinguishes it from generic <see cref="Text"/>).
    /// </summary>
    Paragraph,

    /// <summary>
    /// A bulleted or numbered list.
    /// </summary>
    List,

    /// <summary>
    /// A table region (its cells/structure are recovered by the table recognizer).
    /// </summary>
    Table,

    /// <summary>
    /// A caption attached to a table.
    /// </summary>
    TableCaption,

    /// <summary>
    /// A figure / image / picture region.
    /// </summary>
    Figure,

    /// <summary>
    /// A caption attached to a figure.
    /// </summary>
    FigureCaption,

    /// <summary>
    /// A mathematical formula / equation region (its LaTeX is recovered by the formula recognizer).
    /// </summary>
    Formula,

    /// <summary>
    /// A formula's equation number (e.g. "(3)").
    /// </summary>
    FormulaNumber,

    /// <summary>
    /// A seal / stamp region (its text is recovered by the seal recognizer).
    /// </summary>
    Seal,

    /// <summary>
    /// A chart / plot region.
    /// </summary>
    Chart,

    /// <summary>
    /// A page header.
    /// </summary>
    Header,

    /// <summary>
    /// A page footer.
    /// </summary>
    Footer,

    /// <summary>
    /// A bibliographic reference / citation entry.
    /// </summary>
    Reference,

    /// <summary>
    /// A footnote.
    /// </summary>
    Footnote,

    /// <summary>
    /// A page number.
    /// </summary>
    PageNumber,

    /// <summary>
    /// An abstract block.
    /// </summary>
    Abstract,

    /// <summary>
    /// An algorithm / pseudocode block.
    /// </summary>
    Algorithm,

    /// <summary>
    /// An aside / sidebar / marginal note.
    /// </summary>
    Aside,

    /// <summary>
    /// An unmapped or unrecognized region type (the fallback for any unknown raw class ID).
    /// </summary>
    Unknown,
}
