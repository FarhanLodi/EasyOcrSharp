using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Structure.Table;

/// <summary>
/// Classifies a cropped table region as <i>wired</i> (ruled/bordered) or <i>wireless</i> (borderless) so the
/// SLANeXt path can pick the matching structure model. Implemented by <see cref="TableClassifier"/>
/// (PP-LCNet_x1_0_table_cls); owns and disposes its ONNX session.
/// </summary>
internal interface ITableClassifier : IDisposable
{
    /// <summary>
    /// Returns <c>true</c> when the table is classified as <i>wireless</i> (borderless), <c>false</c> for
    /// <i>wired</i> (bordered).
    /// </summary>
    /// <param name="tableCrop">The cropped table region (caller retains ownership).</param>
    bool IsWireless(Image<Rgb24> tableCrop);
}
