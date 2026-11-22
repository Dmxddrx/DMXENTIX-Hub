using DMXENTIX.Hub.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;

namespace DMXENTIX.Hub;

public partial class AppShell : Shell
{
    public AppShell(IServiceProvider services)
    {
        InitializeComponent();

        // Resolve pages through dependency injection.
        // Home is the first Shell destination.
        HomeShellContent.ContentTemplate =
            new DataTemplate(
                () => services.GetRequiredService<HomePage>()
            );

        DevicesShellContent.ContentTemplate =
            new DataTemplate(
                () => services.GetRequiredService<DeviceHubPage>()
            );

        SettingsShellContent.ContentTemplate =
            new DataTemplate(
                () => services.GetRequiredService<SettingsPage>()
            );

        // Register the saved-device collection page.
        Routing.RegisterRoute(
            "my-devices",
            typeof(MyDevicesPage)
        );

        // Register individual product workspaces.
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