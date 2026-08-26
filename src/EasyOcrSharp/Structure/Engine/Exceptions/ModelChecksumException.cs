namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// A downloaded model failed integrity verification — its SHA256 did not match the registry value, or it
/// has no known checksum and unverified models were not explicitly allowed.
/// </summary>
internal sealed class ModelChecksumException : ModelDownloadException
{
    /// <summary>
    /// Initializes a new instance with a message.
    /// </summary>
    public ModelChecksumException(string message) : base(message) { }
}
