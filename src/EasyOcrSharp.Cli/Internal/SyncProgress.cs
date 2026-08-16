namespace EasyOcrSharp.Cli.Internal;

/// <summary>
/// A synchronous <see cref="IProgress{T}"/>. Unlike <see cref="Progress{T}"/>, which posts to the
/// captured synchronization context (and therefore to the thread pool in a console app), this invokes
/// the handler inline — so a progress line never arrives after the summary it was meant to precede.
/// </summary>
/// <typeparam name="T">The progress payload.</typeparam>
/// <param name="handler">Called for every report, on the reporting thread.</param>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    /// <inheritdoc />
    public void Report(T value) => handler(value);
}
