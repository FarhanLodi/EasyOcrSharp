namespace EasyOcrSharp.Structure;

/// <summary>
/// Selects which table-structure model the structure pipeline uses for <see cref="StructureBlockType.Table"/>
/// regions. Both produce HTML <c>&lt;table&gt;</c> markup with the region's OCR text matched into the cells.
/// </summary>
internal enum TableRecognitionModel
{
    /// <summary>
    /// SLANet_plus — a single end-to-end structure model (488×488). The default, and the recommended
    /// choice: lightest and validated, it recovers the table grid and per-cell boxes in one pass. On the
    /// bundled fixtures it places every value in the right cell.
    /// </summary>
    SlanetPlus,

    /// <summary>
    /// SLANeXt (PP-StructureV3 "table recognition v2"). A lightweight table-type classifier
    /// (<c>PP-LCNet_x1_0_table_cls</c>) first decides whether the table is <i>wired</i> (ruled/bordered) or
    /// <i>wireless</i> (borderless), then the matching SLANeXt structure model (512×512) recovers the grid.
    /// Downloads three extra models on first use.
    /// <para>
    /// <b>Still less accurate than <see cref="SlanetPlus"/>; prefer the default.</b> SLANeXt's location head
    /// is content-normalized rather than canvas-normalized, which 2.0.4 corrects — that halved the damage
    /// (on the bundled 16×6 fixture, empty cells fell from 54 to 33; on the 4-row fixture, from 8 to 4). A
    /// residual error remains: the fit against SLANet_plus's verified boxes explains only ~89% of the
    /// variance, so scale was not the whole story, and cell text is still misplaced on both fixtures.
    /// <see cref="SlanetPlus"/> places every value correctly on the same inputs and stays the default.
    /// </para>
    /// </summary>
    SlaNeXt,
}
