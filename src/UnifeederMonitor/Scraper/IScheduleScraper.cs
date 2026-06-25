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

    /// <summary>
    /// Launches a short-lived headless browser, navigates to the configured page, and extracts the list
    /// of selectable vessel names (from the vessel dropdown/filter on the page). The browser is created
    /// and disposed within the call so it does not affect the long-lived scrape browser.
    /// </summary>
    /// <param name="ct">Cancelled when the operation should be aborted.</param>
    /// <returns>A sorted, de-duplicated list of vessel names. May be empty if none were found.</returns>
    /// <exception cref="PlaywrightException">Thrown when a Playwright/navigation operation fails.</exception>
    Task<List<string>> GetAvailableVesselsAsync(CancellationToken ct = default);
}
