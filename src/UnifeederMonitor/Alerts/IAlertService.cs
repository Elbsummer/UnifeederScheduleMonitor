using UnifeederMonitor.ChangeDetection;

namespace UnifeederMonitor.Alerts;

/// <summary>
/// Notifies a human/operator that a meaningful schedule change was detected.
/// Implementations (console, Discord, ...) are aggregated by <see cref="CompositeAlertService"/>.
/// </summary>
public interface IAlertService
{
    /// <summary>
    /// Sends an alert describing <paramref name="result"/>.
    /// Implementations must not throw on transient failures; they should log internally so the
    /// worker loop keeps running.
    /// </summary>
    Task SendAsync(ChangeResult result, CancellationToken stoppingToken = default);
}
