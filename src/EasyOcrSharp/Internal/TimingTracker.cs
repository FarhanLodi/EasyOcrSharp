using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Tracks timing for various initialization and processing components.
/// Provides detailed logging and summary capabilities.
/// </summary>
internal sealed class TimingTracker
{
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, TimingEntry> _timings = new();
    private readonly object _lock = new();
    
    public TimingTracker(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Starts timing a component operation.
    /// </summary>
    /// <param name="componentName">Name of the component being timed</param>
    /// <param name="description">Optional description of what's being done</param>
    /// <param name="isSubComponent">Whether this is a sub-component of another timing</param>
    /// <returns>A disposable timing scope</returns>
    public IDisposable StartTiming(string componentName, string? description = null, bool isSubComponent = false)
    {
        return new TimingScope(this, componentName, description, isSubComponent);
    }

    /// <summary>
    /// Records a timing measurement for a component.
    /// </summary>
    /// <param name="componentName">Name of the component</param>
    /// <param name="elapsed">Time elapsed</param>
    /// <param name="description">Optional description</param>
    /// <param name="isSubComponent">Whether this is a sub-component of another timing</param>
    public void RecordTiming(string componentName, TimeSpan elapsed, string? description = null, bool isSubComponent = false)
    {
        lock (_lock)
        {
            if (_timings.TryGetValue(componentName, out var existing))
            {
                existing.TotalTime = existing.TotalTime.Add(elapsed);
                existing.Count++;
                existing.LastDescription = description ?? existing.LastDescription;
            }
            else
            {
                _timings[componentName] = new TimingEntry
                {
                    ComponentName = componentName,
                    TotalTime = elapsed,
                    Count = 1,
                    LastDescription = description,
                    IsSubComponent = isSubComponent
                };
            }
        }

        var prefix = isSubComponent ? "  ↳" : "⏱️";
        var descriptionText = !string.IsNullOrEmpty(description) ? $" ({description})" : "";
        
        _logger?.LogInformation("{Prefix} {ComponentName}: {Duration:F2}s{Description}", 
            prefix, componentName, elapsed.TotalSeconds, descriptionText);
    }

    /// <summary>
    /// Gets all recorded timings.
    /// </summary>
    public IReadOnlyDictionary<string, TimingEntry> GetTimings()
    {
        lock (_lock)
        {
            return _timings.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone());
        }
    }

    /// <summary>
    /// Gets the total time for a specific component.
    /// </summary>
    public TimeSpan GetTotalTime(string componentName)
    {
        return _timings.TryGetValue(componentName, out var entry) ? entry.TotalTime : TimeSpan.Zero;
    }

    /// <summary>
    /// Gets a formatted summary as string for programmatic use.
    /// </summary>
    public string GetFormattedSummary()
    {
        var timings = GetTimings();
        if (!timings.Any())
        {
            return "No timing data recorded.";
        }

        var mainComponents = timings.Values.Where(t => !t.IsSubComponent).OrderByDescending(t => t.TotalTime);
        var totalTime = mainComponents.Sum(t => t.TotalTime.TotalSeconds);

        var summary = $"Total Runtime: {totalTime:F2}s\n";
        summary += "Main Components:\n";
        
        foreach (var timing in mainComponents)
        {
            var percentage = totalTime > 0 ? (timing.TotalTime.TotalSeconds / totalTime) * 100 : 0;
            summary += $"  • {timing.ComponentName}: {timing.TotalTime.TotalSeconds:F2}s ({percentage:F1}%)\n";
        }

        return summary;
    }

    /// <summary>
    /// Disposable timing scope that automatically records timing when disposed.
    /// </summary>
    private sealed class TimingScope : IDisposable
    {
        private readonly TimingTracker _tracker;
        private readonly string _componentName;
        private readonly string? _description;
        private readonly bool _isSubComponent;
        private readonly Stopwatch _stopwatch;

        public TimingScope(TimingTracker tracker, string componentName, string? description, bool isSubComponent = false)
        {
            _tracker = tracker;
            _componentName = componentName;
            _description = description;
            _isSubComponent = isSubComponent;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _tracker.RecordTiming(_componentName, _stopwatch.Elapsed, _description, _isSubComponent);
        }
    }
}

/// <summary>
/// Represents a timing entry for a component.
/// </summary>
public sealed class TimingEntry
{
    /// <summary>
    /// Gets or sets the name of the component being timed.
    /// </summary>
    public string ComponentName { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the total time spent on this component.
    /// </summary>
    public TimeSpan TotalTime { get; set; }
    
    /// <summary>
    /// Gets or sets the number of times this component was timed.
    /// </summary>
    public int Count { get; set; }
    
    /// <summary>
    /// Gets or sets the last description provided for this timing entry.
    /// </summary>
    public string? LastDescription { get; set; }
    
    /// <summary>
    /// Gets or sets a value indicating whether this is a sub-component timing.
    /// </summary>
    public bool IsSubComponent { get; set; }

    /// <summary>
    /// Creates a copy of this timing entry.
    /// </summary>
    /// <returns>A cloned TimingEntry instance.</returns>
    public TimingEntry Clone()
    {
        return new TimingEntry
        {
            ComponentName = ComponentName,
            TotalTime = TotalTime,
            Count = Count,
            LastDescription = LastDescription,
            IsSubComponent = IsSubComponent
        };
    }
}
