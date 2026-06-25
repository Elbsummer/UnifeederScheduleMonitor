using System.Windows;
using System.Windows.Controls;
using UnifeederMonitor.Scraper;

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

    /// <summary>DI scraper used to fetch the live vessel list from the target page.</summary>
    private readonly IScheduleScraper _scraper;

    public SettingsWindow(
        IScheduleScraper scraper,
        string vessel,
        string senderEmail,
        string senderPassword,
        string recipientEmail)
    {
        InitializeComponent();

        _scraper = scraper;
        _originalVessel = vessel ?? string.Empty;

        SenderEmailBox.Text = senderEmail;
        SenderPasswordBox.Password = senderPassword;
        RecipientEmailBox.Text = recipientEmail;

        // Seed the strict combo with the currently-saved vessel (if any) so Save works without forcing a
        // fetch, and so the existing value is shown selected when the dialog opens. The list is replaced
        // wholesale by "Load Vessels".
        // Seed via ItemsSource (NOT Items.Add) — WPF forbids setting ItemsSource later if the Items
        // collection was populated directly ("Items collection must be empty before using ItemsSource").
        if (!string.IsNullOrWhiteSpace(_originalVessel))
        {
            VesselCombo.ItemsSource = new List<string> { _originalVessel };
            VesselCombo.SelectedItem = _originalVessel;
        }

        UpdateSaveState();
    }

    /// <summary>Enables Save only when a non-empty vessel is selected in the strict combo.</summary>
    private void UpdateSaveState()
    {
        var selected = VesselCombo.SelectedItem as string;
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(selected);
    }

    private void VesselCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSaveState();

    /// <summary>
    /// Fetches the live vessel list via the DI scraper on a background thread, keeping the UI responsive.
    /// Disables Save/Load and shows the progress indicator during the fetch, restores state afterward,
    /// and re-selects the previously-saved vessel if it's present in the loaded list.
    /// </summary>
    private async void LoadVessels_Click(object sender, RoutedEventArgs e)
    {
        // Enter "loading" UI state.
        LoadingPanel.Visibility = Visibility.Visible;
        LoadVesselsButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        VesselCombo.IsEnabled = false;

        // Remember the current selection so we can restore/re-select it after the list is replaced.
        var previousSelection = (VesselCombo.SelectedItem as string) ?? _originalVessel;

        try
        {
            var vessels = await _scraper.GetAvailableVesselsAsync().ConfigureAwait(true);

            VesselCombo.ItemsSource = vessels;

            if (vessels.Count == 0)
            {
                MessageBox.Show(this,
                    "No vessels were found on the target page. Verify the Target URL and the vessel-list " +
                    "selector (Monitor:Selectors:VesselListSelector) in appsettings.json.",
                    "Load Vessels", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                // Re-select the saved vessel if it still exists in the freshly-loaded list; otherwise
                // select the first (top) item so the combo isn't blank — a clear visual sign to the
                // operator that the load completed successfully and a valid vessel is ready to save.
                var match = string.IsNullOrWhiteSpace(previousSelection)
                    ? null
                    : vessels.FirstOrDefault(v =>
                        string.Equals(v, previousSelection, StringComparison.OrdinalIgnoreCase));
                VesselCombo.SelectedItem = match ?? vessels[0];
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Failed to load the vessel list:\n\n{ex.Message}",
                "Load Vessels", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // Restore normal UI state regardless of success/failure.
            LoadingPanel.Visibility = Visibility.Collapsed;
            LoadVesselsButton.IsEnabled = true;
            VesselCombo.IsEnabled = true;
            UpdateSaveState();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var vessel = (VesselCombo.SelectedItem as string)?.Trim() ?? string.Empty;
        var senderEmail = SenderEmailBox.Text?.Trim() ?? string.Empty;
        var senderPassword = SenderPasswordBox.Password?.Trim() ?? string.Empty;
        var recipientEmail = RecipientEmailBox.Text?.Trim() ?? string.Empty;

        // Strict validation: a vessel must be selected from the dropdown (manual entry is disabled).
        if (string.IsNullOrWhiteSpace(vessel))
        {
            MessageBox.Show(this,
                "Please select a vessel from the list. Use \"Load Vessels\" to populate it.",
                "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            VesselCombo.Focus();
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
