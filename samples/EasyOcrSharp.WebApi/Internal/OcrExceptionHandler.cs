using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace EasyOcrSharp.WebApi.Internal;

/// <summary>
/// Translates the library's typed exceptions into RFC&#160;9457 problem details with an honest status
/// code, so a client can tell "your file is too big" (413, don't retry) from "the model cache is cold"
/// (503, retry later) without parsing prose.
/// </summary>
/// <remarks>
/// Detail text is taken from the exception message, which for these types is operator-written and safe
/// to expose. Anything unrecognized is deliberately <b>not</b> handled here: it falls through to the
/// framework's own handler, which returns a bare 500 and leaks nothing.
/// </remarks>
internal sealed class OcrExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<OcrExceptionHandler> _logger;

    /// <summary>Creates the handler.</summary>
    public OcrExceptionHandler(IProblemDetailsService problemDetails, ILogger<OcrExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // The client hung up. There is nobody to send a response to, and it is not an error.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("Request {Path} was cancelled by the client.", httpContext.Request.Path);
            return true;
        }

        var (status, title) = Classify(exception);
        if (status is null)
        {
            return false;
        }

        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = "30";
        }

        _logger.LogWarning(exception, "{Status} on {Path}: {Message}",
            status, httpContext.Request.Path, exception.Message);

        httpContext.Response.StatusCode = status.Value;
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = exception.Message,
                Instance = httpContext.Request.Path,
            },
        }).ConfigureAwait(false);
    }

    /// <summary>Maps an exception to the status code and title that describe it truthfully.</summary>
    private static (int? Status, string Title) Classify(Exception exception) => exception switch
    {
        // The upload was larger than a configured guard allows.
        ImageTooLargeException => (StatusCodes.Status413PayloadTooLarge, "Image too large"),
        BadHttpRequestException bad when bad.StatusCode == StatusCodes.Status413PayloadTooLarge
            => (StatusCodes.Status413PayloadTooLarge, "Request body too large"),

        // Well-formed request, unusable document. EasyImageSharp raises these from ImageFormatException,
        // which does not derive from ArgumentException — but they stay above the ArgumentException arm so
        // the mapping survives an imaging library that classifies them differently.
        EasyImageSharp.UnknownImageFormatException
            => (StatusCodes.Status415UnsupportedMediaType, "Unsupported image format"),
        EasyImageSharp.InvalidImageContentException
            => (StatusCodes.Status422UnprocessableEntity, "Image could not be decoded"),
        PdfProcessingException => (StatusCodes.Status422UnprocessableEntity, "PDF could not be processed"),

        // The caller sent something this endpoint cannot work with.
        BadHttpRequestException bad => (bad.StatusCode, "Invalid request"),
        ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),

        // The server cannot serve this right now, but the request was fine.
        OfflineModelMissingException => (StatusCodes.Status503ServiceUnavailable, "Model not available offline"),
        ModelChecksumException => (StatusCodes.Status502BadGateway, "Model failed integrity verification"),
        ModelDownloadException => (StatusCodes.Status502BadGateway, "Model download failed"),

        _ => (null, string.Empty),
    };
}
