namespace UnifeederMonitor.Scraper;

/// <summary>
/// Drives a headless browser against the target schedule page, performs the search,
/// and returns the extracted schedule snapshot.
/// </summary>
public interface IScheduleScraper : IAsyncDisposable
{
    /// <summary>
    /// Navigates to the configured page, performs the search for the configured query,
    /// waits for the results table, and extracts the meaningful columns.
    /// </summary>
    /// <param name="stoppingToken">Cancelled when the host is shutting down.</param>
    /// <returns>The extracted snapshot. May contain zero rows if no results were returned.</returns>
    /// <exception cref="TimeoutException">Thrown when the page or results table fails to load in time.</exception>
    /// <exception cref="PlaywrightException">Thrown when a Playwright operation fails after all internal fallbacks.</exception>
    Task<ScheduleSnapshot> ScrapeAsync(CancellationToken stoppingToken = default);
}
