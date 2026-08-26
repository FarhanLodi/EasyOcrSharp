using EasyOcrSharp.Structure.Engine.Models;

namespace EasyOcrSharp.Structure;

/// <summary>
/// Per-call configuration for <see cref="StructureEngine.AnalyzeAsync"/> /
/// <see cref="Engine.Services.IStructureService"/>'s document analysis. Controls document pre-processing
/// (orientation / unwarp), which sub-recognizers run (tables, formulas, seals), the layout model, and the
/// recognition language list passed through to the text recognizer.
/// </summary>
internal sealed record StructureOptions
{
    /// <summary>
    /// Run whole-document orientation correction (0/90/180/270°) before layout detection. Default false.
    /// </summary>
    public bool UseDocOrientation { get; init; }

    /// <summary>
    /// Run document unwarping (UVDoc dewarp) before layout detection. Default false.
    /// </summary>
    public bool UseUnwarp { get; init; }

    /// <summary>
    /// Recognize the structure (HTML) of detected table regions. Default true.
    /// </summary>
    public bool RecognizeTables { get; init; } = true;

    /// <summary>
    /// Recognize the LaTeX of detected formula regions. Default true.
    /// </summary>
    public bool RecognizeFormulas { get; init; } = true;

    /// <summary>
    /// Recognize the text of detected seal regions. Default true.
    /// </summary>
    public bool RecognizeSeals { get; init; } = true;

    /// <summary>
    /// Which layout-detection model to use. Default <see cref="LayoutModel.RtDetrL"/> — the RT-DETR slot is
    /// served by the hosted PP-DocLayoutV3 model (the PicoDet S/M variants are not hosted yet).
    /// </summary>
    public LayoutModel LayoutModel { get; init; } = LayoutModel.RtDetrL;

    /// <summary>
    /// Which table-structure model recovers <see cref="StructureBlockType.Table"/> regions. Default
    /// <see cref="TableRecognitionModel.SlanetPlus"/> (single end-to-end model). Set
    /// <see cref="TableRecognitionModel.SlaNeXt"/> to use the PP-StructureV3 v2 path (a wired/wireless
    /// classifier picks the matching SLANeXt model) — more accurate on clearly bordered/borderless tables;
    /// downloads three extra models on first use. Only consulted when <see cref="RecognizeTables"/> is true.
    /// </summary>
    public TableRecognitionModel TableModel { get; init; } = TableRecognitionModel.SlanetPlus;

    /// <summary>
    /// Recognition languages passed through to the text recognizer for text/caption/seal regions. Takes
    /// strongly-typed <see cref="OcrLanguage"/> values. Defaults to a single-element list of
    /// <see cref="OcrLanguage.ChineseSimplified"/> (code <c>"ch"</c>, which also covers English/Japanese).
    /// Use <see cref="OcrLanguage.Auto"/> for auto-detect.
    /// </summary>
    public IReadOnlyList<OcrLanguage> Languages { get; init; } = new[] { OcrLanguage.ChineseSimplified };

    /// <summary>
    /// Default structure options.
    /// </summary>
    public static StructureOptions Default { get; } = new();
}
