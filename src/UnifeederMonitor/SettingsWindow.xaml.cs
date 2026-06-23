using System.Windows;

namespace UnifeederMonitor;

/// <summary>
/// Modal dialog for editing the four user-tunable settings (vessel name + the three email fields).
/// Pre-fills from the current configuration and exposes the edited values via <see cref="Result"/>.
/// Does not itself write to disk — the caller (<see cref="App"/>) owns persistence + state reset.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// The edited values, or null if the user cancelled. Populated on Save click.
    /// </summary>
    public SettingsEdit? Result { get; private set; }

    /// <summary>
    /// The vessel name shown when the dialog opened. Used by the caller to detect whether the vessel
    /// changed (which requires a state.json reset).
    /// </summary>
    private readonly string _originalVessel;

    public SettingsWindow(string vessel, string senderEmail, string senderPassword, string recipientEmail)
    {
        InitializeComponent();

        VesselBox.Text = vessel;
        SenderEmailBox.Text = senderEmail;
        SenderPasswordBox.Password = senderPassword;
        RecipientEmailBox.Text = recipientEmail;
        _originalVessel = vessel ?? string.Empty;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var vessel = VesselBox.Text?.Trim() ?? string.Empty;
        var senderEmail = SenderEmailBox.Text?.Trim() ?? string.Empty;
        var senderPassword = SenderPasswordBox.Password?.Trim() ?? string.Empty;
        var recipientEmail = RecipientEmailBox.Text?.Trim() ?? string.Empty;

        // Basic validation. Empty vessel is fatal (nothing to search for); empty email fields just
        // disable that channel (the EmailAlertService self-disables on incomplete config), but we warn
        // so the user doesn't silently lose alerts.
        if (string.IsNullOrWhiteSpace(vessel))
        {
            MessageBox.Show(this, "Vessel name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            VesselBox.Focus();
            return;
        }

        Result = new SettingsEdit
        {
            Vessel = vessel,
            SenderEmail = senderEmail,
            SenderPassword = senderPassword,
            RecipientEmail = recipientEmail,
            VesselChanged = !string.Equals(vessel, _originalVessel, StringComparison.OrdinalIgnoreCase),
        };

        DialogResult = true;
        Close();
    }
}

/// <summary>The edited settings values returned from <see cref="SettingsWindow"/>.</summary>
public sealed class SettingsEdit
{
    public string Vessel { get; init; } = string.Empty;
    public string SenderEmail { get; init; } = string.Empty;
    public string SenderPassword { get; init; } = string.Empty;
    public string RecipientEmail { get; init; } = string.Empty;

    /// <summary>True if the vessel name differs from the one loaded into the dialog.</summary>
    public bool VesselChanged { get; init; }
}
