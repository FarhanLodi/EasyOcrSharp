namespace EasyOcrSharp.Internal;

/// <summary>
/// Catalogue of the two small neural models behind the model-based document preprocessing steps
/// (<see cref="Models.PreprocessingOptions.DocumentOrientation"/> and
/// <see cref="Models.PreprocessingOptions.DocumentUnwarp"/>). They originate from PaddleOCR/PaddleX
/// and are hosted on the PaddleOcrNet model repo; downloads are SHA256-verified fail-closed by
/// <see cref="ModelDownloadManager"/> like every other model, and the
/// <c>EASYOCRSHARP_MODEL_BASE_URL</c> mirror override applies to them too.
/// </summary>
internal static class DocPreprocessModelRegistry
{
    /// <summary>Base URL where the document-preprocessing ONNX models are hosted.</summary>
    public const string DefaultBaseUrl =
        "https://huggingface.co/PaddleOcrNet/PaddleOcrNet-models/resolve/main";

    /// <summary>
    /// PP-LCNet_x1_0 document-orientation classifier: input <c>[1,3,224,224]</c>, output <c>[1,4]</c>
    /// logits over {0°, 90°, 180°, 270°} whole-page rotation.
    /// </summary>
    public static readonly ModelAsset DocOrientationClassifier = new(
        "PP-LCNet_x1_0_doc_ori.onnx",
        $"{DefaultBaseUrl}/PP-LCNet_x1_0_doc_ori.onnx",
        "D85B3185075AFCA1A83157F73EAC2E52B598D72E9D47DD19CC4A2F3605E23E3F");

    /// <summary>
    /// UVDoc document-unwarp (dewarp) network: input <c>[1,3,H,W]</c> (fed at 488×712), output a
    /// rectified RGB image at the same size.
    /// </summary>
    public static readonly ModelAsset DocUnwarp = new(
        "UVDoc.onnx",
        $"{DefaultBaseUrl}/UVDoc.onnx",
        "7E54E917AD9CA8F6CFFE606C7C311AAD3B6EEE457D4D9776F99F175D0CA86835");
}
