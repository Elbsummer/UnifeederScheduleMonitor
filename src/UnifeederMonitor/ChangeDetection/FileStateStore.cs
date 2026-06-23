using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnifeederMonitor.Configuration;
using UnifeederMonitor.Scraper;

namespace UnifeederMonitor.ChangeDetection;

/// <summary>
/// File-backed <see cref="IStateStore"/>. Writes a small JSON document containing the last hash,
/// the snapshot that produced it, and a timestamp, to the path resolved from <see cref="MonitorOptions"/>.
/// </summary>
public sealed class FileStateStore : IStateStore
{
    private readonly MonitorOptions _options;
    private readonly ILogger<FileStateStore> _logger;
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    // Reused serializer config. PropertyNameCaseInsensitive=true is CRITICAL: the writer uses a
    // CamelCase naming policy (so keys are "hash", "searchQuery", …), and without case-insensitive
    // reading the default deserializer (case-sensitive, expects "Hash") silently fails to bind every
    // property — leaving Hash empty and making every tick look like a first run (the alert-spam bug).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public FileStateStore(IOptions<MonitorOptions> options, ILogger<FileStateStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> LoadHashAsync(CancellationToken stoppingToken = default)
    {
        var path = _options.ResolveStateFilePath();
        if (!File.Exists(path))
        {
            _logger.LogInformation("No state file at {Path}; this looks like the first run.", path);
            return null;
        }

        await _ioGate.WaitAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var record = await JsonSerializer.DeserializeAsync<StateRecord>(stream, JsonOptions, stoppingToken)
                .ConfigureAwait(false);
            var hash = record?.Hash;
            if (string.IsNullOrEmpty(hash))
            {
                _logger.LogWarning(
                    "State file at {Path} parsed but contained no hash; treating as a first run.", path);
            }
            else
            {
                _logger.LogInformation("Loaded stored hash {Hash} from {Path}.", hash, path);
            }
            return hash;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read state file {Path}; treating as a first run.", path);
            return null;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<List<string>?> LoadPreviousRowsAsync(CancellationToken stoppingToken = default)
    {
        var path = _options.ResolveStateFilePath();
        if (!File.Exists(path))
        {
            return null;
        }

        await _ioGate.WaitAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var record = await JsonSerializer.DeserializeAsync<StateRecord>(stream, JsonOptions, stoppingToken)
                .ConfigureAwait(false);
            // Project each persisted ScheduleRow into its joined string form (same format the snapshot's
            // rows use for hashing/display) so the diff operates on readable row text.
            if (record?.LastSnapshot?.Rows is null)
            {
                _logger.LogWarning("State file at {Path} contained no previous rows; diff will be unavailable.", path);
                return null;
            }
            return record.LastSnapshot.Rows
                .Select(r => string.Join(" | ", r.Cells))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read previous rows from state file {Path}.", path);
            return null;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task SaveAsync(string hash, ScheduleSnapshot snapshot, CancellationToken stoppingToken = default)
    {
        var path = _options.ResolveStateFilePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var record = new StateRecord
        {
            Hash = hash,
            SearchQuery = snapshot.SearchQuery,
            RowCount = snapshot.RowCount,
            UpdatedAtUtc = DateTime.UtcNow,
            LastSnapshot = snapshot,
        };

        await _ioGate.WaitAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            // Write to a temp file first, then atomically replace, so a crash mid-write can never leave
            // a corrupt/truncated state.json that LoadHashAsync would treat as "no baseline" (the exact
            // failure that caused repeated false first-runs / alert spam on consecutive ticks).
            var tempPath = path + ".tmp";
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, record, JsonOptions, stoppingToken).ConfigureAwait(false);
            }
            File.Copy(tempPath, path, overwrite: true);
            try { File.Delete(tempPath); } catch { /* temp cleanup is best-effort */ }

            _logger.LogInformation("State persisted to {Path} (hash={Hash}, rows={Rows}).", path, hash, snapshot.RowCount);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// On-disk shape of the state file. Properties use explicit public setters so System.Text.Json can
    /// populate them on deserialization, and the (shared, case-insensitive) JsonOptions below make the
    /// read tolerant of camelCase-vs-PascalCase key drift between writer and reader.
    /// </summary>
    private sealed class StateRecord
    {
        public string Hash { get; set; } = string.Empty;
        public string SearchQuery { get; set; } = string.Empty;
        public int RowCount { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public ScheduleSnapshot? LastSnapshot { get; set; }
    }
}
