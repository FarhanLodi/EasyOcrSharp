namespace EasyOcrSharp.Structure.Engine.Recognition;

/// <summary>
/// One crop's recognition result as it flows through the OCR engine: the decoded text and confidence plus,
/// when word boxes were requested, the emitted characters' CTC timesteps and the geometry needed to place
/// them back on the crop.
/// </summary>
/// <param name="Text">The decoded text (already in display order for right-to-left packs).</param>
/// <param name="Confidence">The mean per-character confidence (0–1).</param>
internal sealed record RecognizedText(string Text, float Confidence)
{
    /// <summary>
    /// The emitted characters with their timesteps, in emission (left-to-right) order; <c>null</c> when
    /// character positions were not requested.
    /// </summary>
    public IReadOnlyList<CtcCharacter>? Characters { get; init; }

    /// <summary>
    /// The width, in pixels of the crop that was recognized, covered by one CTC timestep:
    /// <c>(tensorWidth / T) · (cropWidth / resizedWidth)</c>. Zero when character positions were not requested.
    /// </summary>
    public double StepWidth { get; init; }

    /// <summary>
    /// True when <see cref="Text"/> was reordered from emission order into display order (right-to-left
    /// packs), so per-word texts must be reordered the same way.
    /// </summary>
    public bool RightToLeft { get; init; }

    /// <summary>
    /// True when this reading came from the crop rotated 180° (the orientation confirmation chose the flip),
    /// so character positions run right-to-left across the unrotated crop.
    /// </summary>
    public bool Flipped { get; init; }
}
