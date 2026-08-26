namespace EasyOcrSharp.Structure;

/// <summary>
/// Selects which table-structure model the structure pipeline uses for <see cref="StructureBlockType.Table"/>
/// regions. Both produce HTML <c>&lt;table&gt;</c> markup with the region's OCR text matched into the cells.
/// </summary>
internal enum TableRecognitionModel
{
    /// <summary>
    /// SLANet_plus — a single end-to-end structure model (488×488). The default: lightest and validated,
    /// it recovers the table grid and per-cell boxes in one pass.
    /// </summary>
    SlanetPlus,

    /// <summary>
    /// SLANeXt (PP-StructureV3 "table recognition v2"). A lightweight table-type classifier
    /// (<c>PP-LCNet_x1_0_table_cls</c>) first decides whether the table is <i>wired</i> (ruled/bordered) or
    /// <i>wireless</i> (borderless), then the matching SLANeXt structure model (512×512) recovers the grid.
    /// More accurate on clearly bordered or clearly borderless tables; downloads three extra models on first use.
    /// </summary>
    SlaNeXt,
}
