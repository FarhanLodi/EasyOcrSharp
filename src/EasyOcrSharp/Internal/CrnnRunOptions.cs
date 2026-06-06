using EasyOcrSharp.Models;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Internal bundle of the recognition-stage knobs handed to <see cref="CrnnRecognizer"/>. Keeps the
/// recognizer's public signature stable as new tunables are added, and lets internal callers (e.g.
/// language auto-detection) pass minimal defaults.
/// </summary>
internal sealed record CrnnRunOptions
{
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;
    public bool AdjustContrast { get; init; } = true;
    public string? Allowlist { get; init; }
    public string? Blocklist { get; init; }
    public DecoderType Decoder { get; init; } = DecoderType.Greedy;
    public int BeamWidth { get; init; } = 5;
    public IReadOnlyCollection<string>? Dictionary { get; init; }
    public IReadOnlyList<int>? RotationInfo { get; init; }
    public int BatchSize { get; init; } = 1;
    public double ContrastThreshold { get; init; } = 0.1;
    public double AdjustContrastTarget { get; init; } = 0.5;

    /// <summary>Plain defaults — used by internal probes (language detection) that don't need tuning.</summary>
    public static readonly CrnnRunOptions Defaults = new();

    /// <summary>Projects the public <see cref="RecognitionOptions"/> onto the recognizer's knobs.</summary>
    public static CrnnRunOptions FromRecognition(RecognitionOptions o) => new()
    {
        MaxDegreeOfParallelism = o.MaxDegreeOfParallelism,
        AdjustContrast = o.AdjustContrast,
        Allowlist = o.Allowlist,
        Blocklist = o.Blocklist,
        Decoder = o.Decoder,
        BeamWidth = Math.Max(1, o.BeamWidth),
        Dictionary = o.Dictionary,
        RotationInfo = o.RotationInfo,
        BatchSize = Math.Max(1, o.BatchSize),
        ContrastThreshold = o.ContrastThreshold,
        AdjustContrastTarget = o.AdjustContrastTarget,
    };
}
