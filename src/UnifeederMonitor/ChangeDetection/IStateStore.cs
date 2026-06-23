using UnifeederMonitor.Scraper;

namespace UnifeederMonitor.ChangeDetection;

/// <summary>Result of comparing a freshly scraped snapshot against the previously stored baseline.</summary>
public sealed record ChangeResult
{
    /// <summary>True when the current hash differs from the stored one (a real schedule change).</summary>
    public bool Changed { get; init; }

    /// <summary>True only on the very first run, when no baseline existed yet. Baseline is stored silently.</summary>
    public bool IsBaseline { get; init; }

    /// <summary>The hash computed from <see cref="CurrentSnapshot"/>, or null on failure.</summary>
    public string CurrentHash { get; init; } = string.Empty;

    /// <summary>The previously stored hash. Null on the first run.</summary>
    public string? PreviousHash { get; init; }

    /// <summary>The snapshot this result was computed from.</summary>
    public required ScheduleSnapshot CurrentSnapshot { get; init; }

    /// <summary>
    /// Rows present in the current snapshot but absent from the baseline. Empty unless <see cref="Changed"/>
    /// is true. A modified row appears here (and its old form in <see cref="RemovedRows"/>) because the
    /// diff is a set difference over the row strings.
    /// </summary>
    public List<string> AddedRows { get; init; } = [];

    /// <summary>
    /// Rows present in the baseline but absent from the current snapshot. Empty unless <see cref="Changed"/>
    /// is true. A modified row's previous form appears here.
    /// </summary>
    public List<string> RemovedRows { get; init; } = [];
}

/// <summary>
/// Persists the last-seen hash (and snapshot) so change detection survives process restarts.
/// </summary>
public interface IStateStore
{
    /// <summary>Loads the previously stored hash, or null if none exists yet.</summary>
    Task<string?> LoadHashAsync(CancellationToken stoppingToken = default);

    /// <summary>
    /// Loads the previously stored snapshot's rows (the human-readable row strings), or null if none
    /// exists yet. Used to compute a row-level diff (added/removed) on a detected change.
    /// </summary>
    Task<List<string>?> LoadPreviousRowsAsync(CancellationToken stoppingToken = default);

    /// <summary>Persists the current hash and the snapshot it was derived from.</summary>
    Task SaveAsync(string hash, ScheduleSnapshot snapshot, CancellationToken stoppingToken = default);
}
