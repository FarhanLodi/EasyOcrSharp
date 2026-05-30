using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EasyOcrSharp;
using EasyOcrSharp.Services;
using Microsoft.Extensions.Logging;

try { Console.OutputEncoding = Encoding.UTF8; Console.InputEncoding = Encoding.UTF8; } catch { }

var pendingImagePath = args.Length > 0 ? args[0] : null;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
    builder.SetMinimumLevel(LogLevel.Information);
});

while (true)
{
    Console.WriteLine();
    Console.WriteLine("=== EasyOcrSharp v2 Demo (native ONNX) ===");
    Console.WriteLine("1) Run OCR on an image");
    Console.WriteLine("2) Show model cache location");
    Console.WriteLine("3) Clear console");
    Console.WriteLine("4) Exit");
    Console.Write("Select an option: ");

    var choice = Console.ReadLine()?.Trim();
    Console.WriteLine();

    switch (choice)
    {
        case "1": await RunImageOcrAsync(); break;
        case "2": ShowCachePath(); break;
        case "3": case "cls": case "clear": Console.Clear(); break;
        case "4": case "q": case "Q": return 0;
        default: Console.WriteLine("Unknown option."); break;
    }
}

async Task RunImageOcrAsync()
{
    if (!string.IsNullOrWhiteSpace(pendingImagePath))
        Console.WriteLine("Press Enter to reuse: {0}", pendingImagePath);
    Console.Write("Image path: ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input)) input = pendingImagePath;
    if (string.IsNullOrWhiteSpace(input)) { Console.WriteLine("No image path."); return; }

    var fullPath = Path.GetFullPath(input);
    if (!File.Exists(fullPath)) { Console.WriteLine($"File '{fullPath}' not found."); return; }
    pendingImagePath = fullPath;

    Console.Write("Language codes (comma-separated, default: en): ");
    var langInput = Console.ReadLine()?.Trim();
    var languages = string.IsNullOrWhiteSpace(langInput)
        ? new[] { "en" }
        : langInput.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().ToLowerInvariant()).Distinct().ToArray();

    try
    {
        await using var ocr = new EasyOcrService(logger: loggerFactory.CreateLogger<EasyOcrService>());
        Console.WriteLine($"Languages: {string.Join(", ", languages)}");
        Console.WriteLine("First run downloads ~80 MB detector + ~15 MB per script pack.");
        Console.WriteLine();

        var result = await ocr.ExtractTextFromImage(fullPath, languages);

        Console.WriteLine();
        Console.WriteLine("=== Result ===");
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new TimeSpanConverter() }
        });
        Console.WriteLine(json);
    }
    catch (EasyOcrSharpException ex) { Console.WriteLine($"OCR error: {ex}"); }
    catch (Exception ex) { Console.WriteLine($"Unexpected error: {ex}"); }
}

void ShowCachePath()
{
    var cacheOverride = Environment.GetEnvironmentVariable("EASYOCRSHARP_CACHE");
    if (!string.IsNullOrWhiteSpace(cacheOverride))
        Console.WriteLine("Cache override (env): {0}", cacheOverride);
    else
        Console.WriteLine("Default cache: {0}",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyOcrSharp", "models"));
}

internal sealed class TimeSpanConverter : System.Text.Json.Serialization.JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => TimeSpan.TryParse(reader.GetString(), out var r) ? r : TimeSpan.Zero;

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        => writer.WriteStringValue($"{value.TotalSeconds:F2}s");
}
