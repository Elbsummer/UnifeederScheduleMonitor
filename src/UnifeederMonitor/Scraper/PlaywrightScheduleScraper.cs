using Microsoft.Playwright;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnifeederMonitor.Configuration;

namespace UnifeederMonitor.Scraper;

/// <summary>
/// Playwright-based implementation of <see cref="IScheduleScraper"/>.
///
/// The browser (<see cref="IPlaywright"/> + <see cref="IBrowser"/>) is created lazily on first use
/// and reused across scrape cycles (browsers are expensive to launch). A fresh <see cref="IPage"/>
/// is created per scrape so each cycle starts from a clean state.
///
/// All page interaction is intentionally resilient: it prefers role/label locators (which survive
/// ASP.NET WebForms ClientID churn) and falls back to the configurable selectors in
/// <see cref="SelectorsOptions"/>, so a page redesign can usually be fixed by editing
/// appsettings.json without a rebuild.
/// </summary>
public sealed class PlaywrightScheduleScraper : IScheduleScraper
{
    private readonly MonitorOptions _options;
    private readonly ILogger<PlaywrightScheduleScraper> _logger;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _disposed;

    /// <summary>Navigation tab labels that must never be treated as vessel names (case-insensitive).</summary>
    private static readonly HashSet<string> _navTabExclusions =
        new(StringComparer.OrdinalIgnoreCase) { "Master Schedule", "Port", "Vessel" };

    public PlaywrightScheduleScraper(
        IOptions<MonitorOptions> options,
        ILogger<PlaywrightScheduleScraper> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ScheduleSnapshot> ScrapeAsync(CancellationToken stoppingToken = default)
    {
        await EnsureBrowserAsync(stoppingToken).ConfigureAwait(false);

        // Per-cycle fresh page so __VIEWSTATE / session cookies never leak between ticks.
        await using var context = await _browser!.NewContextAsync().ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        // Playwright 1.60 expresses default timeouts in milliseconds (float).
        page.SetDefaultTimeout(_options.TimeoutSeconds * 1000f);
        page.SetDefaultNavigationTimeout(_options.TimeoutSeconds * 1000f);

        var sel = _options.Selectors;

        // 1. Navigate to the schedule page. The initial ASP.NET load is heavy (themes, WebResource.axd,
        //    multiple UpdatePanels), so we give it a generous 60s navigation timeout independent of the
        //    (shorter) per-element default timeout used for the rest of the interaction.
        _logger.LogInformation("Navigating to {Url}", _options.TargetUrl);
        await page.GotoAsync(
            _options.TargetUrl,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000, // 60s, overriding the 15s global navigation timeout
            })
            .WaitAsync(stoppingToken).ConfigureAwait(false);

        // 2. Activate the "Vessel" search tab. This is a REQUIRED step: the vessel input and the
        //    #searchByVesselButton are only visible/active on that tab, and ASP.NET renders the
        //    Port tab by default. If this fails, we must NOT proceed to fill/click — bubble up.
        await ActivateVesselTabAsync(page, sel, stoppingToken).ConfigureAwait(false);

        // 3. Type the vessel name into the autocomplete input (fill + Tab to register), tick the
        //    "Show terminal information" checkbox, then submit. Each step is required and throws on
        //    failure — never proceeds if one fails.
        await FillVesselNameAsync(page, sel, stoppingToken).ConfigureAwait(false);
        await CheckTerminalInfoBoxAsync(page, sel, stoppingToken).ConfigureAwait(false);
        await ClickSearchButtonAsync(page, sel, stoppingToken).ConfigureAwait(false);

        // 4. Wait for the results data to render (first data row visible, not just the panel), and
        //    resolve the results container for scoped extraction.
        var resultsContainer = await WaitForResultsAsync(page, sel, stoppingToken).ConfigureAwait(false);

        // 5. (Debug only) dump the DOM to help an operator discover real selectors.
        if (_options.HeadedDebug)
        {
            await DumpDomAsync(page).ConfigureAwait(false);
        }

        // 6. Extract the meaningful columns, scoped to the results container so stray tables
        //    elsewhere on the page (ads, navigation, footers) never pollute the snapshot.
        var rows = await ExtractRowsAsync(resultsContainer, sel, stoppingToken).ConfigureAwait(false);

        // 7. Capture a PDF of the results page while it's still loaded. The snapshot is handed to the
        //    alerters on a detected change so the email service can attach it. Skipped in headed/debug
        //    mode because Chromium's PdfAsync only works headless; we log a warning so the operator knows
        //    no attachment will be available.
        byte[]? pdf = await CapturePdfAsync(page, stoppingToken).ConfigureAwait(false);

        var snapshot = new ScheduleSnapshot
        {
            SearchQuery = _options.SearchQuery,
            Rows = rows,
            CapturedAtUtc = DateTime.UtcNow,
            PdfBytes = pdf,
        };

        _logger.LogInformation(
            "Extracted {Count} row(s) for query \"{Query}\".",
            snapshot.RowCount, snapshot.SearchQuery);

        return snapshot;
    }

    /// <summary>
    /// Activates the "Vessel" search tab. REQUIRED before interacting with the vessel form: the vessel
    /// input and <c>#searchByVesselButton</c> only exist/become interactive on this tab, and ASP.NET
    /// renders a different tab by default. Throws on failure so the worker logs it and retries — never
    /// proceeds to fill/click when the tab could not be activated.
    /// </summary>
    private async Task ActivateVesselTabAsync(
        IPage page, SelectorsOptions sel, CancellationToken ct)
    {
        // Resilient locators that survive ASP.NET WebForms ClientID churn. Try role=tab (exact "Vessel")
        // first, then an <a> link whose text is "Vessel", then a loose text match.
        var tabName = sel.VesselSearchTabText;
        ILocator tab = page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = tabName, Exact = true });

        // Wait for the tab itself to be ready; WaitForAsync resolves when attached+visible, else throws.
        try
        {
            await tab.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = (int)TimeSpan.FromSeconds(_options.TimeoutSeconds).TotalMilliseconds,
            }).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Fall back to an <a> link (some ASP.NET tab strips render tabs as plain anchors).
            tab = page.Locator("a").Filter(new LocatorFilterOptions { HasText = tabName });
            await tab.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = (int)TimeSpan.FromSeconds(_options.TimeoutSeconds).TotalMilliseconds,
            }).WaitAsync(ct).ConfigureAwait(false);
        }

        // ClickAsync performs Playwright's actionability checks and THROWS on timeout/hidden — no silent skip.
        await tab.First.ClickAsync().WaitAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Activated the \"{Tab}\" search tab.", tabName);

        // Give a client-side tab swap a moment to settle before we touch the form.
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NetworkIdle is best-effort here; the form reveal is what matters, and FillAsync below
            // will actionability-check the input regardless.
            _logger.LogDebug(ex, "NetworkIdle did not settle after tab click; continuing.");
        }
    }

    /// <summary>
    /// Types the vessel name into the &lt;input type="text"&gt; autocomplete field, then DETERMINISTICALLY
    /// waits for the AJAX suggestion item matching the query and clicks it. ASP.NET AJAX AutoComplete
    /// (backed by an UpdatePanel) does NOT register the vessel on an instant <c>FillAsync</c> — it needs
    /// real per-keystroke events AND an explicit suggestion selection to populate the hidden Vessel ID.
    /// Blind keyboard keys (ArrowDown/Enter) or a fixed delay race the suggestion load and silently miss
    /// it; waiting on the actual suggestion element is race-free. Throws on failure so the worker logs
    /// the real cause and retries — never proceeds.
    /// </summary>
    private async Task FillVesselNameAsync(
        IPage page, SelectorsOptions sel, CancellationToken ct)
    {
        var inputSelector = sel.VesselInputSelector;
        var timeoutMs = (int)TimeSpan.FromSeconds(_options.TimeoutSeconds).TotalMilliseconds;
        var input = page.Locator(inputSelector).First;

        // Wait for the input to be visible; throws if it never appears (e.g. wrong tab active).
        try
        {
            await input.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs,
            }).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new PlaywrightException(
                $"Could not find a visible vessel input via '{inputSelector}'. Is the Vessel tab active? " +
                "Adjust Monitor:Selectors:VesselInputSelector in appsettings.json.", ex);
        }

        // Clear any leftover value, then type slowly (150ms/keystroke). The slow per-keystroke events are
        // what trigger the ASP.NET AJAX AutoComplete to query its suggestion service.
        try
        {
            await input.FillAsync(string.Empty).WaitAsync(ct).ConfigureAwait(false);
            await input.PressSequentiallyAsync(
                _options.SearchQuery,
                new LocatorPressSequentiallyOptions { Delay = 150, Timeout = timeoutMs })
                .WaitAsync(ct).ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            throw new PlaywrightException(
                $"Could not type vessel \"{_options.SearchQuery}\" into the input '{inputSelector}'. " +
                "Adjust Monitor:SearchQuery or Monitor:Selectors:VesselInputSelector.", ex);
        }

        // DETERMINISTIC selection: wait for the autocomplete suggestion whose visible text matches the
        // query, then click it. No blind delays, no ArrowDown/Enter — we wait on the real DOM element so
        // a slow AJAX response can't be missed. Filtering by text guards against a stray default item.
        var suggestion = page.Locator(sel.AutocompleteSuggestionSelector)
            .Filter(new LocatorFilterOptions { HasText = _options.SearchQuery })
            .First;

        try
        {
            await suggestion.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs,
            }).WaitAsync(ct).ConfigureAwait(false);
            await suggestion.ClickAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new PlaywrightException(
                $"Typed \"{_options.SearchQuery}\" but no matching autocomplete suggestion appeared via " +
                $"'{sel.AutocompleteSuggestionSelector}' within {_options.TimeoutSeconds}s. The suggestion " +
                "service may be slow, the query may not match any vessel, or the selector needs adjusting " +
                "(Monitor:Selectors:AutocompleteSuggestionSelector in appsettings.json).", ex);
        }
        catch (PlaywrightException ex)
        {
            throw new PlaywrightException(
                $"Found the autocomplete suggestion for \"{_options.SearchQuery}\" but failed to click it " +
                $"via '{sel.AutocompleteSuggestionSelector}'.", ex);
        }

        // Brief pause so ASP.NET can register the internal HiddenField Vessel ID after the click, before
        // we move on to the checkbox / submit.
        await Task.Delay(500, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Typed vessel \"{Query}\" and clicked the matching autocomplete suggestion.",
            _options.SearchQuery);
    }

    /// <summary>
    /// Ticks the "Show terminal information" checkbox required before submitting the search. The selector
    /// is scoped under <c>#searchByVesselTab</c> so it matches exactly the checkbox on the active Vessel
    /// tab, never the duplicate on the hidden Port tab. The underlying <c>&lt;input&gt;</c> is visually
    /// hidden by ASP.NET custom styling, so we wait for ATTACHED (not Visible) and check it with
    /// <c>Force = true</c>, which is idempotent. Throws on failure so the worker logs the real cause and
    /// retries — never proceeds.
    /// </summary>
    private async Task CheckTerminalInfoBoxAsync(
        IPage page, SelectorsOptions sel, CancellationToken ct)
    {
        var checkboxSelector = sel.TerminalInfoCheckboxSelector;
        var timeoutMs = (int)TimeSpan.FromSeconds(_options.TimeoutSeconds).TotalMilliseconds;
        var checkbox = page.Locator(checkboxSelector).First;

        // The <input> is visually hidden, so wait for ATTACHED rather than Visible. Throws if absent
        // (e.g. wrong/unknown tab container id — adjust Monitor:Selectors:TerminalInfoCheckboxSelector).
        try
        {
            await checkbox.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = timeoutMs,
            }).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new PlaywrightException(
                $"Could not find the 'Show terminal information' checkbox via '{checkboxSelector}'. " +
                "Is the Vessel tab active and is the selector scoped to #searchByVesselTab? " +
                "Adjust Monitor:Selectors:TerminalInfoCheckboxSelector in appsettings.json.", ex);
        }

        // Force=true bypasses the visibility actionability check that the hidden input would otherwise fail.
        // CheckAsync is idempotent: it ensures the box is checked without toggling an already-checked one.
        await checkbox.CheckAsync(new LocatorCheckOptions { Force = true })
            .WaitAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Checked the 'Show terminal information' checkbox (forced).");
    }

    /// <summary>
    /// Clicks the "Show Schedules" submit button. Waits for the configured selector
    /// (<c>#searchByVesselButton</c>, a static ID) and clicks it; throws on failure. Falls back to a
    /// role=button lookup only if the configured selector doesn't resolve — but always throws if neither
    /// matches, never silently proceeds.
    /// </summary>
    private async Task ClickSearchButtonAsync(
        IPage page, SelectorsOptions sel, CancellationToken ct)
    {
        var button = page.Locator(sel.SearchButton).First;
        var timeoutMs = (int)TimeSpan.FromSeconds(_options.TimeoutSeconds).TotalMilliseconds;

        try
        {
            await button.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs,
            }).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // Fallback to role=button, but keep it loud.
            var byRole = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = "Show Schedules",
                Exact = false,
            });
            try
            {
                await byRole.First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = timeoutMs,
                }).WaitAsync(ct).ConfigureAwait(false);
                button = byRole.First;
            }
            catch (TimeoutException)
            {
                throw new PlaywrightException(
                    $"Could not find a visible submit button via '{sel.SearchButton}' or role=button " +
                    $"'Show Schedules'. Is the Vessel tab active?", ex);
            }
        }

        // Short stability pause: let any lingering JavaScript validators (autocomplete commit, checkbox
        // toggle handlers) settle before we click, so the postback actually fires.
        await Task.Delay(500, ct).ConfigureAwait(false);

        // ClickAsync actionability-checks and throws on failure.
        await button.ClickAsync().WaitAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Clicked the submit button via '{Selector}'.", sel.SearchButton);
    }

    /// <summary>
    /// Waits for the results data to render after submission, then returns the results container so row
    /// extraction can be scoped to it.
    ///
    /// CRITICAL: we do NOT return as soon as <see cref="SelectorsOptions.ResultsContainerSelector"/>
    /// (#searchByVesselResultsPanel) becomes visible. That panel appears instantly while the ASP.NET
    /// UpdatePanel is still async-rendering the inner data table; returning then produced a 24 KB
    /// half-rendered PDF and raw=0 rows. Instead we wait for the FIRST DATA ROW
    /// (<c>ResultsContainerSelector + ' ' + ResultsRowSelector</c>) to become visible — that only happens
    /// once the table has actually been populated.
    ///
    /// A genuine empty search ("No results found") renders no rows, so we detect that explicitly and
    /// return gracefully (the caller will record a 0-row snapshot) instead of throwing into the retry loop.
    /// </summary>
    private async Task<ILocator> WaitForResultsAsync(IPage page, SelectorsOptions sel, CancellationToken ct)
    {
        // Give the postback + any XHR a chance to settle before looking for results.
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "NetworkIdle wait timed out; proceeding to wait for results directly.");
        }

        var containerSelector = sel.ResultsContainerSelector;
        var rowSelector = sel.ResultsRowSelector;
        var timeoutMs = (int)TimeSpan.FromSeconds(_options.TimeoutSeconds).TotalMilliseconds;

        var container = page.Locator(containerSelector).First;

        // Step 1: the panel itself must be present (proves the postback fired).
        try
        {
            await container.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs,
            }).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new PlaywrightException(
                $"Results container '{containerSelector}' did not become visible within " +
                $"{_options.TimeoutSeconds}s. The search likely did not complete (verify the Vessel tab was " +
                "activated, the autocomplete suggestion was selected, and the submit button was clicked). " +
                "If the page layout changed, adjust Monitor:Selectors:ResultsContainerSelector.", ex);
        }

        // Step 2: wait for the FIRST DATA ROW inside the container. The panel renders instantly but the
        // UpdatePanel populates the inner <table> a beat later — this is the wait that closes the race
        // which caused raw=0 + a truncated PDF.
        var firstDataRow = page.Locator($"{containerSelector} {rowSelector}").First;
        try
        {
            await firstDataRow.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs,
            }).WaitAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Results data is rendered (first row matched '{Container} {Row}').",
                containerSelector, rowSelector);
        }
        catch (TimeoutException)
        {
            // No data row appeared. Distinguish a genuine "No results found" page (return gracefully, no
            // retry — the caller records a legitimate 0-row snapshot) from a still-broken/empty render
            // (throw so the worker retries).
            if (await IsNoResultsStateAsync(container, sel, ct).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "Search returned no results ('{NoResultsText}' shown in '{Container}'). " +
                    "Proceeding with a 0-row snapshot; no alert will fire unless rows later appear.",
                    sel.NoResultsText, containerSelector);
            }
            else
            {
                throw new PlaywrightException(
                    $"Results panel '{containerSelector}' is visible but no data row matched " +
                    $"'{containerSelector} {rowSelector}' within {_options.TimeoutSeconds}s, and no " +
                    $"'{sel.NoResultsText}' marker was found. The page may be mid-render or the selectors " +
                    "need adjusting (Monitor:Selectors:ResultsRowSelector / NoResultsText).");
            }
        }

        return container;
    }

    /// <summary>
    /// Returns true if the results container is showing the configured "no results" text, indicating a
    /// genuine empty search rather than a load/render failure.
    /// </summary>
    private async Task<bool> IsNoResultsStateAsync(ILocator container, SelectorsOptions sel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sel.NoResultsText))
        {
            return false;
        }

        try
        {
            var text = await container.InnerTextAsync().WaitAsync(ct).ConfigureAwait(false);
            return text.Contains(sel.NoResultsText, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts the configured cell selectors from each results row, scoped to <paramref name="container"/>
    /// (the schedule table) so stray tables elsewhere on the page can't pollute the snapshot.
    /// </summary>
    private async Task<List<ScheduleRow>> ExtractRowsAsync(
        ILocator container, SelectorsOptions sel, CancellationToken ct)
    {
        var rows = new List<ScheduleRow>();
        var rowLocators = await container.Locator(sel.ResultsRowSelector).AllAsync().WaitAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Row extraction: container matched '{RowSelector}', found {Count} raw row(s).",
            sel.ResultsRowSelector, rowLocators.Count);

        var skippedHidden = 0;
        var skippedEmpty = 0;

        foreach (var rowLocator in rowLocators)
        {
            ct.ThrowIfCancellationRequested();

            // Skip non-visible rows (e.g. hidden template/pager rows some ASP.NET grids render).
            try
            {
                if (!await rowLocator.IsVisibleAsync().WaitAsync(ct).ConfigureAwait(false))
                {
                    skippedHidden++;
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skippedHidden++;
                continue;
            }

            var cells = new List<string>(sel.CellSelectors.Length);
            foreach (var cellSelector in sel.CellSelectors)
            {
                try
                {
                    var text = await rowLocator.Locator(cellSelector).First
                        .InnerTextAsync()
                        .WaitAsync(ct).ConfigureAwait(false);
                    cells.Add(Normalize(text));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A missing cell shouldn't abort the whole row; record an empty placeholder.
                    cells.Add(string.Empty);
                }
            }

            // Skip entirely empty rows (they're noise, not data).
            if (cells.Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                rows.Add(new ScheduleRow { Cells = cells });
            }
            else
            {
                skippedEmpty++;
            }
        }

        if (rows.Count == 0)
        {
            // This is the 0-rows diagnostic: the table was found and a PDF rendered, but no rows
            // matched. The most likely cause is ResultsRowSelector / CellSelectors mismatch with the
            // actual grid structure — inspect debug_dom.html and adjust the Selectors in appsettings.json.
            _logger.LogWarning(
                "Row extraction yielded 0 rows (raw={Raw}, skippedHidden={Hidden}, skippedEmpty={Empty}). " +
                "The results table rendered but '{RowSelector}' / CellSelectors did not match its rows. " +
                "Inspect debug_dom.html and adjust Monitor:Selectors:ResultsRowSelector and CellSelectors.",
                rowLocators.Count, skippedHidden, skippedEmpty, sel.ResultsRowSelector);
        }
        else
        {
            _logger.LogInformation(
                "Row extraction: kept {Kept} row(s) (skipped {Hidden} hidden, {Empty} empty).",
                rows.Count, skippedHidden, skippedEmpty);
        }

        return rows;
    }

    /// <summary>Writes the full results-page DOM next to the executable, for one-time selector discovery.</summary>
    private async Task DumpDomAsync(IPage page)
    {
        try
        {
            var html = await page.ContentAsync().ConfigureAwait(false);
            var path = Path.Combine(AppContext.BaseDirectory, "debug_dom.html");
            await File.WriteAllTextAsync(path, html).ConfigureAwait(false);
            _logger.LogInformation("Debug DOM written to {Path}. Open it in a browser to inspect element IDs.", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write debug DOM dump.");
        }
    }

    /// <summary>
    /// Renders the current page to a landscape A4 PDF (backgrounds printed). Captured while the
    /// Playwright page is still open so the alerters can attach it on a detected change.
    ///
    /// Chromium can ONLY produce PDFs in headless mode; in headed/debug mode <c>PdfAsync</c> throws, so we
    /// skip it and return null with a warning. The PDF is returned in-memory (no temp file to clean up).
    /// Never throws — a PDF capture failure must not fail the scrape.
    /// </summary>
    private async Task<byte[]?> CapturePdfAsync(IPage page, CancellationToken ct)
    {
        if (_options.HeadedDebug || (_options.BrowserHeadless is false && _options.HeadedDebug))
        {
            _logger.LogWarning(
                "Skipping PDF capture: the browser is running headed (HeadedDebug=true). " +
                "Set HeadedDebug=false to enable PDF attachments for change alerts.");
            return null;
        }

        try
        {
            var pdf = await page.PdfAsync(new PagePdfOptions
            {
                Format = "A4",
                Landscape = true,          // the schedule table is wide; landscape avoids column clipping
                PrintBackground = true,    // preserve the site's banding/styling for readability
            }).WaitAsync(ct).ConfigureAwait(false);

            _logger.LogInformation("Captured results-page PDF ({Kb} KB).", pdf.Length / 1024);
            return pdf;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal: the change alert still fires via console/Discord; only the attachment is missing.
            _logger.LogWarning(ex, "Failed to capture results-page PDF; change alerts will be sent without an attachment.");
            return null;
        }
    }

    /// <summary>Collapses whitespace and trims, so cosmetic formatting never trips the change detector.</summary>
    private static string Normalize(string text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Lazily creates the Playwright runtime and the browser the first time it's needed.</summary>
    private async Task EnsureBrowserAsync(CancellationToken ct)
    {
        if (_browser is not null) return;

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_browser is not null) return;

            _playwright = await Playwright.CreateAsync().WaitAsync(ct).ConfigureAwait(false);

            var launchOptions = BuildLaunchOptions(_options.BrowserHeadless && !_options.HeadedDebug);

            _browser = await _playwright.Chromium.LaunchAsync(launchOptions).WaitAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Launched browser (channel={Channel}, headless={Headless}).",
                launchOptions.Channel ?? "(default Chromium)", launchOptions.Headless);
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// Builds the standard browser launch options (system Edge channel, optional pinned executable path)
    /// so both the long-lived scrape browser and the short-lived vessel-list browser share one config.
    /// </summary>
    private BrowserTypeLaunchOptions BuildLaunchOptions(bool headless)
    {
        var launchOptions = new BrowserTypeLaunchOptions
        {
            Headless = headless,
            // Drive the system-installed Microsoft Edge instead of the Playwright-managed Chromium
            // build. This makes the deployment self-contained — no `playwright install` step, no
            // 150 MB Chromium download (painful over maritime satellite/eSIM links). Edge is present
            // on every stock Windows install. An explicit BrowserExecutablePath, if configured,
            // still wins (below) so a custom browser can be pinned.
            Channel = "msedge",
        };
        if (!string.IsNullOrWhiteSpace(_options.BrowserExecutablePath))
        {
            launchOptions.ExecutablePath = _options.BrowserExecutablePath;
        }
        return launchOptions;
    }

    /// <summary>
    /// Launches a dedicated, short-lived headless browser, navigates to the schedule page, activates the
    /// Vessel tab, and enumerates the selectable vessel names via
    /// <see cref="SelectorsOptions.VesselListSelector"/>. The browser is always closed/disposed in the
    /// finally block — independent of the reused scrape browser — so this can be called from the tray
    /// Settings UI without disturbing the background worker.
    /// </summary>
    public async Task<List<string>> GetAvailableVesselsAsync(CancellationToken ct = default)
    {
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        try
        {
            playwright = await Playwright.CreateAsync().WaitAsync(ct).ConfigureAwait(false);
            // Always headless for this background fetch, regardless of HeadedDebug.
            browser = await playwright.Chromium
                .LaunchAsync(BuildLaunchOptions(headless: true))
                .WaitAsync(ct).ConfigureAwait(false);

            await using var context = await browser.NewContextAsync().ConfigureAwait(false);
            var page = await context.NewPageAsync().ConfigureAwait(false);
            page.SetDefaultTimeout(_options.TimeoutSeconds * 1000f);
            page.SetDefaultNavigationTimeout(_options.TimeoutSeconds * 1000f);

            var sel = _options.Selectors;

            _logger.LogInformation("Fetching available vessels from {Url}", _options.TargetUrl);
            await page.GotoAsync(
                _options.TargetUrl,
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000,
                })
                .WaitAsync(ct).ConfigureAwait(false);

            // Best-effort: the vessel list/dropdown often lives on the Vessel tab. Activate it if we can,
            // but don't fail the whole fetch if the page exposes the list without tab activation.
            try
            {
                await ActivateVesselTabAsync(page, sel, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Vessel tab activation failed during vessel-list fetch; continuing anyway.");
            }

            // The vessel filter is a custom ASP.NET AJAX autocomplete, NOT a <select>. Wait for the input,
            // click the adjacent dropdown arrow to trigger the AJAX list load, then wait for the rendered
            // suggestion items. Fall back to typing a space to coax the autocomplete into rendering.
            var timeoutMs = (int)TimeSpan.FromSeconds(_options.TimeoutSeconds).TotalMilliseconds;
            // Scope to the VESSEL control by its stable ASP.NET id suffix. There are several
            // 'img.autoCompleteArrowDown' on the page (Voyage-Vessel, Voyage-No, Vessel, TradeLane); the
            // generic class selector clicks the first (a hidden Voyage-tab arrow), so the vessel popup
            // never opens. The id suffixes survive WebForms ClientID churn.
            const string inputSel = "input[id$='_searchByVesselAutoCompleter__textBox']";
            const string arrowSel = "img[id$='_searchByVesselAutoCompleter__textBoxArrowDown']";

            await page.WaitForSelectorAsync(inputSel, new PageWaitForSelectorOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs,
            }).WaitAsync(ct).ConfigureAwait(false);

            var itemLocator = page.Locator(sel.AutocompleteSuggestionSelector);
            // Click the vessel arrow to trigger the AJAX list, then wait for the option items to render.
            await page.ClickAsync(arrowSel, new PageClickOptions { Timeout = timeoutMs })
                .WaitAsync(ct).ConfigureAwait(false);
            // Short settle for the AJAX call (slow satellite links), then wait for the first option item.
            await page.WaitForTimeoutAsync(2500).WaitAsync(ct).ConfigureAwait(false);
            await itemLocator.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs,
            }).WaitAsync(ct).ConfigureAwait(false);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var optionLocators = await itemLocator.AllAsync().WaitAsync(ct).ConfigureAwait(false);

            foreach (var loc in optionLocators)
            {
                ct.ThrowIfCancellationRequested();
                string text;
                try
                {
                    text = await loc.InnerTextAsync().WaitAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    continue;
                }

                var normalized = Normalize(text);
                // Guard: skip empties and the page's navigation tab labels that can leak into the list.
                if (string.IsNullOrWhiteSpace(normalized)) continue;
                if (_navTabExclusions.Contains(normalized)) continue;
                names.Add(normalized);
            }

            var result = names
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.LogInformation(
                "Fetched {Count} unique vessel name(s) via '{Selector}'.", result.Count, sel.AutocompleteSuggestionSelector);
            return result;
        }
        finally
        {
            // Close + dispose the dedicated browser immediately, regardless of success/failure.
            if (browser is not null)
            {
                try { await browser.CloseAsync().ConfigureAwait(false); } catch { }
                try { await browser.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            playwright?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_browser is not null)
        {
            try { await _browser.CloseAsync().ConfigureAwait(false); } catch { }
            try { await _browser.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _playwright?.Dispose();
        _initGate.Dispose();
    }
}
