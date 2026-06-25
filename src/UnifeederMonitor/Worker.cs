using Microsoft.Extensions.Options;
using UnifeederMonitor.Alerts;
using UnifeederMonitor.ChangeDetection;
using UnifeederMonitor.Configuration;
using UnifeederMonitor.Scraper;

namespace UnifeederMonitor;

/// <summary>
/// Background service that periodically scrapes the schedule page, detects changes, and alerts.
///
/// Each tick is wrapped in a retry loop (configurable attempts/back-off) so a transient network
/// hiccup or a one-off page timeout never crashes the host. Any exception that escapes the retry
/// loop is logged and swallowed; the next tick runs on schedule regardless.
///
/// The loop's pause/resume state (<see cref="IsRunning"/>) is driven by the tray app's context menu,
/// and <see cref="TriggerImmediateTick"/> lets the "Start" menu item fire a scrape right away instead
/// of waiting for the next interval.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly IScheduleScraper _scraper;
    private readonly ChangeDetector _changeDetector;
    private readonly IStateStore _stateStore;
    private readonly IAlertService _alertService;
    private readonly MonitorOptions _options;
    private readonly ILogger<Worker> _logger;

    /// <summary>Once-flag: 1 once the "unconfigured" warning has been logged, so an idling monitor
    /// doesn't spam the log every tick. Reset to 0 when a vessel becomes configured.</summary>
    private int _unconfiguredLogged;

    /// <summary>
    /// Volatile flag the tray app toggles. When false, the loop sleeps (cheaply, cooperatively) and
    /// skips scrape cycles until it's set true again. Marked volatile because it's written from the UI
    /// thread and read from the background loop.
    /// </summary>
    private volatile bool _internalPauseRequested;

    /// <summary>
    /// True while the monitor is allowed to scrape. Exposed so the tray menu can read/flip it.
    /// Setting it to true (after a pause) triggers an immediate tick so the user sees action quickly.
    /// </summary>
    public bool IsRunning
    {
        get => !_internalPauseRequested;
        set => _internalPauseRequested = !value;
    }

    /// <summary>
    /// Set by <see cref="TriggerImmediateTick"/> to break the PeriodicTimer wait early. When observed,
    /// the loop runs a cycle immediately and resets the flag so it keeps cadence afterward.
    /// </summary>
    private volatile bool _immediateTickRequested;

    /// <summary>
    /// Requests an out-of-band scrape cycle now. Used by the tray "Start Monitor" command so the user
    /// doesn't have to wait for the next polling interval after resuming.
    /// </summary>
    public void TriggerImmediateTick() => _immediateTickRequested = true;

    /// <summary>
    /// The single outstanding <see cref="PeriodicTimer.WaitForNextTickAsync"/> task. PeriodicTimer
    /// forbids overlapping calls, so we create ONE wait and reuse it across loop iterations. It is only
    /// replaced after it completes (a real timer tick); an immediate-tick request returns without
    /// disturbing it, so the next iteration keeps awaiting the same pending task.
    /// </summary>
    private Task<bool>? _pendingTimerTask;

    public Worker(
        IScheduleScraper scraper,
        ChangeDetector changeDetector,
        IStateStore stateStore,
        IAlertService alertService,
        IOptions<MonitorOptions> options,
        ILogger<Worker> logger)
    {
        _scraper = scraper;
        _changeDetector = changeDetector;
        _stateStore = stateStore;
        _alertService = alertService;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Unifeeder schedule monitor starting. Query=\"{Query}\", interval={Interval}m, retries={Retries}.",
            _options.SearchQuery, _options.PollingIntervalMinutes, _options.MaxRetries);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.PollingIntervalMinutes));

        // Run the first check immediately, then on the configured cadence thereafter.
        do
        {
            await RunOneCycleIfRunningAsync(stoppingToken).ConfigureAwait(false);
        } while (await WaitForNextTickOrTriggerAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Runs a scrape cycle only if <see cref="IsRunning"/> is true; otherwise logs once and waits. This
    /// is how the tray "Stop Monitor" command pauses scraping without stopping the host. Also idles
    /// (without scraping) when the app is unconfigured (empty SearchQuery) so the tray UI still loads
    /// and the user can open Settings to enter a vessel.
    /// </summary>
    private async Task RunOneCycleIfRunningAsync(CancellationToken stoppingToken)
    {
        if (!IsRunning)
        {
            _logger.LogInformation("Monitor is paused; skipping this tick.");
            return;
        }

        // Idle guard: if no vessel is configured, do NOT scrape (there's nothing to search for). Log it
        // once (not every tick) so the file isn't spammed, then keep idling until the user configures a
        // vessel via the tray Settings dialog.
        if (string.IsNullOrWhiteSpace(_options.SearchQuery))
        {
            if (Interlocked.Exchange(ref _unconfiguredLogged, 1) == 0)
            {
                _logger.LogWarning(
                    "SearchQuery is empty. The monitor is unconfigured and will idle until a vessel name " +
                    "is entered in Settings (tray icon → ⚙️ Settings...).");
            }
            return;
        }
        // Reset the once-flag so a future blank config logs again.
        _unconfiguredLogged = 0;

        await RunOneCycleAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for either the PeriodicTimer's next tick OR an immediate-tick request from the tray menu,
    /// whichever comes first. The immediate-tick path lets "Start Monitor" fire a scrape right away.
    /// </summary>
    private async Task<bool> WaitForNextTickOrTriggerAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        // PeriodicTimer forbids overlapping WaitForNextTickAsync calls. Reuse a SINGLE pending wait that
        // persists across calls: only create a new one when none is outstanding (i.e. after a prior real
        // tick completed it). An immediate-tick request returns WITHOUT touching this task, so the pending
        // wait survives and is awaited again next time — never two concurrent waits (the prior crash).
        _pendingTimerTask ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();

        while (true)
        {
            stoppingToken.ThrowIfCancellationRequested();

            if (_immediateTickRequested)
            {
                _immediateTickRequested = false; // consume the request
                return true;                     // _pendingTimerTask intentionally left outstanding
            }

            // Cooperative wait: poll the timer + the trigger on a short cadence so a pause toggle or an
            // immediate-tick request is noticed within ~1s instead of blocking until the full interval.
            var winner = await Task.WhenAny(
                _pendingTimerTask,
                Task.Delay(TimeSpan.FromSeconds(1), stoppingToken)).ConfigureAwait(false);

            if (winner.IsCanceled) throw new OperationCanceledException(stoppingToken);
            if (ReferenceEquals(winner, _pendingTimerTask))
            {
                // Real tick consumed: clear it so a fresh wait is created on the next call.
                var result = await _pendingTimerTask.ConfigureAwait(false);
                _pendingTimerTask = null;
                return result;
            }
            // else: the 1s delay elapsed — loop and re-check the immediate-tick / pause flags.
        }
    }

    /// <summary>A single monitoring cycle: scrape (with retries) -> compare -> optionally alert.</summary>
    private async Task RunOneCycleAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting scrape cycle at {Time:O}.", DateTimeOffset.Now);

        ScheduleSnapshot? snapshot;
        try
        {
            snapshot = await ScrapeWithRetriesAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down; don't treat as an error.
            throw;
        }
        catch (Exception ex)
        {
            // Last line of defense: never let a tick take down the service.
            _logger.LogError(ex, "Scrape cycle failed after all retries; will retry next tick.");
            return;
        }

        try
        {
            var result = await _changeDetector.CompareAsync(snapshot, stoppingToken).ConfigureAwait(false);

            // First run stores the baseline silently; only genuine changes raise an alert.
            if (result.Changed)
            {
                _logger.LogWarning(
                    "Change detected for \"{Query}\". Firing alerts.", snapshot.SearchQuery);
                await _alertService.SendAsync(result, stoppingToken).ConfigureAwait(false);

                // Persist the new baseline so the alert doesn't repeat on every subsequent tick.
                await _stateStore.SaveAsync(result.CurrentHash, snapshot, stoppingToken).ConfigureAwait(false);
            }
            else if (result.IsBaseline)
            {
                _logger.LogInformation(
                    "Baseline stored for \"{Query}\". No alert on first run.", snapshot.SearchQuery);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change detection/alerting failed for this tick; will retry next tick.");
        }
    }

    /// <summary>
    /// Runs the scrape with a simple linear back-off retry policy. The delay grows with the attempt
    /// number (<c>RetryDelaySeconds * attempt</c>) to give a recovering server room to breathe.
    /// </summary>
    private async Task<ScheduleSnapshot> ScrapeWithRetriesAsync(CancellationToken stoppingToken)
    {
        var maxRetries = Math.Max(0, _options.MaxRetries);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxRetries + 1; attempt++)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                return await _scraper.ScrapeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt > maxRetries)
                {
                    _logger.LogError(
                        ex, "Scrape attempt {Attempt}/{Total} failed; out of retries.",
                        attempt, maxRetries + 1);
                    break;
                }

                var delay = TimeSpan.FromSeconds(_options.RetryDelaySeconds * attempt);
                _logger.LogWarning(
                    ex, "Scrape attempt {Attempt}/{Total} failed; retrying in {Delay}s.",
                    attempt, maxRetries + 1, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }

        throw lastError ?? new InvalidOperationException("Scrape failed for an unknown reason.");
    }
}
