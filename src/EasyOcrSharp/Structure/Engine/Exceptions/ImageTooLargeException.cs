namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// An input image's pixel count exceeds <c>StructureServiceOptions.MaxImagePixels</c>, the
/// decompression-bomb / pixel-flood guard. Raise the limit or downscale the image.
/// </summary>
internal sealed class ImageTooLargeException : StructureEngineException
{
    /// <summary>
    /// Initializes a new instance with a message.
    /// </summary>
    public ImageTooLargeException(string message) : base(message) { }
}
