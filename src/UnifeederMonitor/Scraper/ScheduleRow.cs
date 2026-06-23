using System.Text.Json.Serialization;

namespace UnifeederMonitor.Scraper;

/// <summary>
/// A single row of the scraped schedule, represented generically as the ordered list of
/// meaningful cell values selected via <c>Monitor.Selectors.CellSelectors</c>.
/// </summary>
public sealed record ScheduleRow
{
    /// <summary>The trimmed text content of each selected cell, in selector order.</summary>
    public List<string> Cells { get; init; } = [];

    public override string ToString() => string.Join(" | ", Cells);
}

/// <summary>
/// A complete snapshot of the schedule search result captured at a point in time.
/// This is the unit that gets hashed and compared across runs.
/// </summary>
public sealed record ScheduleSnapshot
{
    /// <summary>The query that produced this snapshot (echoed from config).</summary>
    public string SearchQuery { get; init; } = string.Empty;

    /// <summary>The rows extracted from the results table.</summary>
    public List<ScheduleRow> Rows { get; init; } = [];

    /// <summary>UTC timestamp at which the snapshot was captured.</summary>
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// A rendered PDF of the results page (landscape A4). Captured by the scraper while the Playwright
    /// page is still open, then attached to change alerts. Marked [JsonIgnore] so it never bloats the
    /// persisted state.json — it is only meaningful for the in-memory snapshot handed to alerters.
    /// Null when running in headed/debug mode (Chromium cannot produce PDFs with a visible window).
    /// </summary>
    [JsonIgnore]
    public byte[]? PdfBytes { get; init; }

    [JsonIgnore]
    public int RowCount => Rows.Count;

    public override string ToString() =>
        $"Snapshot(Query=\"{SearchQuery}\", Rows={RowCount}, CapturedAt={CapturedAtUtc:O})";
}
