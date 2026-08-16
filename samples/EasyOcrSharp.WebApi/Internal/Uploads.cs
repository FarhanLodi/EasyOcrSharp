using System.Globalization;

namespace EasyOcrSharp.WebApi.Internal;

/// <summary>
/// Upload admission checks shared by the image and PDF endpoints. These run <b>before</b> the
/// concurrency gate is entered, so a malformed or oversized request is rejected immediately instead of
/// occupying a queue slot behind real work.
/// </summary>
internal static class Uploads
{
    /// <summary>
    /// Validates an uploaded part. Returns <c>null</c> when the upload is acceptable, or the
    /// <see cref="IResult"/> to return to the caller when it is not.
    /// </summary>
    /// <param name="file">The multipart part named <c>file</c>, or null when it was absent.</param>
    /// <param name="options">Sample configuration supplying <see cref="WebApiOptions.MaxUploadBytes"/>.</param>
    /// <remarks>
    /// The size check is belt-and-braces: Kestrel and the form reader are configured with the same
    /// limit in <c>Program.cs</c>, so an oversized body is normally rejected before it is ever
    /// buffered. This catches the case where the multipart part itself is larger than the declared
    /// limit and keeps the failure a clean 413 rather than an exception.
    /// </remarks>
    public static IResult? Validate(IFormFile? file, WebApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (file is null || file.Length == 0)
        {
            return Results.Problem(
                title: "No file uploaded.",
                detail: "Send multipart/form-data with a non-empty part named 'file'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > options.MaxUploadBytes)
        {
            return Results.Problem(
                title: "Upload too large.",
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"The upload is {file.Length} bytes; this server accepts at most {options.MaxUploadBytes}."),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        return null;
    }

    /// <summary>
    /// The response returned when the concurrency gate could not admit a request before
    /// <see cref="OcrConcurrencyGate.QueueTimeout"/> elapsed.
    /// </summary>
    /// <remarks>
    /// 503 with <c>Retry-After</c> is the honest answer: the work was never started, so the client is
    /// free to retry. Shedding load here — rather than queueing without bound — is what keeps latency
    /// bounded when traffic exceeds what the box can OCR.
    /// </remarks>
    public static IResult TooBusy(HttpContext http, OcrConcurrencyGate gate)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);

        int retryAfter = Math.Max(1, (int)Math.Ceiling(gate.QueueTimeout.TotalSeconds));
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        return Results.Problem(
            title: "Server busy.",
            detail: string.Create(
                CultureInfo.InvariantCulture,
                $"All {gate.Capacity} OCR slots are in use and the queue wait exceeded {gate.QueueTimeout.TotalSeconds:0.#}s. Retry shortly."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Rejects an upload that does not look like a PDF. Returns <c>null</c> when it does.
    /// </summary>
    /// <remarks>
    /// Checked by signature rather than by file name or content type, both of which the client
    /// controls: every PDF begins with <c>%PDF-</c>. Reading five bytes is far cheaper than
    /// discovering the problem inside the rasterizer.
    /// </remarks>
    public static async Task<IResult?> ValidatePdfAsync(IFormFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        var header = new byte[5];
        await using (var probe = file.OpenReadStream())
        {
            int read = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

            if (read < header.Length || !header.AsSpan().SequenceEqual("%PDF-"u8))
            {
                return Results.Problem(
                    title: "Not a PDF.",
                    detail: "The uploaded file does not start with the %PDF- signature.",
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
        }

        return null;
    }
}
