# Unifeeder Schedule Change Monitor

A lightweight .NET 8 Worker Service that uses **headless browser automation (Playwright)** to monitor an
ASP.NET Web Forms schedule page for changes to a specific search query (e.g. a vessel name such as
`VESSEL_NAME`), and raises an alert (console + optional Discord webhook) when a meaningful change is detected.

## Why a headless browser?

The target page (`default.aspx`) is an ASP.NET Web Forms application. Plain `HttpClient` GET requests are
not enough because the page relies on `__VIEWSTATE`, server-side form postbacks, and dynamic JavaScript to
render the search results. Playwright drives a real Chromium instance that performs the search, waits for the
results table to render, and then extracts only the meaningful data columns.

## How change detection works

1. Each tick, the page is scraped and the configured columns of the results table are extracted into a snapshot.
2. The snapshot is serialized to **canonical JSON** (rows sorted, timestamps excluded) and hashed with **SHA-256**.
3. The hash is compared against the previously stored one (in `state.json`).
   - **First run:** the baseline hash is stored **silently** (no alert).
   - **Later runs:** if the hashes differ, a schedule change is detected and an alert fires; the new hash becomes the baseline.

Because only the configured data columns contribute to the hash, incidental noise (session IDs, ads, server
timestamps, row reordering) does **not** cause false positives.

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (the project also builds on the .NET 10 SDK).
- `pwsh` (PowerShell 7+) — used by Playwright's browser-install script. On Windows it ships with the .NET SDK
  dependencies; if missing, install [PowerShell](https://github.com/PowerShell/PowerShell).

## 1. Build

```bash
dotnet build src/UnifeederMonitor/UnifeederMonitor.csproj
```

## 2. Install 
The application is configured to use the system-installed Microsoft Edge browser (Channel = "msedge"), making it completely portable with zero setup required and no heavy browser binaries to download.

## 3. Configure

All knobs live in [`appsettings.json`](src/UnifeederMonitor/appsettings.json) under the `Monitor` section:

| Setting | Default | Description |
|---|---|---|
| `TargetUrl` | unifeeder schedule URL | The ASPX page to monitor. |
| `SearchQuery` | `VESSEL_NAME` | The vessel name / search term to look up. |
| `PollingIntervalMinutes` | `30` | How often to re-check the page. |
| `TimeoutSeconds` | `60` | Per-operation Playwright timeout. |
| `MaxRetries` | `3` | Retry attempts per scrape cycle before giving up for that tick. |
| `RetryDelaySeconds` | `15` | Base delay between retries (actual delay = `RetryDelaySeconds * attempt`). |
| `BrowserHeadless` | `true` | Run Chromium headless. Set `false` (with `HeadedDebug`) to watch it. |
| `HeadedDebug` | `false` | Show the browser and write `debug_dom.html` for selector discovery (see below). |
| `BrowserExecutablePath` | *(empty)* | Optional path to a custom Chromium. Empty = use Playwright's build. |
| `Selectors.*` | *(see below)* | Locators used to interact with the page. |
| `StateFilePath` | *(empty)* | Path to the hash state file. Empty → `%LocalAppData%\UnifeederMonitor\state.json`. |
| `DiscordWebhookUrl` | *(empty)* | Discord webhook URL. Empty → webhook alerts disabled (console still runs). |

### `Selectors` reference

| Selector | Default | Purpose |
|---|---|---|
| `VesselSearchTabText` | `vessel` | Text used to find the "search by vessel" tab/link. |
| `SearchInputLabel` | `vessel` | Label of the vessel search input field (fallback for role/label lookup). |
| `SearchInputSelector` | `input[id$='txtVesselName']` | Resilient CSS selector for the vessel input (attribute-ends-with on the stable ID suffix). **Primary** input locator. |
| `SearchButton` | `#searchByVesselButton` | Selector for the "Show Schedules" submit button (static ID). |
| `ResultsTableHint` | `table` | Broad selector confirming a results table is present. |
| `ResultsContainerSelector` | `div[id$='_hostedControl_scheduleArea']` | The results container wrapping the schedule table. Uses a CSS "ends-with" match that survives the dynamic ASP.NET ClientID prefix. **Awaited before extraction; row extraction is scoped to it.** |
| `ResultsRowSelector` | `table tbody tr` | Selector identifying each data row (scoped under `ResultsContainerSelector`). |
| `CellSelectors` | 5 columns | **The columns that contribute to the hash.** Only these matter for change detection. |

The target is an ASP.NET Web Forms page, so most IDs carry a dynamic prefix (e.g.
`_menuPanel_tabControl_ctl..._`). The scraper therefore prefers **partial-match CSS selectors** (`[id$='...']`)
and role/label locators (`GetByRole`, `GetByLabel`, `GetByText`) over full IDs, and falls back through a
chain of candidates — so a page redesign can usually be fixed by editing `appsettings.json`
**without a rebuild**.

## 4. Run

```bash
cd src/UnifeederMonitor
dotnet run
```

The first run will:
- navigate, search for the configured vessel, wait for the results table,
- extract the configured columns,
- write the baseline `state.json` **without** firing an alert.

Every subsequent run compares the new hash to the stored one and alerts on a difference.

### Setting the Discord webhook

Set `Monitor:DiscordWebhookUrl` in `appsettings.json` (or via environment variable
`Monitor__DiscordWebhookUrl`). The alert uses a Discord **embed** payload. When the field is empty the
webhook channel self-disables and only console logging runs.

### Running as a Windows service (optional)

Add the `Microsoft.Extensions.Hosting.WindowsServices` package, then register with `sc.exe`. The Worker
host already supports `BackgroundService` lifetime semantics for service hosting.

---

## Selector discovery (do this once)

Because ASP.NET Web Forms generates unstable element IDs and the live DOM can't always be predicted,
there's a built-in discovery mode:

1. In `appsettings.json`, set `Monitor:HeadedDebug` to `true` (and `BrowserHeadless` to `false` to watch).
2. Run the app once. It will perform the full search flow, then write the **complete results-page DOM** to
   `debug_dom.html` (next to the executable).
3. Open `debug_dom.html` in a browser and use devtools to find the real IDs/classes of:
   - the vessel search input → put its label text in `Selectors:SearchInputLabel`,
   - the submit button → put a selector in `Selectors:SearchButton`,
   - the results table rows → `Selectors:ResultsRowSelector`,
   - the meaningful columns (Voyage / Port / ETA / ETD / …) → `Selectors:CellSelectors` (e.g.
     `td:nth-child(N)` for each column you care about).
4. Set `HeadedDebug` back to `false` and restart. Only the columns you selected now drive the hash.

---

## Verifying it works

A quick sanity-check flow:

1. `dotnet build` succeeds.
2. `pwsh .../playwright.ps1 install chromium` installs the browser.
3. First `dotnet run` creates `state.json` under `%LocalAppData%\UnifeederMonitor\` and logs "Baseline
   stored" with **no** alert.
4. Manually edit the `hash` value in `state.json` to something else → the next tick detects the mismatch
   and fires the console alert (and the Discord webhook, if configured).
5. Stop the network mid-scrape → confirm the retry loop runs and the service keeps going without crashing.

## Troubleshooting

- **"Could not find a visible search input…"** — the page layout changed. Run selector discovery (above)
  and update `Selectors:SearchInputLabel` / `SearchButton`.
- **No rows extracted / "no row matched"** — verify `ResultsRowSelector` and `CellSelectors` against
  `debug_dom.html`. Also confirm the query actually returns results on the site.
- **Timeouts** — raise `TimeoutSeconds`; the site can be slow under postback. The retry loop will already
  absorb transient failures.
- **Cloudflare / bot detection** — if the site challenges automated browsers, try running non-headless
  (`BrowserHeadless=false`) or supplying a real Chromium via `BrowserExecutablePath`. Playwright's
  stealth is limited; for hard challenges a commercial scraping service may be required.

## Project structure

```
src/UnifeederMonitor/
├── Program.cs                          Host + DI composition root
├── Worker.cs                           BackgroundService: PeriodicTimer + retry loop
├── appsettings.json                    All configuration
├── Configuration/MonitorOptions.cs     Strongly-typed, validated options
├── Scraper/
│   ├── IScheduleScraper.cs
│   ├── PlaywrightScheduleScraper.cs    Browser lifecycle + interaction + extraction
│   └── ScheduleRow.cs                  Snapshot data model
├── ChangeDetection/
│   ├── IStateStore.cs                  ChangeResult + store interface
│   ├── FileStateStore.cs               Hash + snapshot persistence
│   └── ChangeDetector.cs               Canonical JSON → SHA-256
└── Alerts/
    ├── IAlertService.cs
    ├── ConsoleAlertService.cs          Always-on logger alert
    ├── DiscordWebhookAlertService.cs   Optional webhook alert
    └── CompositeAlertService.cs        Fans out to all channels
```
