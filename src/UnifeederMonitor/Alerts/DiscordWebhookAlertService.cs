using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnifeederMonitor.ChangeDetection;
using UnifeederMonitor.Configuration;

namespace UnifeederMonitor.Alerts;

/// <summary>
/// Posts a Discord webhook notification when a schedule change is detected.
///
/// If <see cref="MonitorOptions.DiscordWebhookUrl"/> is empty, this service self-disables and
/// silently no-ops, so the worker can always resolve an <see cref="IAlertService"/> for it.
/// </summary>
public sealed class DiscordWebhookAlertService : IAlertService
{
    private readonly MonitorOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<DiscordWebhookAlertService> _logger;

    public DiscordWebhookAlertService(
        IOptions<MonitorOptions> options,
        HttpClient http,
        ILogger<DiscordWebhookAlertService> logger)
    {
        _options = options.Value;
        _http = http;
        _logger = logger;
    }

    public async Task SendAsync(ChangeResult result, CancellationToken stoppingToken = default)
    {
        var url = _options.DiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            // Webhook disabled by configuration; not an error.
            return;
        }

        var snapshot = result.CurrentSnapshot;

        // Discord embeds cap the description at 4096 chars; keep a compact row preview.
        const int maxDesc = 4000;
        var preview = string.Join(
            '\n',
            snapshot.Rows.Take(8).Select((r, i) => $"{i + 1}. {Truncate(r.ToString(), 120)}"));
        if (snapshot.RowCount > 8)
        {
            preview += $"\n... and {snapshot.RowCount - 8} more row(s).";
        }

        var description =
            $"Detected a change for **{snapshot.SearchQuery}** at {snapshot.CapturedAtUtc:O} (UTC).\n" +
            $"Previous hash: `{result.PreviousHash ?? "(none)"}`\n" +
            $"New hash: `{result.CurrentHash}`\n" +
            $"Rows now: **{snapshot.RowCount}**\n\n{preview}";

        if (description.Length > maxDesc)
        {
            description = description[..maxDesc] + "…";
        }

        var payload = new DiscordWebhookPayload
        {
            Embeds =
            [
                new DiscordEmbed
                {
                    Title = $"Unifeeder schedule changed — {snapshot.SearchQuery}",
                    Description = description,
                    Color = 0xFFD700, // gold
                    Timestamp = snapshot.CapturedAtUtc.ToString("O"),
                }
            ]
        };

        try
        {
            // Use a short timeout so a slow/absent endpoint never blocks the worker loop.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await _http.PostAsJsonAsync(url, payload, cts.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Discord webhook alert sent successfully.");
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                _logger.LogError(
                    "Discord webhook returned HTTP {Status}: {Body}",
                    (int)response.StatusCode, body);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Never let a webhook failure crash the monitor.
            _logger.LogError(ex, "Failed to send Discord webhook alert.");
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");

    // ---- Discord API payload models ----

    private sealed record DiscordWebhookPayload
    {
        [JsonPropertyName("embeds")]
        public List<DiscordEmbed> Embeds { get; init; } = [];
    }

    private sealed record DiscordEmbed
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("color")]
        public int? Color { get; init; }

        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; init; }
    }
}
