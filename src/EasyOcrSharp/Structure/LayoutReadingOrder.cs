namespace EasyOcrSharp.Structure;

/// <summary>
/// Which source decides the reading order written into <see cref="StructureBlock.Order"/> (and the order the
/// blocks are returned in).
/// </summary>
internal enum LayoutReadingOrder
{
    /// <summary>
    /// Use the model's own predicted order when the layout model emits one, otherwise fall back to
    /// <see cref="XyCut"/>. The default: PP-DocLayoutV3 is trained to predict reading order alongside the
    /// boxes (its detections carry an order index), while the PicoDet PP-DocLayout-S/M and
    /// PP-DocLayout_plus-L exports do not.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Always use the geometric XY-cut orderer, ignoring any order the model predicted. Choose this to keep
    /// ordering behaviour identical across layout models, or when a document's columns are recovered better
    /// by pure geometry.
    /// </summary>
    XyCut = 1,

    /// <summary>
    /// Require the model's predicted order. Identical to <see cref="Auto"/> on a model that emits one; on a
    /// model that does not, ordering falls back to XY-cut just the same, since there is nothing else to use.
    /// </summary>
    Model = 2,
}
