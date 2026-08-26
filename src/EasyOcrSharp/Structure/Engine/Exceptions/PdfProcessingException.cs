namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// A PDF could not be opened or rendered — corrupt, not a PDF, password-protected/encrypted, or it
/// exceeded a configured page/size guard.
/// </summary>
internal sealed class PdfProcessingException : StructureEngineException
{
    /// <summary>
    /// Initializes a new instance with a message.
    /// </summary>
    public PdfProcessingException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance with a message and inner exception.
    /// </summary>
    public PdfProcessingException(string message, Exception innerException) : base(message, innerException) { }
}
