using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnifeederMonitor.Scraper;

namespace UnifeederMonitor.ChangeDetection;

/// <summary>
/// Computes a canonical, deterministic representation of a <see cref="ScheduleSnapshot"/> and
/// derives a SHA-256 hash from it. The canonical form ignores incidental ordering differences
/// and field case so that only genuine data changes move the hash.
/// </summary>
public sealed class ChangeDetector
{
    private readonly IStateStore _store;
    private readonly ILogger<ChangeDetector> _logger;

    // Deterministic, compact serialization -> stable bytes -> stable hash.
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Ignore the volatile CapturedAtUtc timestamp so it never affects the hash.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ChangeDetector(IStateStore store, ILogger<ChangeDetector> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Compares <paramref name="current"/> against the stored baseline.
    /// On the first run (no baseline) the baseline is stored silently and
    /// a <see cref="ChangeResult"/> with <see cref="ChangeResult.IsBaseline"/> = true is returned.
    /// </summary>
    public async Task<ChangeResult> CompareAsync(ScheduleSnapshot current, CancellationToken stoppingToken = default)
    {
        var previousHash = await _store.LoadHashAsync(stoppingToken).ConfigureAwait(false);
        var currentHash = ComputeHash(current);

        // First run: store the baseline without raising an alert.
        if (previousHash is null)
        {
            _logger.LogInformation(
                "First run: storing baseline hash {Hash} silently ({Rows} row(s)).",
                currentHash, current.RowCount);
            await _store.SaveAsync(currentHash, current, stoppingToken).ConfigureAwait(false);
            return new ChangeResult
            {
                Changed = false,
                IsBaseline = true,
                CurrentHash = currentHash,
                PreviousHash = null,
                CurrentSnapshot = current,
            };
        }

        var changed = !string.Equals(previousHash, currentHash, StringComparison.Ordinal);

        // Row-level diff. Only computed when the hash actually changed (cheap fast-path on the common
        // no-change tick). A modified row appears as one Removed (its old form) + one Added (its new
        // form) because the diff is a set difference over the row strings — that's by design and reads
        // naturally in the alert ("the old row was X, the new row is Y").
        List<string> addedRows = [];
        List<string> removedRows = [];
        if (changed)
        {
            var previousRows = await _store.LoadPreviousRowsAsync(stoppingToken).ConfigureAwait(false);
            var currentRows = current.Rows
                .Select(r => string.Join(" | ", r.Cells))
                .ToList();
            var previousSet = previousRows ?? [];

            // Use Distinct on each side so a row that legitimately repeats doesn't inflate the diff.
            addedRows = currentRows.Except(previousSet, StringComparer.Ordinal).ToList();
            removedRows = previousSet.Except(currentRows, StringComparer.Ordinal).ToList();

            _logger.LogWarning(
                "Schedule change detected: previous hash {Previous}, new hash {Current}. " +
                "Diff: +{Added} added/changed, -{Removed} removed/previous.",
                previousHash, currentHash, addedRows.Count, removedRows.Count);
        }
        else
        {
            _logger.LogInformation(
                "No change (rows={Rows}, hash={Hash}).", current.RowCount, currentHash);
        }

        return new ChangeResult
        {
            Changed = changed,
            IsBaseline = false,
            CurrentHash = currentHash,
            PreviousHash = previousHash,
            CurrentSnapshot = current,
            AddedRows = addedRows,
            RemovedRows = removedRows,
        };
    }

    /// <summary>
    /// Builds the canonical JSON of the snapshot (rows sorted for determinism, timestamp excluded)
    /// and returns its lowercase hex SHA-256 hash.
    /// </summary>
    public static string ComputeHash(ScheduleSnapshot snapshot)
    {
        // Sort rows so a row reordering (which is not a meaningful data change) doesn't move the hash.
        var canonical = new
        {
            query = snapshot.SearchQuery,
            rows = snapshot.Rows
                .Select(r => r.Cells.ToList())
                .OrderBy(cells => string.Join('\u0001', cells), StringComparer.Ordinal)
                .ToList(),
        };

        var json = JsonSerializer.Serialize(canonical, CanonicalJsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
