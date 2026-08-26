namespace EasyOcrSharp.Structure.Layout;

/// <summary>
/// Parses a layout model's label sidecar into a raw-class-id → <see cref="StructureBlockType"/> map. The
/// sidecar lists the model's labels in class-id order (PP-DocLayout ships them in a small <c>.yml</c>);
/// each label name is normalized and matched to a <see cref="StructureBlockType"/> via
/// <see cref="MapName"/>, with unrecognized names resolving to <see cref="StructureBlockType.Unknown"/>.
/// </summary>
internal static class LayoutLabelMap
{
    /// <summary>
    /// The canonical 23-class PP-DocLayout label vocabulary in class-id order, shared by the PicoDet
    /// PP-DocLayout-S / -M models and the RT-DETR PP-DocLayout-L model. Used as the built-in fallback when a
    /// model's <c>.yml</c> sidecar is missing or unparseable so detections still map to real block types.
    /// (Reference: PaddleX <c>PP-DocLayout</c> <c>label_list</c>.)
    /// </summary>
    public static readonly IReadOnlyList<string> DocLayout23 = new[]
    {
        "paragraph_title", "image", "text", "number", "abstract", "content", "figure_title", "formula",
        "table", "table_title", "reference", "doc_title", "footnote", "header", "algorithm", "footer",
        "seal", "chart_title", "chart", "formula_number", "header_image", "footer_image", "aside_text",
    };

    /// <summary>
    /// The canonical 20-class PP-DocLayout_plus-L label vocabulary in class-id order. This is a <i>different</i>
    /// list from <see cref="DocLayout23"/> (no separate chart_title / header_image / footer_image; adds
    /// <c>reference_content</c>), so it is kept separately and used as the built-in fallback for the plus-L
    /// model. (Reference: PaddleX <c>PP-DocLayout_plus-L</c> <c>label_list</c>.)
    /// </summary>
    public static readonly IReadOnlyList<string> DocLayoutPlus20 = new[]
    {
        "paragraph_title", "image", "text", "number", "abstract", "content", "figure_title", "formula",
        "table", "reference", "doc_title", "footnote", "header", "algorithm", "footer", "seal", "chart",
        "formula_number", "aside_text", "reference_content",
    };

    /// <summary>
    /// The canonical 25-class PP-DocLayoutV3 label vocabulary in class-id order, as shipped in the model's
    /// <c>PP-DocLayoutV3_labels.txt</c> sidecar (one label per line, id = line index). This is the vocabulary
    /// of the RT-DETR layout model driven by <see cref="RtDetrLayoutDetector"/>; it is kept here as the
    /// built-in fallback used when the sidecar is missing or unparseable. (Verified by loading the ONNX and
    /// running it: e.g. id 21 = <c>table</c>, 22 = <c>text</c>, 14 = <c>image</c>.)
    /// </summary>
    public static readonly IReadOnlyList<string> DocLayoutV325 = new[]
    {
        "abstract", "algorithm", "aside_text", "chart", "content", "display_formula", "doc_title",
        "figure_title", "footer", "footer_image", "footnote", "formula_number", "header", "header_image",
        "image", "inline_formula", "number", "paragraph_title", "reference", "reference_content", "seal",
        "table", "text", "vertical_text", "vision_footnote",
    };

    /// <summary>
    /// Loads the label sidecar at <paramref name="labelPath"/> and returns the class-id → block-type map.
    /// Accepts either a plain one-label-per-line list (id = line index), simple <c>id: name</c> /
    /// <c>- name</c> YAML lines, or a PaddleX-style <c>label_list:</c> block (only entries under that key are
    /// read); only the label names are used, in file order. When the sidecar yields no usable labels the
    /// caller can fall back to <see cref="FromNames"/> with a canonical list.
    /// </summary>
    public static IReadOnlyDictionary<int, StructureBlockType> Load(string labelPath)
    {
        var map = new Dictionary<int, StructureBlockType>();
        if (string.IsNullOrEmpty(labelPath) || !File.Exists(labelPath)) return map;

        // A PaddleX inference.yml mixes the label_list with unrelated keys (paths, mean/std, etc.). If a
        // "label_list:" / "categories:" key is present, only consume the "- name" entries directly under it.
        var lines = File.ReadAllLines(labelPath);
        bool hasLabelBlock = lines.Any(l =>
        {
            var t = l.TrimStart();
            return t.StartsWith("label_list:", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("categories:", StringComparison.OrdinalIgnoreCase);
        });

        int autoId = 0;
        bool inLabelBlock = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (hasLabelBlock)
            {
                // Toggle the block on at the label_list/categories key; off at the next non-list key.
                if (line.StartsWith("label_list:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("categories:", StringComparison.OrdinalIgnoreCase))
                {
                    inLabelBlock = true;
                    continue;
                }
                if (!line.StartsWith("- ", StringComparison.Ordinal) && line != "-")
                {
                    inLabelBlock = false;
                    continue;
                }
                if (!inLabelBlock) continue;
            }

            // Support "<id>: <name>", "- <name>", or a bare "<name>".
            int id = autoId;
            string name = line;
            int colon = line.IndexOf(':');
            if (colon > 0 && int.TryParse(line.AsSpan(0, colon).Trim(), out int parsedId))
            {
                id = parsedId;
                name = line[(colon + 1)..].Trim();
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                name = line[2..].Trim();
            }

            name = name.Trim('"', '\'', ' ');
            if (name.Length == 0) continue;

            map[id] = MapName(name);
            autoId = id + 1;
        }

        return map;
    }

    /// <summary>
    /// Builds a class-id → block-type map from an ordered list of label names (id = list index), mapping each
    /// via <see cref="MapName"/>. Used to materialize the built-in <see cref="DocLayout23"/> /
    /// <see cref="DocLayoutPlus20"/> fallback vocabularies.
    /// </summary>
    public static IReadOnlyDictionary<int, StructureBlockType> FromNames(IReadOnlyList<string> names)
    {
        var map = new Dictionary<int, StructureBlockType>(names.Count);
        for (int i = 0; i < names.Count; i++) map[i] = MapName(names[i]);
        return map;
    }

    /// <summary>
    /// Maps a (case/format-insensitive) layout label name onto a <see cref="StructureBlockType"/>. Covers
    /// the PP-DocLayout / PP-DocLayout_plus label vocabularies; unknown names map to
    /// <see cref="StructureBlockType.Unknown"/>.
    /// </summary>
    public static StructureBlockType MapName(string name)
    {
        var key = name.Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
        return key switch
        {
            "text" or "plain_text" or "content" or "vertical_text" => StructureBlockType.Text,
            "title" or "section_title" or "section_header" or "paragraph_title" => StructureBlockType.Title,
            "doc_title" or "document_title" => StructureBlockType.DocTitle,
            "paragraph" => StructureBlockType.Paragraph,
            "list" or "list_item" => StructureBlockType.List,
            "table" => StructureBlockType.Table,
            "table_caption" or "table_title" => StructureBlockType.TableCaption,
            "figure" or "image" or "picture" => StructureBlockType.Figure,
            "figure_caption" or "figure_title" or "image_caption" => StructureBlockType.FigureCaption,
            "formula" or "equation" or "isolate_formula" or "interline_equation"
                or "display_formula" or "inline_formula" => StructureBlockType.Formula,
            "formula_number" or "formula_caption" or "equation_number" => StructureBlockType.FormulaNumber,
            "seal" or "stamp" => StructureBlockType.Seal,
            "chart" or "chart_title" => StructureBlockType.Chart,
            "header" or "page_header" or "header_image" => StructureBlockType.Header,
            "footer" or "page_footer" or "footer_image" => StructureBlockType.Footer,
            "reference" or "references" or "reference_content" => StructureBlockType.Reference,
            "footnote" or "vision_footnote" => StructureBlockType.Footnote,
            "page_number" or "number" => StructureBlockType.PageNumber,
            "abstract" => StructureBlockType.Abstract,
            "algorithm" => StructureBlockType.Algorithm,
            "aside" or "aside_text" or "sidebar" or "marginalia" => StructureBlockType.Aside,
            _ => StructureBlockType.Unknown,
        };
    }
}
