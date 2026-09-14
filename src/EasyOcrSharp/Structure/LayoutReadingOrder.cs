namespace EasyOcrSharp.Structure;

/// <summary>
/// Which source decides the reading order written into <see cref="StructureBlock.Order"/> (and the order the
/// blocks are returned in).
/// </summary>
internal enum LayoutReadingOrder
{
    /// <summary>
    /// Use the default orderer — currently <see cref="XyCutEnhanced"/>, matching Python PP-StructureV3,
    /// which never trusts the layout model's own order column for final ordering (headers are emitted
    /// first, footers last, titles and vision blocks are re-inserted by weighted distance). The model's
    /// predicted order, when present, is still fed to the orderer as a tie-breaking hint. On a right-to-left
    /// page (<see cref="StructureOptions.RightToLeft"/>) it resolves to the direction-aware <see cref="XyCut"/>
    /// instead, because XY-Cut++ only reads left to right.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Always use the plain geometric XY-cut orderer, ignoring any order the model predicted and the
    /// XY-Cut++ label-aware rules. Choose this for the simplest, fully label-agnostic geometric ordering.
    /// </summary>
    XyCut = 1,

    /// <summary>
    /// Require the model's predicted order (PP-DocLayoutV3's detections carry an order index). On a model
    /// that does not emit one (the PicoDet PP-DocLayout-S/M and PP-DocLayout_plus-L exports), ordering
    /// falls back to the plain XY-cut orderer, since there is nothing else to use.
    /// </summary>
    Model = 2,

    /// <summary>
    /// The XY-Cut++ orderer ported from Python PP-StructureV3's <c>xycut_enhanced</c> pass: recursive
    /// XY-cuts augmented with label-aware rules — headers first, footers/unordered last, doc-title and
    /// caption blocks matched to their bodies, weighted-distance insertion of titles and vision blocks.
    /// This is what <see cref="Auto"/> resolves to; listed separately so callers can pin it explicitly.
    /// </summary>
    XyCutEnhanced = 3,
}
