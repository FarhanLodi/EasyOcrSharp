using System.Runtime.ExceptionServices;

namespace EasyOcrSharp.Structure.Engine.Recognition;

/// <summary>
/// A <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/> wrapper for the recognition stage:
/// runs inline when the degree is 1 (no scheduling overhead, exceptions propagate unchanged), and otherwise
/// unwraps a single-exception <see cref="AggregateException"/> so callers observe the same exception types
/// as the sequential path.
/// </summary>
internal static class BoundedParallel
{
    /// <summary>
    /// Resolves <see cref="Models.RecognitionOptions.MaxDegreeOfParallelism"/>: positive values are used as-is,
    /// anything else means "use every processor".
    /// </summary>
    /// <param name="requested">The configured degree of parallelism.</param>
    /// <returns>A degree of parallelism of at least 1.</returns>
    public static int ResolveDegree(int requested) => requested > 0 ? requested : Environment.ProcessorCount;

    /// <summary>
    /// Invokes <paramref name="body"/> for every index in <c>[0, count)</c>, at most
    /// <paramref name="maxDegree"/> at a time. All started iterations have finished when this returns or throws.
    /// </summary>
    /// <param name="count">Number of iterations.</param>
    /// <param name="maxDegree">Maximum concurrent iterations; values ≤ 1 run sequentially on the calling thread.</param>
    /// <param name="cancellationToken">Stops scheduling further iterations when cancelled.</param>
    /// <param name="body">The per-index work; must be safe to run concurrently for distinct indices.</param>
    public static void For(int count, int maxDegree, CancellationToken cancellationToken, Action<int> body)
    {
        if (count <= 0) return;
        if (maxDegree <= 1 || count == 1)
        {
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                body(i);
            }
            return;
        }

        try
        {
            Parallel.For(0, count, new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegree,
                CancellationToken = cancellationToken,
            }, body);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
    }
}
