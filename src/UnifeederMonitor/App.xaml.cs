using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using UnifeederMonitor.Alerts;
using UnifeederMonitor.ChangeDetection;
using UnifeederMonitor.Configuration;
using UnifeederMonitor.Scraper;

namespace UnifeederMonitor;

/// <summary>
/// WPF tray-only application. There is no main window — the app lives entirely in the taskbar
/// notification area. The tray icon's context menu exposes Start / Stop / Exit to control the
/// background <see cref="Worker"/> scrape loop, and the DI host (scraper, change detector, alerters)
/// is built here in <see cref="OnStartup"/>.
/// </summary>
public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private MenuItem? _startItem;
    private MenuItem? _stopItem;
    private IHost? _host;
    private Worker? _worker;

    /// <summary>
    /// The global Serilog logger. Created in <see cref="BuildHost"/> before anything logs, disposed on
    /// exit. Routes all <c>ILogger</c> output (the worker, scraper, alerters) to a rolling daily file
    /// under <c>Logs/monitor_.txt</c> next to the .exe — replacing the console output that vanished
    /// when this became a windowless WinExe.
    /// </summary>
    private static Serilog.Core.Logger? _serilogLogger;

    public App()
    {
        // Subscribe early — before any startup logic can throw — so a silent crash (no console to print
        // to in a WinExe app) instead surfaces a visible MessageBox with the full exception.
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += App_DomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    /// <summary>ViewModel-style state to keep the menu items' Enabled flags consistent.</summary>
    private bool IsMonitorRunning
    {
        get => _worker?.IsRunning ?? false;
        set
        {
            if (_worker is not null) _worker.IsRunning = value;
            UpdateMenuEnabledState();
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Wrap ALL startup logic (host build, StartAsync, resource lookup) in try/catch so any DI or
        // init failure — which would otherwise vanish silently in a windowless WinExe — is shown to the
        // user via a MessageBox instead of an instant silent exit.
        try
        {
            // Build the DI host exactly as the old Program.cs did, but DON'T call RunAsync — we drive the
            // worker ourselves via the tray menu, and we want a non-blocking startup.
            _host = BuildHost(e.Args);
            await _host.StartAsync();

            _worker = _host.Services.GetRequiredService<Worker>();
            // Default: running, so the first scrape fires shortly after the icon appears.
            IsMonitorRunning = true;

            // Create the tray icon from XAML. The IconSource (pack URI to the embedded DP World .ico)
            // supplies the image — no runtime system-icon override needed.
            _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
            _trayIcon.ForceCreate(); // ensure the icon is actually placed in the tray now
            // Cache the menu item references once so the toggle is reliable (looking them up by name on
            // every click was fragile — x:Name in a ContextMenu isn't always in the expected namescope).
            ResolveMenuItems();
            // Sync the menu's Start/Stop enabled state with the running worker now that the menu exists.
            UpdateMenuEnabledState();

            ShowBalloon("Unifeeder Monitor", "Running in the background. Right-click the tray icon to control.");
        }
        catch (Exception ex)
        {
            ShowFatalError(ex, "Fatal Startup Error");
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Cleanly tear down the host (cancels the worker, disposes the Playwright browser, flushes state).
        if (_host is not null)
        {
            try { await _host.StopAsync(TimeSpan.FromSeconds(15)); }
            catch { /* best-effort shutdown */ }
            _host.Dispose();
        }
        _trayIcon?.Dispose();
        // Flush + close the Serilog file sink last so shutdown logs are captured.
        _serilogLogger?.Dispose();
        base.OnExit(e);
    }

    // --- Context menu handlers ---

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (IsMonitorRunning) return;
        IsMonitorRunning = true;
        _worker?.TriggerImmediateTick();
        ShowBalloon("Unifeeder Monitor", "Monitoring resumed.");
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!IsMonitorRunning) return;
        IsMonitorRunning = false;
        ShowBalloon("Unifeeder Monitor", "Monitoring paused.");
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Shutdown(); // fires OnExit -> host.StopAsync -> clean teardown
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // Read the CURRENT bound options (so the dialog opens with today's values, not stale defaults).
        var opts = _host?.Services.GetRequiredService<IOptions<MonitorOptions>>().Value;
        if (opts is null)
        {
            MessageBox.Show("Could not load current settings.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var scraper = _host!.Services.GetRequiredService<IScheduleScraper>();
        var dialog = new SettingsWindow(
            scraper,
            opts.SearchQuery,
            opts.Email.SenderEmail ?? string.Empty,
            opts.Email.SenderAppPassword ?? string.Empty,
            opts.Email.RecipientEmail ?? string.Empty);

        // ShowDialog() blocks the UI thread until the user closes the dialog; meanwhile the worker keeps
        // running in the background. We DON'T pause it during the dialog — only after a successful save
        // (rebuilding the host requires it stopped).
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return; // user cancelled
        }

        var edit = dialog.Result;
        try
        {
            // 1. Persist the four fields to appsettings.json (writes clean, formatted JSON — JSON comments
            //    don't survive a JsonObject round-trip, which is acceptable: the values are what matter).
            PersistSettings(edit);

            // 2. If the vessel changed, the stored hash/state.json is for a different vessel and would
            //    produce a massive false-positive "Schedule Changed" alert. Delete it so the next run
            //    establishes a fresh baseline silently.
            if (edit.VesselChanged)
            {
                ResetStateFile(opts);
            }

            // 3. Rebuild + restart the host so the new options are re-read from disk and rebound. The
            //    Playwright browser is disposed/re-created (the safe path — options like BrowserHeadless
            //    could have changed). The worker resumes running and fires an immediate first tick.
            RestartHost();

            ShowBalloon("Unifeeder Monitor",
                edit.VesselChanged
                    ? $"Settings saved. Now monitoring \"{edit.Vessel}\" (baseline reset)."
                    : "Settings saved and applied.");
        }
        catch (Exception ex)
        {
            ShowFatalError(ex, "Failed to save settings");
        }
    }

    /// <summary>
    /// Writes the four user-editable values into appsettings.json, preserving all other keys. Reads the
    /// file as a <see cref="JsonObject"/> (strict JSON — comments will be dropped on write), updates the
    /// Monitor / Email nodes, and writes it back formatted.
    /// </summary>
    private static void PersistSettings(SettingsEdit edit)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var json = File.ReadAllText(path);
        // The user's appsettings.json contains C-style // comments (perfectly legal for IConfiguration,
        // which is lenient). JsonNode.Parse is STRICT by default and throws JsonReaderException on the
        // first '/'. Skip comments so the round-trip read works regardless of whether the file has them.
        var root = JsonNode.Parse(
                json,
                nodeOptions: new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
            ?? throw new InvalidOperationException("appsettings.json is empty or invalid.");

        var monitor = root["Monitor"]
            ?? throw new InvalidOperationException("appsettings.json is missing the 'Monitor' section.");
        monitor["SearchQuery"] = edit.Vessel;

        var email = monitor["Email"] ?? (monitor["Email"] = new JsonObject());
        email["SenderEmail"] = edit.SenderEmail;
        email["SenderAppPassword"] = edit.SenderPassword;
        email["RecipientEmail"] = edit.RecipientEmail;
        // If the user filled in real email values, make sure email alerts are enabled so they take effect.
        if (!string.IsNullOrWhiteSpace(edit.SenderEmail)
            && !string.IsNullOrWhiteSpace(edit.SenderPassword)
            && !string.IsNullOrWhiteSpace(edit.RecipientEmail))
        {
            email["EnableEmailAlerts"] = true;
        }

        // Atomic write via temp + replace (same pattern as FileStateStore) so a crash can't corrupt the
        // file the app needs to start.
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Copy(tempPath, path, overwrite: true);
        try { File.Delete(tempPath); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Deletes the state.json (or its configured path) so the next scrape treats the run as a first-run
    /// baseline instead of diffing against the previous vessel's rows.
    /// </summary>
    private static void ResetStateFile(MonitorOptions opts)
    {
        var path = opts.ResolveStateFilePath();
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort: a locked state file shouldn't block the settings save. The next run will
            // simply re-diff against the old baseline (a one-time false positive, recoverable).
        }
    }

    /// <summary>
    /// Stops + disposes the current host, then builds + starts a fresh one so the saved settings are
    /// re-read and rebound. Re-resolves the worker and resumes it, firing an immediate tick so the user
    /// sees the new config take effect right away.
    /// </summary>
    private async void RestartHost()
    {
        // Stop the worker first (cooperative pause) so no scrape races the host teardown.
        IsMonitorRunning = false;

        if (_host is not null)
        {
            try { await _host.StopAsync(TimeSpan.FromSeconds(15)); }
            catch { /* best-effort */ }
            _host.Dispose();
            _host = null;
            _worker = null;
        }

        // Build + start a fresh host. This re-reads appsettings.json and rebinds MonitorOptions, and
        // re-creates the (disposed) Playwright browser.
        _host = BuildHost(Environment.GetCommandLineArgs());
        await _host.StartAsync();

        _worker = _host.Services.GetRequiredService<Worker>();
        IsMonitorRunning = true;            // resume + refresh menu state
        _worker.TriggerImmediateTick();     // fire a scrape now so the new settings are exercised
    }

    // --- Helpers ---

    /// <summary>
    /// Resolves the Start/Stop <see cref="MenuItem"/> references from the tray icon's ContextMenu. Called
    /// once after the icon is created; subsequent toggles use the cached references for reliability.
    /// </summary>
    private void ResolveMenuItems()
    {
        if (_trayIcon?.ContextMenu?.Items is not { } items) return;
        foreach (var item in items.OfType<MenuItem>())
        {
            if (item.Name == "StartItem") _startItem = item;
            else if (item.Name == "StopItem") _stopItem = item;
        }
    }

    private void UpdateMenuEnabledState()
    {
        // Toggle on the UI thread (menu items are Dispatcher-owned). When running: Start disabled,
        // Stop enabled. When paused: the reverse.
        var running = IsMonitorRunning;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_startItem is not null) _startItem.IsEnabled = !running;
            if (_stopItem is not null) _stopItem.IsEnabled = running;
        }));
    }

    /// <summary>
    /// Shows a Windows toast/balloon via H.NotifyIcon's ShowNotification API (the 2.x fork has no
    /// ShowBalloonTip). Best-effort — never fail the app over a notification.
    /// </summary>
    private void ShowBalloon(string title, string message)
    {
        if (_trayIcon is null) return;
        try
        {
            _trayIcon.ShowNotification(
                title,
                message,
                NotificationIcon.Info);
        }
        catch
        {
            // Notifications are best-effort; never fail the app over them.
        }
    }

    // --- Global exception handling ---
    // A windowless WinExe has no console to print to, so any unhandled exception vanishes silently and
    // the process just exits. These handlers surface the full exception in a MessageBox instead, so the
    // operator can diagnose misconfig (bad appsettings.json, missing Playwright browser, auth failure, …).

    /// <summary>UI-thread exceptions (e.g. thrown from a menu click handler).</summary>
    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalError(e.Exception, "Unhandled UI Thread Error");
        e.Handled = true; // prevent the process from being torn down; keep running if possible
    }

    /// <summary>Exceptions on non-UI threads that bubble up to the AppDomain (fatal by definition).</summary>
    private void App_DomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        ShowFatalError(ex, "Fatal Error (non-UI thread)");
        // Cannot stop the CLR from terminating here; Shutdown ensures cleanup runs.
        Shutdown(1);
    }

    /// <summary>Faulted/observed tasks whose exception was never awaited (e.g. fire-and-forget alerts).</summary>
    private void TaskScheduler_UnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        ShowFatalError(e.Exception, "Unobserved Task Error");
        e.SetObserved(); // mark handled so the process isn't terminated
    }

    /// <summary>
    /// Shows the exception in a modal MessageBox on the dispatcher thread. Safe to call from any thread
    /// — marshals to the UI thread if needed so the dialog can actually render.
    /// </summary>
    private void ShowFatalError(Exception? ex, string title)
    {
        var message = ex?.ToString() ?? "(no exception object provided)";
        try
        {
            if (Dispatcher.CheckAccess())
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                Dispatcher.Invoke(() => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }
        catch
        {
            // If even the MessageBox can't be shown (e.g. during process teardown), fall back to a
            // crash log file next to the executable so the error is never truly lost.
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"{DateTime.UtcNow:O} {title}\n{message}");
            }
            catch { /* nothing more we can do */ }
        }
    }

    /// <summary>Builds the generic host with the same DI registrations the old Program.cs used.</summary>
    private static IHost BuildHost(string[] args)
    {
        // Configure Serilog FIRST, before anything logs. Rolling daily file under Logs/ (created next
        // to the .exe via AppContext.BaseDirectory), plus a console sink for when running under a
        // debugger. This replaces the console output that disappeared with the WinExe switch.
        var logsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "Logs");
        System.IO.Directory.CreateDirectory(logsDir);
        _serilogLogger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            // Quieten the noisy Microsoft framework noise so the log is dominated by our own messages.
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                path: System.IO.Path.Combine(logsDir, "monitor_.txt"),
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder(args);

        // Route all Microsoft.Extensions.Logging output through Serilog (so the worker/scraper/alerters
        // land in the rolling file). dispose:false because we dispose _serilogLogger ourselves on exit.
        builder.Logging.AddSerilog(_serilogLogger, dispose: false);

        // CRITICAL: the default host builder loads appsettings.json relative to the CURRENT WORKING
        // DIRECTORY, which is unpredictable for a WPF tray app (cmd launch vs. double-click vs. Task
        // Scheduler all differ). Re-root config loading to AppContext.BaseDirectory (next to the .exe)
        // so the file is always found. optional:false makes a genuinely missing file throw clearly
        // instead of silently binding to null (the OptionsValidationException we just saw).
        //
        // HostApplicationBuilder exposes its config as a ConfigurationManager, so we configure sources
        // directly on builder.Configuration (there is no ConfigureAppConfiguration callback here).
        builder.Configuration.SetBasePath(AppContext.BaseDirectory);
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        // Optional per-environment file, matching the convention the Worker SDK used to provide.
        var env = builder.Environment.EnvironmentName;
        builder.Configuration.AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: true);

        builder.Services
            .AddOptions<MonitorOptions>()
            .Bind(builder.Configuration.GetSection("Monitor"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<IScheduleScraper, PlaywrightScheduleScraper>();
        builder.Services.AddSingleton<ChangeDetector>();
        builder.Services.AddSingleton<IStateStore, FileStateStore>();

        builder.Services.AddSingleton<ConsoleAlertService>();
        builder.Services.AddHttpClient<DiscordWebhookAlertService>();
        builder.Services.AddSingleton<EmailAlertService>();
        builder.Services.AddSingleton<IAlertService>(sp =>
        {
            var services = new List<IAlertService>
            {
                sp.GetRequiredService<ConsoleAlertService>(),
                sp.GetRequiredService<DiscordWebhookAlertService>(),
                sp.GetRequiredService<EmailAlertService>(),
            };
            return new CompositeAlertService(services, sp.GetRequiredService<ILogger<CompositeAlertService>>());
        });

        // Register the worker both as a hosted service AND as a resolvable singleton so App.xaml.cs can
        // reach the same instance to drive IsRunning / TriggerImmediateTick from the tray menu.
        builder.Services.AddSingleton<Worker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());

        return builder.Build();
    }
}
