using Microsoft.Maui.Controls;

namespace DMXENTIX.Hub.Views;

public partial class Emotix360DashboardView : ContentView
{
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
}