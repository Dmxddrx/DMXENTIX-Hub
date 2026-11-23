using Microsoft.Maui.Controls;

namespace DMXENTIX.Hub.Views;

public partial class Emotix360DashboardView : ContentView
{
    public event EventHandler? WifiConfigurationRequested;

    public Emotix360DashboardView()
    {
        InitializeComponent();
    }

    // The Home tab displays the device connection card.
    // An individual device workspace hides it because
    // the workspace already has its own connection section.
    public bool ShowDeviceConnectionCard
    {
        get => DeviceConnectionCard.IsVisible;
        set => DeviceConnectionCard.IsVisible = value;
    }

    // Request the parent workspace to open Wi-Fi configuration.
    private void OnWifiConfigurationClicked(
        object? sender,
        EventArgs e)
    {
        WifiConfigurationRequested?.Invoke(this, EventArgs.Empty);
    }
}