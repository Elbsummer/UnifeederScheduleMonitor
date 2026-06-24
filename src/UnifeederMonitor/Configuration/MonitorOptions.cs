using System.ComponentModel.DataAnnotations;

namespace UnifeederMonitor.Configuration;

/// <summary>
/// Strongly-typed configuration for the Unifeeder schedule monitor.
/// Bound from the "Monitor" section of appsettings.json and validated on startup.
/// </summary>
public sealed class MonitorOptions
{
    /// <summary>The schedule page URL. Validated by the scraper at runtime rather than at DI startup so
    /// the app can boot with a blank config and be configured via the Settings tray UI.</summary>
    [Url(ErrorMessage = "Monitor:TargetUrl must be a valid absolute URL when set.")]
    public string TargetUrl { get; set; } = string.Empty;

    /// <summary>The vessel name / search term to look up. Deliberately NOT [Required]: the app must start
    /// in an idle/unconfigured state when this is empty, so the user can open the tray Settings dialog
    /// and enter it. The Worker guards against an empty value and idles instead of scraping.</summary>
    public string SearchQuery { get; set; } = string.Empty;

    /// <summary>Interval between two scrape cycles. Defaults to 30 minutes.</summary>
    [Range(1, 1440, ErrorMessage = "PollingIntervalMinutes must be between 1 and 1440.")]
    public int PollingIntervalMinutes { get; set; } = 30;

    /// <summary>Per-operation Playwright timeout, in seconds. Kept low (15s) during debugging so failures surface fast.</summary>
    [Range(5, 600, ErrorMessage = "TimeoutSeconds must be between 5 and 600.")]
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>How many times a failed scrape cycle is retried before giving up for the tick.</summary>
    [Range(0, 10, ErrorMessage = "MaxRetries must be between 0 and 10.")]
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay between retries, in seconds. Actual delay grows linearly with the attempt number.</summary>
    [Range(0, 300, ErrorMessage = "RetryDelaySeconds must be between 0 and 300.")]
    public int RetryDelaySeconds { get; set; } = 15;

    /// <summary>Run the browser headless (true) or with a visible window (false, useful for selector discovery).</summary>
    public bool BrowserHeadless { get; set; } = true;

    /// <summary>
    /// When true, the browser window is shown regardless of <see cref="BrowserHeadless"/> and the full DOM
    /// of the results page is written to debug_dom.html next to the executable. Use this once to discover
    /// the real element IDs/selectors on the target page, then set it back to false.
    /// </summary>
    public bool HeadedDebug { get; set; } = false;

    /// <summary>Optional path to a Chromium executable. Leave empty to use the Playwright-managed build.</summary>
    public string? BrowserExecutablePath { get; set; }

    /// <summary>Locators/selectors used to interact with the target ASP.NET page. All configurable so an
    /// operator can recover from page redesign without recompiling.</summary>
    [ValidateObjectMembers]
    public SelectorsOptions Selectors { get; set; } = new();

    /// <summary>
    /// Absolute path to the state file that holds the last-seen hash + snapshot.
    /// Empty -> defaults to %LocalAppData%/UnifeederMonitor/state.json.
    /// </summary>
    public string StateFilePath { get; set; } = string.Empty;

    /// <summary>Discord webhook URL. Leave empty to disable webhook alerts (console logging still runs).</summary>
    public string? DiscordWebhookUrl { get; set; }

    /// <summary>Email (SMTP/Gmail) alert configuration. See <see cref="EmailOptions"/>.</summary>
    public EmailOptions Email { get; set; } = new();

    /// <summary>Resolves the effective state file path, applying the default when none is configured.</summary>
    public string ResolveStateFilePath() =>
        string.IsNullOrWhiteSpace(StateFilePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnifeederMonitor",
                "state.json")
            : StateFilePath;
}

/// <summary>Selector/locator configuration for the target page.</summary>
public sealed class SelectorsOptions
{
    /// <summary>Text used to locate the "Vessel" search tab/link. Matched exactly (the tab is labeled "Vessel").</summary>
    public string VesselSearchTabText { get; set; } = "Vessel";

    /// <summary>Label (aria-label / associated label) of the vessel field, used for a role/label lookup.</summary>
    public string SearchInputLabel { get; set; } = "vessel";

    /// <summary>
    /// Resilient selector for the vessel &lt;input type="text"&gt; autocomplete field. Uses a CSS
    /// "ends-with" match on the stable ID suffix (<c>_searchByVesselAutoCompleter__textBox</c>) so it
    /// survives ASP.NET WebForms ClientID prefix churn.
    /// </summary>
    public string VesselInputSelector { get; set; } = "input[id$='_searchByVesselAutoCompleter__textBox']";

    /// <summary>
    /// Selector for the autocomplete suggestion list items. The scraper types the query, waits for a
    /// suggestion item whose text matches the query, and clicks it (rather than blind keyboard keys).
    /// ASP.NET AutoCompleteExtender commonly renders suggestions as <c>&lt;li&gt;</c> or
    /// <c>.ajax__autocomplete_item</c> elements; this comma list covers both, and is overridable if the
    /// page uses a different class. The scraper further filters by visible text = the search query.
    /// </summary>
    public string AutocompleteSuggestionSelector { get; set; } = "li, .ajax__autocomplete_item";

    /// <summary>
    /// Selector for the "Show terminal information" checkbox that must be ticked before submitting the
    /// search. Scoped under the active Vessel tab container (<c>#searchByVesselTab</c>) so it matches
    /// exactly one element and never the duplicate checkbox on the (hidden) Port tab. The underlying
    /// <c>&lt;input&gt;</c> is visually hidden by ASP.NET custom styling, so the scraper checks it with
    /// <c>Force = true</c> (idempotent).
    /// </summary>
    public string TerminalInfoCheckboxSelector { get; set; } = "#searchByVesselTab .showTerminalInfoCheckBox input[type='checkbox']";

    /// <summary>Selector for the submit button. Defaults to the static-ID "Show Schedules" button.</summary>
    public string SearchButton { get; set; } = "#searchByVesselButton";

    /// <summary>Broad selector used to confirm a results table is present on the page.</summary>
    public string ResultsTableHint { get; set; } = "table";

    /// <summary>
    /// Selector for the results element the scraper awaits (and scopes row extraction to). Defaults to
    /// <c>#searchByVesselResultsPanel table</c> — deliberately scoped to the table INSIDE the panel,
    /// <summary>
    /// Selector for the results element the scraper awaits (and scopes extraction to). Defaults to the
    /// static Vessel results panel (<c>#searchByVesselResultsPanel</c>). NOTE: scoping to the panel (not
    /// to <c>… table</c>) is deliberate — the ASP.NET layout nests several layout tables inside the
    /// panel, and <c>… table</c> matches the FIRST (layout, td-less) table, not the data grid. With the
    /// panel as the scope, <see cref="ResultsRowSelector"/> = <c>table tr:has(td)</c> finds the data rows
    /// in whichever inner table actually holds them.
    /// </summary>
    public string ResultsContainerSelector { get; set; } = "#searchByVesselResultsPanel";

    /// <summary>
    /// Selector identifying each data row, scoped under <see cref="ResultsContainerSelector"/>. Defaults
    /// to <c>table tr:has(td)</c>: it searches across ALL tables inside the panel (needed because ASP.NET
    /// wraps the data grid in nested layout tables) and matches only rows containing <c>&lt;td&gt;</c>
    /// cells, skipping <c>&lt;th&gt;</c>-only header rows so column headers never pollute the hash.
    /// </summary>
    public string ResultsRowSelector { get; set; } = "table tr:has(td)";

    /// <summary>
    /// Per-cell selectors applied to each row. Only these columns contribute to the hash, which is what
    /// makes the change detector immune to noise (session IDs, ads, server timestamps, etc.).
    /// </summary>
    [MinLength(1, ErrorMessage = "At least one CellSelector is required.")]
    public string[] CellSelectors { get; set; } = ["td:nth-child(1)"];

    /// <summary>
    /// Text the panel shows when a search returns no results (e.g. "No results found"). The scraper uses
    /// it to distinguish a genuine empty-results state (returns gracefully, no retry) from a still-loading
    /// page (throws so the worker retries). Case-insensitive substring match. Empty disables the check.
    /// </summary>
    public string NoResultsText { get; set; } = "No results";
}

/// <summary>
/// Triggers validation of nested <see cref="SelectorsOptions"/> during options validation.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class ValidateObjectMembersAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context)
    {
        if (value is null) return ValidationResult.Success;
        var results = new List<ValidationResult>();
        var ctx = new ValidationContext(value) { MemberName = context.MemberName };
        return Validator.TryValidateObject(value, ctx, results, validateAllProperties: true)
            ? ValidationResult.Success
            : new ValidationResult(
                string.Join(" | ", results.Select(r => r.ErrorMessage)),
                results.SelectMany(r => r.MemberNames).Distinct());
    }
}
