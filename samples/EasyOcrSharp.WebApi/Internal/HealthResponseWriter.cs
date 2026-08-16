using System.Buffers;
using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EasyOcrSharp.WebApi.Internal;

/// <summary>
/// Renders the health report as JSON by hand with <see cref="Utf8JsonWriter"/>.
/// </summary>
/// <remarks>
/// The default response writer emits only the word <c>Healthy</c>, which throws away the most useful
/// part of <c>EasyOcrHealthCheck</c>: its <c>data</c> payload names the cache directory and lists
/// exactly which model files are missing. Writing the JSON manually also keeps the endpoint free of
/// reflection-based serialization, so it survives trimming and Native AOT.
/// </remarks>
internal static class HealthResponseWriter
{
    /// <summary>Writes <paramref name="report"/> to the response as UTF-8 JSON.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("status", report.Status.ToString());
            json.WriteNumber("totalDurationMs", Math.Round(report.TotalDuration.TotalMilliseconds, 1));

            json.WriteStartObject("checks");
            foreach (var (name, entry) in report.Entries)
            {
                json.WriteStartObject(name);
                json.WriteString("status", entry.Status.ToString());
                if (!string.IsNullOrEmpty(entry.Description))
                {
                    json.WriteString("description", entry.Description);
                }

                json.WriteNumber("durationMs", Math.Round(entry.Duration.TotalMilliseconds, 1));

                if (entry.Data.Count > 0)
                {
                    json.WriteStartObject("data");
                    foreach (var (key, value) in entry.Data)
                    {
                        WriteValue(json, key, value);
                    }

                    json.WriteEndObject();
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        return context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).AsTask();
    }

    /// <summary>
    /// Writes one <c>data</c> entry. The health check contributes strings and string lists; anything
    /// else is stringified rather than reflected over.
    /// </summary>
    private static void WriteValue(Utf8JsonWriter json, string key, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNull(key);
                break;
            case string s:
                json.WriteString(key, s);
                break;
            case bool b:
                json.WriteBoolean(key, b);
                break;
            case long l:
                json.WriteNumber(key, l);
                break;
            case int i:
                json.WriteNumber(key, i);
                break;
            case IEnumerable items:
                json.WriteStartArray(key);
                foreach (var item in items)
                {
                    json.WriteStringValue(item?.ToString() ?? string.Empty);
                }

                json.WriteEndArray();
                break;
            default:
                json.WriteString(key, value.ToString() ?? string.Empty);
                break;
        }
    }
}
