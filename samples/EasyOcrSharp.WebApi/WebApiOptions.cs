namespace EasyOcrSharp.WebApi;

/// <summary>
/// Everything the sample lets an operator tune, bound from the <c>EasyOcr</c> section of
/// configuration (appsettings.json, environment variables such as
/// <c>EasyOcr__MaxConcurrentOcr=4</c>, or any other provider).
/// </summary>
/// <remarks>
/// Every limit here exists because the endpoints face untrusted uploads. The defaults are deliberately
/// conservative: it is much easier to raise a limit after measuring than to discover in production that
/// a 900&#160;MB "image" took the pod down.
/// </remarks>
internal sealed class WebApiOptions
{
    /// <summary>Configuration section these options are bound from.</summary>
    public const string SectionName = "EasyOcr";

    /// <summary>
    /// Language codes a caller is allowed to ask for via <c>?lang=</c>. Anything else is rejected with
    /// 400 rather than silently triggering the download of a fresh recognizer pack — an unbounded
    /// language list is a denial-of-service lever on a public endpoint.
    /// </summary>
    /// <remarks>
    /// The default set is exactly the languages served by the single <c>latin_g2</c> recognizer, so the
    /// whole allow-list costs one model download. Widening it (e.g. <c>ja</c>, <c>ch_sim</c>, <c>ar</c>)
    /// is fine — just pre-seed the cache or expect a slow first request per added script.
    /// </remarks>
    public string[] AllowedLanguages { get; set; } = ["en", "fr", "de", "es", "it", "pt", "nl"];

    /// <summary>Languages used when the caller does not pass <c>?lang=</c>.</summary>
    public string[] DefaultLanguages { get; set; } = ["en"];

    /// <summary>Maximum number of languages accepted in a single request.</summary>
    public int MaxLanguagesPerRequest { get; set; } = 4;

    /// <summary>
    /// Model cache directory. Null falls back to the library default: the <c>EASYOCRSHARP_CACHE</c>
    /// environment variable, then a per-user folder under local application data. The container image
    /// sets <c>EASYOCRSHARP_CACHE=/models</c> and mounts a volume there.
    /// </summary>
    public string? ModelCachePath { get; set; }

    /// <summary>
    /// Strict offline mode: a model missing from the cache is a hard error instead of a download.
    /// Turn this on for air-gapped deployments once the cache has been pre-seeded.
    /// </summary>
    public bool Offline { get; set; }

    /// <summary>
    /// Decompression-bomb guard passed straight through to
    /// <c>EasyOcrServiceOptions.MaxImagePixels</c>: an upload whose header advertises more pixels than
    /// this is rejected before a single pixel is decoded. Default 40&#160;MP.
    /// </summary>
    public long MaxImagePixels { get; set; } = 40_000_000;

    /// <summary>
    /// Maximum accepted request body, in bytes. Enforced by Kestrel, by the multipart form reader, and
    /// re-checked per file. Default 25&#160;MiB.
    /// </summary>
    public long MaxUploadBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>
    /// How many OCR operations may run at once. OCR is CPU-bound and each operation already fans out
    /// internally, so admitting one request per core is a good way to make every request slow at the
    /// same time. Half the cores (minimum 1) is a saner starting point.
    /// </summary>
    public int MaxConcurrentOcr { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>
    /// How long a request waits for a slot before it is shed with 503 + <c>Retry-After</c>. Fast
    /// shedding beats an unbounded queue: the caller learns immediately instead of timing out.
    /// </summary>
    public int QueueTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// ONNX Runtime intra-op thread count. Null leaves the runtime default. Pin it when several
    /// containers share a node, otherwise every replica sizes its thread pool to the whole machine.
    /// </summary>
    public int? IntraOpNumThreads { get; set; }

    /// <summary>Maximum pages accepted in an uploaded PDF (<c>PdfOcrOptions.MaxPages</c>).</summary>
    public int MaxPdfPages { get; set; } = 50;

    /// <summary>
    /// Maximum rendered megapixels per PDF page (<c>PdfOcrOptions.MaxPageMegapixels</c>). Guards against
    /// a huge page box at a high DPI exhausting memory.
    /// </summary>
    public int MaxPdfPageMegapixels { get; set; } = 40;

    /// <summary>Rasterization DPI for PDFs when the caller does not pass <c>?dpi=</c>.</summary>
    public int PdfDpi { get; set; } = 200;

    /// <summary>Highest <c>?dpi=</c> a caller may request. Higher DPI is quadratically more work.</summary>
    public int MaxPdfDpi { get; set; } = 400;

    /// <summary>JPEG quality of the page images embedded in a searchable PDF (1-100).</summary>
    public int PdfJpegQuality { get; set; } = 75;

    /// <summary>
    /// Languages the health check expects to find cached, and the languages warmed up when
    /// <see cref="WarmUpOnStart"/> is set.
    /// </summary>
    public string[] WarmUpLanguages { get; set; } = ["en"];

    /// <summary>
    /// Download and initialize the models in the background at startup so the first real request does
    /// not pay cold-start latency. Off by default so <c>docker run</c> never blocks on the network.
    /// </summary>
    public bool WarmUpOnStart { get; set; }

    /// <summary>
    /// Log the library's metrics and spans through <c>ILogger</c> using nothing but
    /// <c>System.Diagnostics</c>. A stand-in for a real OpenTelemetry exporter — see the commented
    /// block in <c>Program.cs</c> — so the sample can demonstrate the instrumentation without dragging
    /// a telemetry backend into the restore graph.
    /// </summary>
    public bool LogTelemetry { get; set; }

    /// <summary>
    /// Fails fast at startup on a nonsensical configuration. A container that refuses to start with a
    /// bad limit is far better than one that serves traffic with an accidental 0-byte upload cap.
    /// </summary>
    public void Validate()
    {
        if (AllowedLanguages.Length == 0)
            throw new InvalidOperationException($"{SectionName}:AllowedLanguages must list at least one language code.");
        if (DefaultLanguages.Length == 0)
            throw new InvalidOperationException($"{SectionName}:DefaultLanguages must list at least one language code.");
        foreach (var language in DefaultLanguages)
        {
            if (!AllowedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{SectionName}:DefaultLanguages contains '{language}', which is not in AllowedLanguages.");
        }
        if (MaxLanguagesPerRequest < 1)
            throw new InvalidOperationException($"{SectionName}:MaxLanguagesPerRequest must be at least 1.");
        if (MaxUploadBytes < 1024)
            throw new InvalidOperationException($"{SectionName}:MaxUploadBytes must be at least 1024.");
        if (MaxImagePixels < 0)
            throw new InvalidOperationException($"{SectionName}:MaxImagePixels must be 0 (unlimited) or positive.");
        if (MaxConcurrentOcr < 1)
            throw new InvalidOperationException($"{SectionName}:MaxConcurrentOcr must be at least 1.");
        if (QueueTimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException($"{SectionName}:QueueTimeoutSeconds must be between 1 and 600.");
        if (IntraOpNumThreads is < 1)
            throw new InvalidOperationException($"{SectionName}:IntraOpNumThreads must be null or at least 1.");
        if (MaxPdfPages < 0)
            throw new InvalidOperationException($"{SectionName}:MaxPdfPages must be 0 (unlimited) or positive.");
        if (MaxPdfPageMegapixels < 0)
            throw new InvalidOperationException($"{SectionName}:MaxPdfPageMegapixels must be 0 (unlimited) or positive.");
        if (PdfDpi is < 36 or > 1200)
            throw new InvalidOperationException($"{SectionName}:PdfDpi must be between 36 and 1200.");
        if (MaxPdfDpi is < 36 or > 1200 || MaxPdfDpi < PdfDpi)
            throw new InvalidOperationException($"{SectionName}:MaxPdfDpi must be between 36 and 1200 and not below PdfDpi.");
        if (PdfJpegQuality is < 1 or > 100)
            throw new InvalidOperationException($"{SectionName}:PdfJpegQuality must be between 1 and 100.");
    }
}
