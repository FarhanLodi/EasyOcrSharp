namespace EasyOcrSharp.Structure.Engine.Models;

/// <summary>
/// Selects which PP-OCRv5 network family serves text detection and recognition.
/// </summary>
internal enum OcrModelVariant
{
    /// <summary>
    /// PP-OCRv5 <b>mobile</b> detection/recognition — the lightweight default (fast, small download).
    /// </summary>
    Mobile = 0,

    /// <summary>
    /// PP-OCRv5 <b>server</b> detection/recognition (<c>PP-OCRv5_server_det</c> /
    /// <c>PP-OCRv5_server_rec</c>) — roughly 3–5× larger than mobile, higher accuracy. The server
    /// recognizer covers Chinese / English / Japanese only (<c>ppocrv5_dict.txt</c>); other language packs
    /// stay on their mobile recognizers regardless of this setting.
    /// </summary>
    Server = 1,
}
