namespace EasyOcrSharp;

/// <summary>
/// A model file could not be obtained — a network/IO failure while downloading, a rejected
/// (non-HTTPS / malformed) source, or a refused file name. Derives <see cref="EasyOcrSharpException"/>
/// so existing catch-all handlers keep working.
/// </summary>
public class ModelDownloadException : EasyOcrSharpException
{
    /// <summary>Initializes a new instance with a message.</summary>
    public ModelDownloadException(string message) : base(message) { }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    public ModelDownloadException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// A downloaded model failed integrity verification — its SHA256 did not match the registry value, or it
/// has no known checksum and unverified models were not explicitly allowed.
/// </summary>
public sealed class ModelChecksumException : ModelDownloadException
{
    /// <summary>Initializes a new instance with a message.</summary>
    public ModelChecksumException(string message) : base(message) { }
}

/// <summary>
/// A required model is not present in the cache and strict offline mode is enabled, so it cannot be
/// downloaded. Pre-seed the cache or disable <c>ModelDownloadOptions.Offline</c>.
/// </summary>
public sealed class OfflineModelMissingException : EasyOcrSharpException
{
    /// <summary>Initializes a new instance with a message.</summary>
    public OfflineModelMissingException(string message) : base(message) { }
}

/// <summary>
/// A PDF could not be opened or rendered — corrupt, not a PDF, password-protected/encrypted, or it
/// exceeded a configured page/size guard.
/// </summary>
public sealed class PdfProcessingException : EasyOcrSharpException
{
    /// <summary>Initializes a new instance with a message.</summary>
    public PdfProcessingException(string message) : base(message) { }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    public PdfProcessingException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// An input image's pixel count exceeds <c>EasyOcrServiceOptions.MaxImagePixels</c>, the
/// decompression-bomb / pixel-flood guard. Raise the limit or downscale the image.
/// </summary>
public sealed class ImageTooLargeException : EasyOcrSharpException
{
    /// <summary>Initializes a new instance with a message.</summary>
    public ImageTooLargeException(string message) : base(message) { }
}
