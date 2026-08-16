using System.Globalization;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Services;
using EasyOcrSharp.WebApi.Internal;

namespace EasyOcrSharp.WebApi.Endpoints;

/// <summary>
/// <c>POST /ocr/pdf</c> — turn an uploaded scanned PDF into a searchable one by adding an invisible
/// text layer over the original page images.
/// </summary>
internal static class PdfEndpoints
{
    /// <summary>Maps the searchable-PDF endpoint onto <paramref name="app"/>.</summary>
    public static IEndpointRouteBuilder MapPdfEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/ocr/pdf", HandleAsync)
            // Same reasoning as the image endpoint: anonymous, cookie-less uploads carry no ambient
            // authority for a forged cross-site POST to abuse.
            .DisableAntiforgery()
            .WithName("OcrPdf")
            .WithSummary("Make an uploaded scanned PDF searchable.")
            .WithDescription(
                "multipart/form-data with a 'file' part. Query: lang=en,fr and dpi=200. Returns application/pdf.");

        return app;
    }

    /// <param name="http">The request context; supplies the cancellation token.</param>
    /// <param name="file">The uploaded PDF (multipart part named <c>file</c>).</param>
    /// <param name="ocr">The OCR service.</param>
    /// <param name="gate">Bounded concurrency admission control.</param>
    /// <param name="options">Sample configuration.</param>
    /// <param name="lang">Comma-separated language codes.</param>
    /// <param name="dpi">Rasterization DPI; clamped to the configured maximum.</param>
    private static async Task<IResult> HandleAsync(
        HttpContext http,
        IFormFile file,
        IEasyOcrService ocr,
        OcrConcurrencyGate gate,
        WebApiOptions options,
        string? lang,
        int? dpi)
    {
        // Parse and validate before admission, so a bad request never occupies a queue slot.
        var languages = RequestParsing.Languages(lang, options);
        int resolvedDpi = RequestParsing.Dpi(dpi, options);

        if (Uploads.Validate(file, options) is { } rejection)
        {
            return rejection;
        }

        var cancellationToken = http.RequestAborted;

        if (await Uploads.ValidatePdfAsync(file, cancellationToken).ConfigureAwait(false) is { } notPdf)
        {
            return notPdf;
        }

        using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (!lease.Acquired)
        {
            return Uploads.TooBusy(http, gate);
        }

        // CreateSearchablePdfAsync takes the document as bytes because PDFium needs random access to
        // the whole file; the upload is already bounded by MaxUploadBytes so this is safe to buffer.
        byte[] pdfBytes;
        await using (var stream = file.OpenReadStream())
        {
            using var buffer = new MemoryStream(capacity: (int)Math.Min(file.Length, int.MaxValue));
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            pdfBytes = buffer.ToArray();
        }

        var pdfOptions = new PdfOcrOptions
        {
            Dpi = resolvedDpi,
            JpegQuality = options.PdfJpegQuality,
            MaxPages = options.MaxPdfPages,
            MaxPageMegapixels = options.MaxPdfPageMegapixels,
        };

        var (result, pdf) = await ocr
            .CreateSearchablePdfAsync(pdfBytes, languages, options: null, pdfOptions, cancellationToken)
            .ConfigureAwait(false);

        http.Response.Headers["X-Ocr-Pages"] = result.Pages.Count.ToString(CultureInfo.InvariantCulture);

        // Surfaced so a caller can tell "searchable in every script" from "the text layer fell back to
        // Latin-1 because no suitable font was installed" — see PdfOcrResult.TextLayerFontStatus.
        http.Response.Headers["X-Ocr-Font-Status"] = result.TextLayerFontStatus.ToString();

        return Results.File(pdf, "application/pdf", RequestParsing.SearchablePdfName(file.FileName));
    }
}
