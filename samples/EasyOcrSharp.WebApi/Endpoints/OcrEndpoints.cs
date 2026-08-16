using EasyOcrSharp.Services;
using EasyOcrSharp.WebApi.Internal;

namespace EasyOcrSharp.WebApi.Endpoints;

/// <summary>
/// <c>POST /ocr</c> — recognize text in an uploaded image and return it as JSON, hOCR, ALTO, TSV or
/// plain text.
/// </summary>
internal static class OcrEndpoints
{
    /// <summary>Maps the image OCR endpoint onto <paramref name="app"/>.</summary>
    public static IEndpointRouteBuilder MapOcrEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/ocr", HandleAsync)
            // Uploads are anonymous and cookie-less, so there is no ambient authority for a forged
            // cross-site POST to abuse. Put authentication in front of this and the calculus changes:
            // use a bearer token (immune) or re-enable antiforgery.
            .DisableAntiforgery()
            .WithName("Ocr")
            .WithSummary("OCR an uploaded image.")
            .WithDescription(
                "multipart/form-data with a 'file' part. Query: lang=en,fr and format=json|hocr|alto|tsv|text.");

        return app;
    }

    /// <param name="http">The request context; supplies the cancellation token.</param>
    /// <param name="file">The uploaded image (multipart part named <c>file</c>).</param>
    /// <param name="ocr">The OCR service.</param>
    /// <param name="gate">Bounded concurrency admission control.</param>
    /// <param name="options">Sample configuration.</param>
    /// <param name="lang">Comma-separated language codes.</param>
    /// <param name="format">Output format.</param>
    private static async Task<IResult> HandleAsync(
        HttpContext http,
        IFormFile file,
        IEasyOcrService ocr,
        OcrConcurrencyGate gate,
        WebApiOptions options,
        string? lang,
        string? format)
    {
        // Parse before admission: a bad request should be rejected instantly, not after queueing.
        var languages = RequestParsing.Languages(lang, options);
        var outputFormat = RequestParsing.Format(format);
        var recognition = RequestParsing.RecognitionFor(outputFormat);

        if (Uploads.Validate(file, options) is { } rejection)
        {
            return rejection;
        }

        // RequestAborted flows all the way into detection and recognition, so a client that hangs up
        // stops the work instead of leaving a core spinning on a response nobody will read.
        var cancellationToken = http.RequestAborted;

        using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (!lease.Acquired)
        {
            return Uploads.TooBusy(http, gate);
        }

        await using var stream = file.OpenReadStream();
        var result = await ocr
            .ExtractTextFromImage(stream, languages, recognition, cancellationToken)
            .ConfigureAwait(false);

        http.Response.Headers["X-Ocr-Lines"] = result.Lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers["X-Ocr-Duration-Ms"] = ((long)result.Duration.TotalMilliseconds)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        return RequestParsing.Render(result, outputFormat, file.FileName);
    }
}
