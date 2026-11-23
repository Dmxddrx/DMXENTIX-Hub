using DMXENTIX.Hub.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;

namespace DMXENTIX.Hub;

public partial class AppShell : Shell
{
    public AppShell(IServiceProvider services)
    {
        InitializeComponent();

        HomeShellContent.ContentTemplate =
            new DataTemplate(
                () => services.GetRequiredService<HomePage>()
            );

        SettingsShellContent.ContentTemplate =
            new DataTemplate(
                () => services.GetRequiredService<SettingsPage>()
            );

        // Device discovery is a normal page, not a footer tab.
        Routing.RegisterRoute(
            "devices",
            typeof(DeviceHubPage)
        );

        Routing.RegisterRoute(
            "my-devices",
            typeof(MyDevicesPage)
        );

        Routing.RegisterRoute(
            "device-workspace",
            typeof(DeviceWorkspacePage)
        );
    }

    // ========================================================
    // ANDROID BACK NAVIGATION
    // ========================================================

    protected override bool OnBackButtonPressed()
    {
        // Close the EMOTIX360 device information popup before
        // handling Wi-Fi configuration or workspace navigation.
        if (CurrentPage is DeviceWorkspacePage infoWorkspacePage &&
            infoWorkspacePage.TryCloseInfoPopup())
        {
            return true;
        }

        // Close EMOTIX360 Wi-Fi configuration before
        // navigating away from the device workspace.
        if (CurrentPage is DeviceWorkspacePage workspacePage &&
            workspacePage.TryCloseWifiConfiguration())
        {
            return true;
        }
        string currentRoute =
            CurrentState.Location.OriginalString.Trim('/');

        // Home is the root of the application.
        // Any other page returns directly to Home.
        if (!string.Equals(
                currentRoute,
                "home",
                StringComparison.OrdinalIgnoreCase))
        {
            MainThread.BeginInvokeOnMainThread(
                async () =>
                {
                    await GoToAsync("//home");
                }
            );

            return true;
        }

        // Normal Android back behavior on the Home page.
        return base.OnBackButtonPressed();
    }
}