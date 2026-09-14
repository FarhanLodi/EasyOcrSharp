using EasyOcrSharp.Structure.Engine.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Table;

/// <summary>
/// The PP-StructureV3 "table recognition v2" path: an <see cref="ITableClassifier"/> decides whether each
/// table crop is wired (ruled/bordered) or wireless (borderless), then delegates to the matching structure
/// recognizer — SLANeXt_wired for wired tables and SLANet_plus for wireless ones, the model pairing
/// PP-StructureV3.yaml ships. SLANeXt is architecturally identical to SLANet (the same <c>[.,.,8]</c>
/// location + <c>[.,.,50]</c> structure heads), so both legs are ordinary
/// <see cref="SlanetTableRecognizer"/> instances (512×512 content-normalized for the wired leg, 488×488
/// canvas-normalized for the wireless one). Owns and disposes the classifier and both recognizers.
/// </summary>
internal sealed class SlaNeXtTableRouter : ITableRecognizer
{
    private readonly ITableClassifier _classifier;
    private readonly ITableRecognizer _wired;
    private readonly ITableRecognizer _wireless;

    /// <summary>
    /// Creates the router over a table-type classifier and the two SLANeXt structure recognizers.
    /// </summary>
    /// <param name="classifier">Wired/wireless classifier (this instance takes ownership).</param>
    /// <param name="wired">SLANeXt recognizer for wired/bordered tables (owned).</param>
    /// <param name="wireless">SLANeXt recognizer for wireless/borderless tables (owned).</param>
    public SlaNeXtTableRouter(ITableClassifier classifier, ITableRecognizer wired, ITableRecognizer wireless)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _wired = wired ?? throw new ArgumentNullException(nameof(wired));
        _wireless = wireless ?? throw new ArgumentNullException(nameof(wireless));
    }

    /// <inheritdoc />
    public TableResult Recognize(Image<Rgb24> tableCrop, IReadOnlyList<OcrLine> ocrLines)
    {
        ArgumentNullException.ThrowIfNull(tableCrop);
        var recognizer = _classifier.IsWireless(tableCrop) ? _wireless : _wired;
        return recognizer.Recognize(tableCrop, ocrLines);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _classifier.Dispose();
        _wired.Dispose();
        _wireless.Dispose();
    }
}
