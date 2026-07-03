namespace EasyOcrSharp.Models;

/// <summary>
/// Which table-structure model recovers table regions during document analysis.
/// </summary>
public enum DocumentTableModel
{
    /// <summary>SLANet-plus — one end-to-end model (the default; smallest download).</summary>
    SlanetPlus = 0,

    /// <summary>
    /// SLANeXt (PP-StructureV3 v2 path) — a wired/wireless table classifier picks the matching
    /// SLANeXt model. More accurate on clearly bordered/borderless tables; downloads three extra
    /// models on first use.
    /// </summary>
    SlaNeXt = 1,
}

/// <summary>
/// Per-call configuration for
/// <see cref="Services.EasyOcrService.AnalyzeDocumentAsync(string, DocumentAnalysisOptions?, CancellationToken)"/> —
/// document-structure analysis (layout regions, tables, formulas, seals, reading order), powered by
/// PaddleOCR's PP-StructureV3 models via the PaddleOcrNet engine. All models download on demand and
/// are SHA256-verified.
/// </summary>
public sealed record DocumentAnalysisOptions
{
    /// <summary>
    /// Correct whole-page orientation (0/90/180/270°) with the document-orientation classifier before
    /// layout detection. Default false.
    /// </summary>
    public bool DocumentOrientation { get; init; }

    /// <summary>
    /// Dewarp a curved/folded page (UVDoc) before layout detection. Default false.
    /// </summary>
    public bool DocumentUnwarp { get; init; }

    /// <summary>Recognize the structure (HTML) of detected table regions. Default true.</summary>
    public bool RecognizeTables { get; init; } = true;

    /// <summary>Recognize the LaTeX of detected formula regions. Default true.</summary>
    public bool RecognizeFormulas { get; init; } = true;

    /// <summary>Recognize the text of detected seal (stamp) regions. Default true.</summary>
    public bool RecognizeSeals { get; init; } = true;

    /// <summary>
    /// Which table-structure model recovers table regions. Default
    /// <see cref="DocumentTableModel.SlanetPlus"/>. Only consulted when
    /// <see cref="RecognizeTables"/> is true.
    /// </summary>
    public DocumentTableModel TableModel { get; init; } = DocumentTableModel.SlanetPlus;

    /// <summary>
    /// Recognition language codes for text inside the document (PaddleOCR codes, e.g. <c>"en"</c>,
    /// <c>"ch"</c>, <c>"fr"</c>, <c>"ru"</c>, <c>"ar"</c>, or <c>"auto"</c> to auto-detect). Null or
    /// empty uses the analyzer's default pack, which covers Chinese + English + Japanese. Codes that
    /// don't map to a known pack are skipped with a warning.
    /// </summary>
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>The default analysis options (tables, formulas and seals on; no page correction).</summary>
    public static DocumentAnalysisOptions Default { get; } = new();
}
