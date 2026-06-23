using System.ComponentModel.DataAnnotations;

namespace UnifeederMonitor.Configuration;

/// <summary>
/// SMTP email configuration for sending PDF snapshots of schedule changes via Gmail.
/// Bound from the "Monitor:Email" section of appsettings.json.
/// </summary>
public sealed class EmailOptions
{
    /// <summary>SMTP server hostname. Defaults to Gmail's server.</summary>
    public string SmtpServer { get; set; } = "smtp.gmail.com";

    /// <summary>SMTP port. 587 = STARTTLS (recommended for Gmail), 465 = implicit SSL.</summary>
    [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535.")]
    public int Port { get; set; } = 587;

    /// <summary>The Gmail address that sends the email (e.g. "you@gmail.com").</summary>
    [EmailAddress(ErrorMessage = "SenderEmail must be a valid email address.")]
    public string? SenderEmail { get; set; }

    /// <summary>
    /// Google App Password for the sender account (16 chars, no spaces).
    /// IMPORTANT: use a Google App Password, NOT your regular Gmail password. Regular passwords
    /// will be rejected by Gmail. Create one at https://myaccount.google.com/apppasswords
    /// (requires 2-Step Verification to be enabled on the account).
    /// </summary>
    public string? SenderAppPassword { get; set; }

    /// <summary>The email address that receives the alert + PDF attachment.</summary>
    [EmailAddress(ErrorMessage = "RecipientEmail must be a valid email address.")]
    public string? RecipientEmail { get; set; }

    /// <summary>Master switch. When false, email alerts are skipped entirely (console/Discord still run).</summary>
    public bool EnableEmailAlerts { get; set; } = false;

    /// <summary>True when all fields required to send email are present and non-empty.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SenderEmail) &&
        !string.IsNullOrWhiteSpace(SenderAppPassword) &&
        !string.IsNullOrWhiteSpace(RecipientEmail);
}
