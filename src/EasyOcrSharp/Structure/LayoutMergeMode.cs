namespace EasyOcrSharp.Structure;

/// <summary>
/// How nested layout regions are resolved. A region counts as nested when at least 90% of its own area falls
/// inside another. Formulas are never absorbed by a non-formula region. Default <see cref="None"/>: the
/// PP-StructureV3 per-class merge modes apply (<c>large</c> for titles/images/formulas/charts, union
/// otherwise) unless <see cref="StructureOptions.LayoutClassMergeModes"/> overrides them.
/// </summary>
internal enum LayoutMergeMode
{
    /// <summary>
    /// No uniform mode chosen — the per-class defaults (or
    /// <see cref="StructureOptions.LayoutClassMergeModes"/>) decide. The default.
    /// </summary>
    None = 0,

    /// <summary>
    /// Take the union of nested regions, i.e. keep them all. Behaves exactly like <see cref="None"/>; kept as
    /// a named mode so the three merge strategies can be spelled out explicitly.
    /// </summary>
    Union = 1,

    /// <summary>
    /// Keep the enclosing block: every region contained by another is dropped. Use when you want one block
    /// per page area — a table returned instead of the table plus its inner text regions.
    /// </summary>
    Large = 2,

    /// <summary>
    /// Keep the inner blocks: a region survives when it contains nothing, or is itself contained by another.
    /// Use when you want the finest-grained regions rather than the wrappers around them.
    /// </summary>
    Small = 3,
}
