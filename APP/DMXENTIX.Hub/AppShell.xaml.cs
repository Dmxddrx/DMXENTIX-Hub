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
        // Close the Home information popup before navigating
        // or allowing Android to handle the Back action.
        if (CurrentPage is HomePage homePage &&
            homePage.TryCloseInfoPopup())
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