using System.Text;
using Microsoft.Extensions.Logging;
using UnifeederMonitor.ChangeDetection;

namespace UnifeederMonitor.Alerts;

/// <summary>
/// Always-on alert that writes a readable summary of the change to the logger/console.
/// This is the baseline channel; it runs regardless of whether a webhook URL is configured.
/// </summary>
public sealed class ConsoleAlertService : IAlertService
{
    private readonly ILogger<ConsoleAlertService> _logger;

    public ConsoleAlertService(ILogger<ConsoleAlertService> logger) => _logger = logger;

    public Task SendAsync(ChangeResult result, CancellationToken stoppingToken = default)
    {
        var snapshot = result.CurrentSnapshot;
        var message = new StringBuilder()
            .AppendLine("========================================")
            .AppendLine("  SCHEDULE CHANGE DETECTED")
            .AppendLine("========================================")
            .AppendLine($"Query     : {snapshot.SearchQuery}")
            .AppendLine($"Captured  : {snapshot.CapturedAtUtc:O} (UTC)")
            .AppendLine($"Previous  : {result.PreviousHash ?? "(none)"}")
            .AppendLine($"Current   : {result.CurrentHash}")
            .AppendLine($"Rows now  : {snapshot.RowCount}")
            .AppendLine($"Added/changed : {result.AddedRows.Count}")
            .AppendLine($"Removed/prev  : {result.RemovedRows.Count}")
            .AppendLine("----------------------------------------")
            .AppendLine("  + ADDED / UPDATED ROWS");

        if (result.AddedRows.Count == 0)
        {
            message.AppendLine("    (none)");
        }
        else
        {
            foreach (var row in result.AddedRows)
            {
                message.AppendLine($"    + {row}");
            }
        }

        message.AppendLine("----------------------------------------")
            .AppendLine("  - REMOVED / PREVIOUS ROWS");

        if (result.RemovedRows.Count == 0)
        {
            message.AppendLine("    (none)");
        }
        else
        {
            foreach (var row in result.RemovedRows)
            {
                message.AppendLine($"    - {row}");
            }
        }
        message.Append("========================================");

        _logger.LogWarning("{Alert}", message.ToString());
        return Task.CompletedTask;
    }
}
