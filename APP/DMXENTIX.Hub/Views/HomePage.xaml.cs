
using Microsoft.Maui.Controls;

namespace DMXENTIX.Hub.Views;

public partial class HomePage : ContentPage
{
    public HomePage()
    {
        InitializeComponent();
    }

    // ========================================================
    // OPEN DEVICE HUB
    // ========================================================

    private async void OnOpenDeviceHubClicked(
        object sender,
        EventArgs e)
    {
        Shell? shell = Shell.Current;

        if (shell is not null)
        {
            await shell.GoToAsync("//devices");
        }
    }

    // ========================================================
    // OPEN MY DEVICES
    // ========================================================

    private async void OnOpenMyDevicesClicked(
        object sender,
        EventArgs e)
    {
        Shell? shell = Shell.Current;

        if (shell is null)
            return;

        // My Devices belongs to the Devices navigation section.
        await shell.GoToAsync("//devices");

        await shell.GoToAsync("my-devices");
    }
}
