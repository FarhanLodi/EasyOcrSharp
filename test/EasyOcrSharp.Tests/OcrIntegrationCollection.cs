using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Groups every model-backed test class into one xUnit collection so they run one at a time.
/// </summary>
/// <remarks>
/// <para>
/// Each of these classes builds its own <c>EasyOcrService</c> — a CRAFT detector session plus one or
/// more recognizer sessions — and OCRs at <c>MaxDegreeOfParallelism == ProcessorCount</c>, with the PDF
/// ones additionally holding full rasterized pages in memory. xUnit's default is one collection per
/// class running concurrently, which on a 12-core box meant a dozen independent ONNX session sets
/// competing for RAM at once.
/// </para>
/// <para>
/// That surfaced as a genuinely confusing failure mode: an OCR assertion in a PDF-heavy class would
/// fail perhaps one run in three, always passing when the class was run on its own. Deliberately
/// over-parallelizing reproduced the real cause — <c>OnnxRuntimeException: bad allocation</c>, i.e.
/// ONNX Runtime's allocator running out of memory, not a correctness bug. Serializing these classes
/// removes the contention rather than papering over the symptom. The fast unit tests are unaffected and
/// still run fully in parallel.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OcrIntegrationCollection
{
    /// <summary>The collection name applied to every model-backed test class.</summary>
    public const string Name = "ocr-integration";
}
