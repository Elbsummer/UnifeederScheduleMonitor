using Microsoft.Extensions.Logging;
using UnifeederMonitor.ChangeDetection;

namespace UnifeederMonitor.Alerts;

/// <summary>
/// Fans an alert out to every registered <see cref="IAlertService"/> (console, Discord, ...).
/// Each channel is invoked independently so a failure in one never blocks the others.
/// </summary>
public sealed class CompositeAlertService : IAlertService
{
    private readonly IReadOnlyList<IAlertService> _services;
    private readonly ILogger<CompositeAlertService> _logger;

    public CompositeAlertService(
        IEnumerable<IAlertService> services,
        ILogger<CompositeAlertService> logger)
    {
        // Keep console first so it always reports, regardless of webhook ordering.
        _services = services.ToList();
        _logger = logger;
    }

    public async Task SendAsync(ChangeResult result, CancellationToken stoppingToken = default)
    {
        foreach (var service in _services)
        {
            try
            {
                await service.SendAsync(result, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Each channel is responsible for its own error handling, but guard against
                // any that throw anyway so one bad channel can't break the loop.
                _logger.LogError(ex, "Alert service {Service} threw while sending.", service.GetType().Name);
            }
        }
    }
}
