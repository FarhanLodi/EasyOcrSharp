using EasyOcrSharp.Services;
using EasyOcrSharp.WebApi;
using EasyOcrSharp.WebApi.Endpoints;
using EasyOcrSharp.WebApi.Internal;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- configuration
// Bound once and registered as a plain singleton: the endpoints take WebApiOptions directly rather
// than IOptions<T>, because nothing here reloads and the indirection buys nothing in a sample.
var options = new WebApiOptions();
builder.Configuration.GetSection(WebApiOptions.SectionName).Bind(options);
options.Validate();   // fail fast: a container that refuses to start beats one serving bad limits
builder.Services.AddSingleton(options);

// ---------------------------------------------------------------- upload limits
// Set in both places on purpose. Kestrel rejects an oversized body before it is buffered; the form
// reader's own cap would otherwise still default to 128 MB for multipart parts.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = options.MaxUploadBytes);
builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = options.MaxUploadBytes;
    form.MultipartHeadersLengthLimit = 16 * 1024;
});

// ---------------------------------------------------------------- the OCR service
// A singleton: ONNX sessions are expensive to build and safe to share. Every hardening lever the
// library exposes is wired to configuration so this sample is a reasonable starting point for a real
// deployment rather than a toy.
builder.Services.AddEasyOcrSharp(ocr =>
{
    ocr.ModelCachePath = options.ModelCachePath;
    ocr.MaxImagePixels = options.MaxImagePixels;
    ocr.IntraOpNumThreads = options.IntraOpNumThreads;

    // Auto already picks a GPU when a provider package is installed; nothing to configure per-host.
    ocr.ExecutionProvider = OcrExecutionProvider.Auto;

    // In a locked-down environment, ship the model cache into the image and set EasyOcr:Offline=true
    // so a missing model is a loud startup-visible error instead of a silent egress attempt.
    ocr.Download.Offline = options.Offline;
});

builder.Services.AddSingleton<OcrConcurrencyGate>();

// ---------------------------------------------------------------- health
builder.Services
    .AddHealthChecks()
    .AddEasyOcrHealthCheck(options.WarmUpLanguages);

// ---------------------------------------------------------------- errors
// ProblemDetails everywhere, including for the exceptions the library raises (ImageTooLarge,
// OfflineModelMissing, PdfProcessing, ...) which OcrExceptionHandler maps to sensible status codes.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<OcrExceptionHandler>();

// ---------------------------------------------------------------- optional extras
if (options.WarmUpOnStart)
{
    // Downloads and initializes the models in the background so the first real request is fast.
    builder.Services.AddHostedService<ModelWarmUpService>();
}

if (options.LogTelemetry)
{
    // Stands in for a real OpenTelemetry exporter. To use OTel instead, drop this and add:
    //
    //   builder.Services.AddOpenTelemetry()
    //       .WithMetrics(m => m.AddMeter(EasyOcrDiagnostics.MeterName).AddOtlpExporter())
    //       .WithTracing(t => t.AddSource(EasyOcrDiagnostics.ActivitySourceName).AddOtlpExporter());
    //
    // (requires the OpenTelemetry.Extensions.Hosting + OpenTelemetry.Exporter.OpenTelemetryProtocol
    // packages, deliberately not referenced here so the sample restores without a telemetry backend.)
    builder.Services.AddHostedService<EasyOcrTelemetryLogger>();
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// ---------------------------------------------------------------- endpoints
app.MapGet("/", () => Results.Content(HomePage.Html, "text/html; charset=utf-8"))
   .ExcludeFromDescription();

app.MapOcrEndpoints();
app.MapPdfEndpoints();

app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

app.Run();

/// <summary>
/// Exposed so an integration test host (<c>WebApplicationFactory&lt;Program&gt;</c>) can reference the
/// entry point of this top-level-statements program.
/// </summary>
public partial class Program;
