using EasyOcrSharp.Models;
using EasyOcrSharp.Export;

namespace EasyOcrSharp.WebApi.Internal;

/// <summary>Output representations <c>POST /ocr</c> can return.</summary>
internal enum OcrOutputFormat
{
    /// <summary>The full <see cref="OcrResult"/> as JSON (default).</summary>
    Json,

    /// <summary>hOCR — HTML with embedded geometry, understood by DMS tooling.</summary>
    Hocr,

    /// <summary>ALTO XML v4 — the layout format used by libraries and digitization workflows.</summary>
    Alto,

    /// <summary>Tesseract-style tab-separated values, one row per word.</summary>
    Tsv,

    /// <summary>Plain recognized text, nothing else.</summary>
    Text,
}

/// <summary>
/// Parses and validates the query string. Every value here comes from an anonymous caller, so each one
/// is checked against an allow-list and rejected with a 400 rather than being passed through and
/// surfacing later as a 500 (or, worse, as work the server was never meant to do).
/// </summary>
internal static class RequestParsing
{
    /// <summary>
    /// Resolves <c>?lang=en,fr</c> against <see cref="WebApiOptions.AllowedLanguages"/>. Empty or absent
    /// falls back to <see cref="WebApiOptions.DefaultLanguages"/>.
    /// </summary>
    /// <exception cref="BadHttpRequestException">The value is malformed or not enabled.</exception>
    public static string[] Languages(string? lang, WebApiOptions options)
    {
        if (string.IsNullOrWhiteSpace(lang))
        {
            return options.DefaultLanguages;
        }

        var requested = lang.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested.Length == 0)
        {
            return options.DefaultLanguages;
        }

        if (requested.Length > options.MaxLanguagesPerRequest)
        {
            throw new BadHttpRequestException(
                $"At most {options.MaxLanguagesPerRequest} languages may be requested at once.");
        }

        foreach (var code in requested)
        {
            // Validate the shape before echoing anything back, so an error message can never become a
            // vehicle for attacker-chosen content.
            if (!IsLanguageCodeShape(code))
            {
                throw new BadHttpRequestException("The 'lang' parameter must be a comma-separated list of language codes.");
            }

            if (!options.AllowedLanguages.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                throw new BadHttpRequestException(
                    $"Language '{code}' is not enabled on this server. Enabled: {string.Join(", ", options.AllowedLanguages)}.");
            }
        }

        return requested;
    }

    /// <summary>Resolves <c>?format=json|hocr|alto|tsv|text</c>, defaulting to JSON.</summary>
    /// <exception cref="BadHttpRequestException">The value is not one of the supported formats.</exception>
    public static OcrOutputFormat Format(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return OcrOutputFormat.Json;
        }

        return format.Trim().ToLowerInvariant() switch
        {
            "json" => OcrOutputFormat.Json,
            "hocr" => OcrOutputFormat.Hocr,
            "alto" or "xml" => OcrOutputFormat.Alto,
            "tsv" => OcrOutputFormat.Tsv,
            "text" or "txt" or "plain" => OcrOutputFormat.Text,
            _ => throw new BadHttpRequestException("The 'format' parameter must be one of: json, hocr, alto, tsv, text."),
        };
    }

    /// <summary>Resolves <c>?dpi=</c> for PDF rasterization against the configured ceiling.</summary>
    /// <exception cref="BadHttpRequestException">The value is outside the permitted range.</exception>
    public static int Dpi(int? dpi, WebApiOptions options)
    {
        if (dpi is not { } requested)
        {
            return options.PdfDpi;
        }

        if (requested < 36 || requested > options.MaxPdfDpi)
        {
            throw new BadHttpRequestException($"The 'dpi' parameter must be between 36 and {options.MaxPdfDpi}.");
        }

        return requested;
    }

    /// <summary>
    /// Recognition settings for a given output format. hOCR, ALTO and TSV all carry per-word geometry,
    /// so the recognizer is asked for real word boxes (read off the CTC alignment, no extra inference)
    /// instead of letting the exporters fall back to splitting the line box by character count.
    /// </summary>
    public static RecognitionOptions RecognitionFor(OcrOutputFormat format) => new()
    {
        WordLevelDetail = format is OcrOutputFormat.Hocr or OcrOutputFormat.Alto or OcrOutputFormat.Tsv
            ? WordLevelDetail.Words
            : WordLevelDetail.None,
    };

    /// <summary>
    /// Renders a result in the requested format. The exporters are pure functions over the result, so
    /// this runs outside the concurrency gate.
    /// </summary>
    public static IResult Render(OcrResult result, OcrOutputFormat format, string? imageName) => format switch
    {
        OcrOutputFormat.Hocr => Results.Text(
            result.ToHocr(result.SourceWidth, result.SourceHeight, imageName), "text/html; charset=utf-8"),
        OcrOutputFormat.Alto => Results.Text(
            result.ToAlto(result.SourceWidth, result.SourceHeight, imageName), "application/xml; charset=utf-8"),
        OcrOutputFormat.Tsv => Results.Text(
            result.ToTsv(), "text/tab-separated-values; charset=utf-8"),
        OcrOutputFormat.Text => Results.Text(
            result.FullText, "text/plain; charset=utf-8"),
        _ => Results.Text(
            result.ToJson(indented: true), "application/json; charset=utf-8"),
    };

    /// <summary>
    /// Turns an uploaded file name into something safe to put in a <c>Content-Disposition</c> header.
    /// <c>IFormFile.FileName</c> is entirely attacker-controlled and may contain path separators,
    /// control characters or quotes.
    /// </summary>
    public static string SearchablePdfName(string? uploadedName)
    {
        var stem = Path.GetFileNameWithoutExtension(uploadedName ?? string.Empty);
        var cleaned = string.Concat(stem.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ')).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "document";
        }
        else if (cleaned.Length > 64)
        {
            cleaned = cleaned[..64].Trim();
        }

        return cleaned + ".searchable.pdf";
    }

    /// <summary>A conservative shape check for a language code: letters, digits and underscore only.</summary>
    private static bool IsLanguageCodeShape(string code)
    {
        if (code.Length is 0 or > 12)
        {
            return false;
        }

        foreach (var c in code)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
