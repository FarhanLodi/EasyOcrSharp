using EasyOcrSharp;
using EasyOcrSharp.Services;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
    }
    catch
    {
    }

    var pendingImagePath = args.Length > 0 ? args[0] : null;

    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
        builder.SetMinimumLevel(LogLevel.Information);
    });

    var runtimePath = Environment.GetEnvironmentVariable("EASYOCRSHARP_RUNTIME") ?? "D:\\Test-Runtime";
    EasyOcrService? sharedService = null;

    try
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("=== EasyOcrSharp Demo ===");
            Console.WriteLine("1) Run OCR on an image");
            Console.WriteLine("2) Show cache location");
            Console.WriteLine("3) Clear console");
            Console.WriteLine("4) Exit");
            Console.Write("Select an option: ");

            var choice = Console.ReadLine()?.Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1":
                    await HandleRunImageOcrAsync();
                    break;
                case "2":
                    DisplayCachePath();
                    break;
                case "3":
                case "cls":
                case "CLS":
                case "clear":
                case "CLEAR":
                    Console.Clear();
                    break;
                case "4":
                case "q":
                case "Q":
                    Console.WriteLine("Exiting EasyOcrSharp demo. Goodbye!");
                    return 0;
                default:
                    Console.WriteLine("Unknown option. Please choose 1-4.");
                    break;
            }
        }
    }
    finally
    {
        if (sharedService is not null)
        {
            await sharedService.DisposeAsync();
        }
    }

    async Task HandleRunImageOcrAsync()
    {
        Console.WriteLine("OCR Test");
        Console.WriteLine("Provide an image path to analyze.");
        if (!string.IsNullOrWhiteSpace(pendingImagePath))
        {
            Console.WriteLine("Press Enter to reuse: {0}", pendingImagePath);
        }

        Console.Write("Image path: ");
        var inputPath = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            inputPath = pendingImagePath;
        }

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            Console.WriteLine("No image path provided. Returning to menu.");
            return;
        }

        var fullPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath))
        {
            Console.WriteLine("File '{0}' was not found.", fullPath);
            return;
        }

        pendingImagePath = fullPath;

        // Ask for languages
        Console.Write("Enter language codes separated by commas (e.g., en,hi,ar) [default: en]: ");
        var langInput = Console.ReadLine()?.Trim();
        var languages = new List<string> { "en" };
        
        if (!string.IsNullOrWhiteSpace(langInput))
        {
            var parsed = langInput
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(lang => lang.Trim().ToLowerInvariant())
                .Where(lang => !string.IsNullOrWhiteSpace(lang))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (parsed.Count > 0)
            {
                languages = parsed;
            }
        }

        try
        {
            sharedService ??= new EasyOcrService(logger: loggerFactory.CreateLogger<EasyOcrService>());
            Console.WriteLine($"Using languages: {string.Join(", ", languages)}");
            Console.WriteLine("GPU will be automatically detected and used if available.");
            Console.WriteLine("Note: Languages like ch_sim, ch_tra, zh_sim, zh_tra, ja, ko, th will automatically include English.");
            Console.WriteLine();
            Console.WriteLine("⚠️  IMPORTANT: On first use, EasyOCR will download language models which may take several minutes.");
            Console.WriteLine("   Models are cached for future use. Please wait for the download to complete...");
            Console.WriteLine();
            var result = await sharedService.ExtractTextFromImage(fullPath, languages);

            Console.WriteLine();
            Console.WriteLine("=== JSON Response ===");
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters = { new TimeSpanConverter() }
            };
            var json = JsonSerializer.Serialize(result, jsonOptions);
            Console.WriteLine(json);
        }
        catch (EasyOcrSharpException ex)
        {
            Console.WriteLine("OCR initialization failed:");
            Console.WriteLine(ex);
        }
        catch (Exception ex)
        {
            Console.WriteLine("An unexpected error occurred:");
            Console.WriteLine(ex);
        }
    }

    static void DisplayCachePath()
    {
        var cacheOverride = Environment.GetEnvironmentVariable("EASYOCRSHARP_CACHE");
        if (!string.IsNullOrWhiteSpace(cacheOverride))
        {
            Console.WriteLine("Cache override detected at: {0}", cacheOverride);
        }
        else
        {
            Console.WriteLine("Cache path is managed automatically under the user's profile.");
            Console.WriteLine("Set the EASYOCRSHARP_CACHE environment variable before launching to override.");
        }
    }

}

internal class TimeSpanConverter : System.Text.Json.Serialization.JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return TimeSpan.TryParse(value, out var result) ? result : TimeSpan.Zero;
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"{value.TotalSeconds:F2}s");
    }
}
