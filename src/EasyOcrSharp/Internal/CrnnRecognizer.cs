using EasyOcrSharp.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Crops detected regions out of the source image, rectifies them, runs the per-language
/// CRNN ONNX model, and CTC-decodes the output (via <see cref="CtcDecoder"/>) to text + confidence.
/// The preprocessing, decoding, confidence formula and low-confidence contrast retry all mirror
/// upstream EasyOCR so accuracy matches the reference implementation. Supports greedy and beam-search
/// decoding, per-box rotation, and optional batched inference.
/// </summary>
internal sealed class CrnnRecognizer : IDisposable
{
    public const int TargetHeight = 64;
    // Upper bound on the resized width fed to the model. EasyOCR doesn't cap single-box width;
    // this only guards against pathologically wide boxes blowing up memory / LSTM runtime.
    public const int MaxWidth = 4096;

    private readonly InferenceSession _session;
    private readonly string _imageInputName;
    private readonly string? _textInputName;
    private readonly string _outputName;
    private readonly string _characters;
    // Set once if the exported graph rejects a batch > 1; future calls then skip the batched path.
    private volatile bool _batchUnsupported;

    public CrnnRecognizer(string modelPath, string characters, SessionOptions? sessionOptions = null)
    {
        _session = new InferenceSession(modelPath, sessionOptions ?? new SessionOptions());
        // EasyOCR's Model.forward(input, text) — `text` is ignored for CTC but PyTorch
        // demanded it stay in the exported graph. Image is float, text is int64.
        _imageInputName = _session.InputMetadata
            .First(kv => kv.Value.ElementType == typeof(float)).Key;
        _textInputName = _session.InputMetadata
            .FirstOrDefault(kv => kv.Value.ElementType != typeof(float)).Key;
        _outputName = _session.OutputMetadata.Keys.First();
        _characters = characters;
    }

    public IReadOnlyList<OcrLine> Recognize(
        Image<Rgb24> source,
        IReadOnlyList<OcrPoint[]> polygons,
        CrnnRunOptions run)
    {
        // Precompute, once per call, which vocabulary positions may be emitted (null = all allowed).
        bool[]? allowed = CtcDecoder.BuildAllowedMask(_characters, run.Allowlist, run.Blocklist);
        WordTrie? trie = run.Decoder == DecoderType.WordBeamSearch ? WordTrie.Build(run.Dictionary) : null;

        var results = new OcrLine[polygons.Count];

        // Batched inference only helps the upright, single-pass path; rotation multiplies passes and
        // is handled per-box. Falls back automatically if the model can't run a batch > 1.
        bool useBatch = run.BatchSize > 1
            && (run.RotationInfo is null || run.RotationInfo.Count == 0)
            && !_batchUnsupported;

        if (useBatch)
        {
            RecognizeBatched(source, polygons, run, allowed, trie, results);
            return results;
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, run.MaxDegreeOfParallelism),
        };
        Parallel.For(0, polygons.Count, parallelOptions, i =>
        {
            results[i] = RecognizeRegion(source, polygons[i], run, allowed, trie);
        });
        return results;
    }

    private OcrLine RecognizeRegion(Image<Rgb24> source, OcrPoint[] poly, CrnnRunOptions run, bool[]? allowed, WordTrie? trie)
    {
        var box = OcrBoundingBox.FromPoints(poly);
        using var crop = PerspectiveWarp.Rectify(source, poly);
        if (crop is null || crop.Width == 0 || crop.Height == 0)
        {
            return new OcrLine { Text = string.Empty, Confidence = 0, BoundingPolygon = poly, BoundingBox = box };
        }

        var (text, conf) = RecognizeCropAllRotations(crop, run, allowed, trie);
        return new OcrLine { Text = text, Confidence = conf, BoundingPolygon = poly, BoundingBox = box };
    }

    /// <summary>
    /// Recognizes an already-rectified crop upright, then (if <see cref="CrnnRunOptions.RotationInfo"/>
    /// is set) at each requested angle, keeping the highest-confidence reading — EasyOCR's
    /// <c>rotation_info</c>.
    /// </summary>
    private (string Text, double Confidence) RecognizeCropAllRotations(Image<Rgb24> crop, CrnnRunOptions run, bool[]? allowed, WordTrie? trie)
    {
        var (bestText, bestConf) = RecognizeCrop(crop, run, allowed, trie);

        if (run.RotationInfo is { Count: > 0 } angles)
        {
            foreach (var angle in angles)
            {
                if (((angle % 360) + 360) % 360 == 0) continue;
                using var rotated = crop.Clone(ctx => ctx.Rotate(angle));
                var (text, conf) = RecognizeCrop(rotated, run, allowed, trie);
                if (conf > bestConf)
                {
                    bestConf = conf;
                    bestText = text;
                }
            }
        }
        return (bestText, bestConf);
    }

    /// <summary>One crop, EasyOCR's two-pass scheme: plain pass, then a contrast-stretched retry when
    /// the confidence falls below <see cref="CrnnRunOptions.ContrastThreshold"/>.</summary>
    private (string Text, double Confidence) RecognizeCrop(Image<Rgb24> crop, CrnnRunOptions run, bool[]? allowed, WordTrie? trie)
    {
        var first = RunInference(crop, adjustContrast: false, run.AdjustContrastTarget);
        if (first is not { } r1) return (string.Empty, 0.0);
        var (text, conf) = Decode(r1.Logits, r1.T, r1.C, run, allowed, trie);

        if (run.AdjustContrast && conf < run.ContrastThreshold)
        {
            var second = RunInference(crop, adjustContrast: true, run.AdjustContrastTarget);
            if (second is { } r2)
            {
                var (text2, conf2) = Decode(r2.Logits, r2.T, r2.C, run, allowed, trie);
                if (conf2 > conf)
                {
                    text = text2;
                    conf = conf2;
                }
            }
        }
        return (text, conf);
    }

    private (string Text, double Confidence) Decode(float[,] logits, int t, int c, CrnnRunOptions run, bool[]? allowed, WordTrie? trie)
        => CtcDecoder.Decode(logits, t, c, _characters, allowed, run.Decoder, run.BeamWidth, trie);

    private readonly record struct InferenceResult(float[,] Logits, int T, int C);

    private InferenceResult? RunInference(Image<Rgb24> crop, bool adjustContrast, double contrastTarget)
    {
        var tensorData = ImageProcessing.PreprocessForCrnn(crop, TargetHeight, MaxWidth, adjustContrast, out int width, contrastTarget);
        if (width < 1) return null;

        var input = new DenseTensor<float>(tensorData, new[] { 1, 1, TargetHeight, width });
        var feeds = new List<NamedOnnxValue>(2)
        {
            NamedOnnxValue.CreateFromTensor(_imageInputName, input),
        };
        if (_textInputName is not null)
        {
            var textPlaceholder = new DenseTensor<long>(new[] { 1, 1 });
            feeds.Add(NamedOnnxValue.CreateFromTensor(_textInputName, textPlaceholder));
        }

        try
        {
            using var runDisp = _session.Run(feeds);
            var output = runDisp.First(o => o.Name == _outputName).AsTensor<float>();
            return ExtractSingle(output);
        }
        catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
        {
            // Defence in depth: a single pathological box (degenerate shape the graph rejects) must
            // never abort the whole page — treat it as unreadable and continue.
            return null;
        }
    }

    /// <summary>Normalises a single-sample recognizer output (shape (T,1,C) or (1,T,C)) to (T, C).</summary>
    private static InferenceResult ExtractSingle(Tensor<float> output)
    {
        var dims = output.Dimensions;
        int t, c;
        if (dims.Length == 3 && dims[1] == 1) { t = dims[0]; c = dims[2]; }       // (T, 1, C)
        else if (dims.Length == 3 && dims[0] == 1) { t = dims[1]; c = dims[2]; }  // (1, T, C)
        else
        {
            throw new EasyOcrSharpException(
                $"Unexpected recognizer output shape [{string.Join(",", dims.ToArray())}]. Expected 3D tensor.");
        }

        // Both (T,1,C) and (1,T,C) are row-major contiguous and the singleton axis contributes nothing,
        // so logical element (i,j) sits at flat index i*c + j in either layout. Read the contiguous buffer
        // sequentially rather than via the strided multi-dimensional indexer (which recomputes the offset
        // and bounds-checks on every element — by far the slowest way to drain an ORT tensor).
        var data = AsReadOnlySpan(output);
        var logits = new float[t, c];
        int k = 0;
        for (int i = 0; i < t; i++)
            for (int j = 0; j < c; j++)
                logits[i, j] = data[k++];
        return new InferenceResult(logits, t, c);
    }

    /// <summary>Zero-copy view over an ORT output's contiguous buffer (falls back to a copy if not dense).</summary>
    private static ReadOnlySpan<float> AsReadOnlySpan(Tensor<float> tensor)
        => tensor is DenseTensor<float> dense ? dense.Buffer.Span : tensor.ToArray();

    // ---- batched inference ----

    private void RecognizeBatched(
        Image<Rgb24> source, IReadOnlyList<OcrPoint[]> polygons, CrnnRunOptions run, bool[]? allowed, WordTrie? trie, OcrLine[] results)
    {
        int n = polygons.Count;
        var crops = new Image<Rgb24>?[n];
        var boxes = new OcrBoundingBox[n];
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, run.MaxDegreeOfParallelism) };
        Parallel.For(0, n, parallelOptions, i =>
        {
            boxes[i] = OcrBoundingBox.FromPoints(polygons[i]);
            var c = PerspectiveWarp.Rectify(source, polygons[i]);
            if (c is not null && c.Width > 0 && c.Height > 0) crops[i] = c;
        });

        try
        {
            for (int start = 0; start < n; start += run.BatchSize)
            {
                int end = Math.Min(start + run.BatchSize, n);
                var idxs = new List<int>(run.BatchSize);
                for (int i = start; i < end; i++)
                {
                    if (crops[i] is null)
                        results[i] = new OcrLine { Text = string.Empty, Confidence = 0, BoundingPolygon = polygons[i], BoundingBox = boxes[i] };
                    else
                        idxs.Add(i);
                }
                if (idxs.Count == 0) continue;

                var datas = new float[idxs.Count][];
                var widths = new int[idxs.Count];
                int maxW = 1;
                for (int k = 0; k < idxs.Count; k++)
                {
                    datas[k] = ImageProcessing.PreprocessForCrnn(crops[idxs[k]]!, TargetHeight, MaxWidth, adjustContrast: false, out widths[k], run.AdjustContrastTarget);
                    if (widths[k] > maxW) maxW = widths[k];
                }

                var perItem = RunBatchInference(datas, widths, maxW);
                for (int k = 0; k < idxs.Count; k++)
                {
                    int i = idxs[k];
                    var (logits, t, c) = perItem[k];
                    var (text, conf) = Decode(logits, t, c, run, allowed, trie);

                    // Contrast retry stays per-item (only the few low-confidence boxes pay for it).
                    if (run.AdjustContrast && conf < run.ContrastThreshold)
                    {
                        var second = RunInference(crops[i]!, adjustContrast: true, run.AdjustContrastTarget);
                        if (second is { } r2)
                        {
                            var (text2, conf2) = Decode(r2.Logits, r2.T, r2.C, run, allowed, trie);
                            if (conf2 > conf) { text = text2; conf = conf2; }
                        }
                    }

                    results[i] = new OcrLine { Text = text, Confidence = conf, BoundingPolygon = polygons[i], BoundingBox = boxes[i] };
                }
            }
        }
        catch (Exception)
        {
            // The exported graph likely rejects batch > 1 (or its output shape is unexpected) —
            // fall back to per-box inference for this and all future calls.
            _batchUnsupported = true;
            Parallel.For(0, n, parallelOptions, i =>
            {
                var crop = crops[i];
                if (crop is null)
                {
                    results[i] = new OcrLine { Text = string.Empty, Confidence = 0, BoundingPolygon = polygons[i], BoundingBox = boxes[i] };
                    return;
                }
                var (text, conf) = RecognizeCropAllRotations(crop, run, allowed, trie);
                results[i] = new OcrLine { Text = text, Confidence = conf, BoundingPolygon = polygons[i], BoundingBox = boxes[i] };
            });
        }
        finally
        {
            foreach (var crop in crops) crop?.Dispose();
        }
    }

    private InferenceResult[] RunBatchInference(float[][] datas, int[] widths, int maxW)
    {
        int n = datas.Length;
        var batch = new float[n * TargetHeight * maxW];
        for (int b = 0; b < n; b++)
        {
            int w = widths[b];
            var data = datas[b];
            int baseB = b * TargetHeight * maxW;
            for (int y = 0; y < TargetHeight; y++)
            {
                int dstRow = baseB + y * maxW;
                int srcRow = y * w;
                Array.Copy(data, srcRow, batch, dstRow, w);
                // Pad the right edge by repeating the last column (EasyOCR's NormalizePAD), not zeros.
                float edge = w > 0 ? data[srcRow + w - 1] : 0f;
                for (int x = w; x < maxW; x++) batch[dstRow + x] = edge;
            }
        }

        var input = new DenseTensor<float>(batch, new[] { n, 1, TargetHeight, maxW });
        var feeds = new List<NamedOnnxValue>(2)
        {
            NamedOnnxValue.CreateFromTensor(_imageInputName, input),
        };
        if (_textInputName is not null)
        {
            feeds.Add(NamedOnnxValue.CreateFromTensor(_textInputName, new DenseTensor<long>(new[] { n, 1 })));
        }

        using var runDisp = _session.Run(feeds);
        var output = runDisp.First(o => o.Name == _outputName).AsTensor<float>();
        return ExtractBatch(output, n);
    }

    /// <summary>Splits a batched recognizer output (shape (T,N,C) or (N,T,C)) into per-sample (T,C).</summary>
    private static InferenceResult[] ExtractBatch(Tensor<float> output, int n)
    {
        if (output.Dimensions.Length != 3)
        {
            throw new EasyOcrSharpException(
                $"Unexpected batched recognizer output shape [{string.Join(",", output.Dimensions.ToArray())}].");
        }

        int d0 = output.Dimensions[0], d1 = output.Dimensions[1], d2 = output.Dimensions[2];
        var results = new InferenceResult[n];
        var data = AsReadOnlySpan(output);

        // Prefer (N, T, C) when the first axis matches the batch and the second doesn't.
        bool batchFirst = d0 == n && d1 != n;
        bool batchSecond = d1 == n && d0 != n;
        if (!batchFirst && !batchSecond)
        {
            // Ambiguous (e.g. N == T): default to (N, T, C).
            batchFirst = d0 == n;
        }

        if (batchFirst)
        {
            // (N, T, C): each sample's T*C block is contiguous at b*T*C.
            int t = d1, c = d2;
            for (int b = 0; b < n; b++)
            {
                var logits = new float[t, c];
                int k = b * t * c;
                for (int i = 0; i < t; i++)
                    for (int j = 0; j < c; j++)
                        logits[i, j] = data[k++];
                results[b] = new InferenceResult(logits, t, c);
            }
        }
        else
        {
            // (T, N, C): element (i, j) of sample b is at i*N*C + b*C + j.
            int t = d0, c = d2;
            for (int b = 0; b < n; b++)
            {
                var logits = new float[t, c];
                for (int i = 0; i < t; i++)
                {
                    int rowBase = i * n * c + b * c;
                    for (int j = 0; j < c; j++)
                        logits[i, j] = data[rowBase + j];
                }
                results[b] = new InferenceResult(logits, t, c);
            }
        }
        return results;
    }

    public void Dispose() => _session.Dispose();
}
