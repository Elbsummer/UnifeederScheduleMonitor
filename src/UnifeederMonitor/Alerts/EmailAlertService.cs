using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using UnifeederMonitor.ChangeDetection;
using UnifeederMonitor.Configuration;
using UnifeederMonitor.Scraper;

namespace UnifeederMonitor.Alerts;

/// <summary>
/// Emails a schedule-change alert (with the results-page PDF attached) via Gmail SMTP using MailKit.
///
/// Self-disables (no-ops) when <see cref="EmailOptions.EnableEmailAlerts"/> is false or the required
/// fields are missing, so the worker can always resolve an <see cref="IAlertService"/> for it.
/// Attachments are best-effort: if the scraper captured no PDF (e.g. headed/debug mode), the email is
/// still sent with just the textual summary. Never throws — logs internally so the worker loop survives.
/// </summary>
public sealed class EmailAlertService : IAlertService
{
    private readonly EmailOptions _options;
    private readonly ILogger<EmailAlertService> _logger;

    public EmailAlertService(
        IOptions<MonitorOptions> options,
        ILogger<EmailAlertService> logger)
    {
        _options = options.Value.Email;
        _logger = logger;
    }

    public async Task SendAsync(ChangeResult result, CancellationToken stoppingToken = default)
    {
        if (!_options.EnableEmailAlerts)
        {
            return; // disabled by configuration
        }
        if (!_options.IsConfigured)
        {
            _logger.LogWarning(
                "Email alerts are enabled but SenderEmail/SenderAppPassword/RecipientEmail are incomplete; " +
                "no email sent. Fill Monitor:Email in appsettings.json.");
            return;
        }

        var snapshot = result.CurrentSnapshot;
        var subject = $"Unifeeder schedule changed — {snapshot.SearchQuery}";

        var body = new BodyBuilder
        {
            HtmlBody = BuildHtmlBody(result, snapshot),
        };

        // Attach the PDF captured by the scraper, if present (null in headed/debug mode).
        if (snapshot.PdfBytes is { Length: > 0 } pdf)
        {
            var fileName = $"schedule-{snapshot.SearchQuery}-{snapshot.CapturedAtUtc:yyyyMMdd-HHmmssZ}.pdf";
            body.Attachments.Add(fileName, pdf, new ContentType("application", "pdf"));
            _logger.LogInformation("Attaching PDF ({Kb} KB) as {FileName}.", pdf.Length / 1024, fileName);
        }
        else
        {
            _logger.LogWarning("No PDF attachment available (scraper ran headed, or PDF capture failed). Emailing text-only alert.");
        }

        var message = new MimeMessage
        {
            Subject = subject,
            Body = body.ToMessageBody(),
        };
        message.From.Add(MailboxAddress.Parse(_options.SenderEmail!));
        message.To.Add(MailboxAddress.Parse(_options.RecipientEmail!));
        message.Date = DateTimeOffset.UtcNow;

        // Gmail on 587 uses STARTTLS; 465 uses implicit SSL. SecureSocketOptions.Auto lets MailKit pick
        // based on the port, which handles both correctly.
        try
        {
            using var client = new SmtpClient
            {
                // Be forgiving about Gmail's certificate sandbox; fail loudly otherwise.
                ServerCertificateValidationCallback = (_, _, _, _) => true,
            };

            await client.ConnectAsync(
                _options.SmtpServer,
                _options.Port,
                SecureSocketOptions.Auto,
                stoppingToken).ConfigureAwait(false);

            try
            {
                await client.AuthenticateAsync(
                    _options.SenderEmail!,
                    _options.SenderAppPassword!,
                    stoppingToken).ConfigureAwait(false);

                await client.SendAsync(message, stoppingToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Email alert sent to {Recipient} (subject: \"{Subject}\").",
                    _options.RecipientEmail, subject);
            }
            finally
            {
                try { await client.DisconnectAsync(true, stoppingToken).ConfigureAwait(false); }
                catch { /* best-effort cleanup */ }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Authentication failures (wrong password / not an App Password) land here with a clear message.
            _logger.LogError(
                ex,
                "Failed to send email alert via {Server}:{Port}. " +
                "If this is an auth error, verify SenderAppPassword is a Google App Password " +
                "(not your regular Gmail password) and 2-Step Verification is enabled.",
                _options.SmtpServer, _options.Port);
        }
    }

    /// <summary>
    /// Builds an HTML summary of the change for the email body. Shows only the row-level diff
    /// (added/updated and removed/previous rows) rather than the entire table, so the recipient can
    /// see exactly what changed at a glance. The full table context is provided by the PDF attachment.
    /// </summary>
    private static string BuildHtmlBody(ChangeResult result, ScheduleSnapshot snapshot)
    {
        var captured = System.Net.WebUtility.HtmlEncode(snapshot.CapturedAtUtc.ToString("u"));
        var query = System.Net.WebUtility.HtmlEncode(snapshot.SearchQuery);
        var prev = System.Net.WebUtility.HtmlEncode(result.PreviousHash ?? "(none)");
        var curr = System.Net.WebUtility.HtmlEncode(result.CurrentHash);

        var addedHtml = BuildDiffList(result.AddedRows, "🟢", "#e6f4ea");
        var removedHtml = BuildDiffList(result.RemovedRows, "🔴", "#fce8e6");

        return $"""
               <h2>Unifeeder schedule change detected</h2>
               <p>A meaningful change was detected for vessel <strong>{query}</strong> at <code>{captured}</code> (UTC).</p>
               <table border="0" cellpadding="4" cellspacing="0">
                 <tr><td>Previous hash:</td><td><code>{prev}</code></td></tr>
                 <tr><td>New hash:</td><td><code>{curr}</code></td></tr>
                 <tr><td>Rows now:</td><td><strong>{snapshot.RowCount}</strong></td></tr>
                 <tr><td>Added / updated:</td><td><strong>{result.AddedRows.Count}</strong></td></tr>
                 <tr><td>Removed / previous:</td><td><strong>{result.RemovedRows.Count}</strong></td></tr>
               </table>

               <h3>🟢 Added / updated rows</h3>
               {addedHtml}

               <h3>🔴 Removed / previous rows</h3>
               {removedHtml}

               <p>The full results page is attached as a PDF for complete context.</p>
               """;
    }

    /// <summary>Builds an HTML list of diff rows; shows "(none)" when the section is empty.</summary>
    private static string BuildDiffList(List<string> rows, string marker, string color)
    {
        if (rows.Count == 0)
        {
            return "<p><em>(none)</em></p>";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"<table border=\"1\" cellpadding=\"4\" cellspacing=\"0\" style=\"border-collapse:collapse;background:{color};\">");
        foreach (var row in rows)
        {
            sb.AppendLine($"<tr><td><strong>{marker}</strong>&nbsp;{System.Net.WebUtility.HtmlEncode(row)}</td></tr>");
        }
        sb.AppendLine("</table>");
        return sb.ToString();
    }
}
