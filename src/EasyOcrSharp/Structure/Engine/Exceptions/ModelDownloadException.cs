namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// A model file could not be obtained — a network/IO failure while downloading, a rejected
/// (non-HTTPS / malformed) source, or a refused file name. Derives <see cref="StructureEngineException"/>
/// so existing catch-all handlers keep working.
/// </summary>
internal class ModelDownloadException : StructureEngineException
{
    /// <summary>
    /// Initializes a new instance with a message.
    /// </summary>
    public ModelDownloadException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance with a message and inner exception.
    /// </summary>
    public ModelDownloadException(string message, Exception innerException) : base(message, innerException) { }
}
