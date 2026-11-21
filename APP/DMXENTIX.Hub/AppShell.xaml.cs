using DMXENTIX.Hub.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DMXENTIX.Hub;

public partial class AppShell : Shell
{
    public AppShell(IServiceProvider services)
    {
        InitializeComponent();

        // Resolve pages from MAUI dependency injection when Shell creates a tab.
        // This allows each page to receive its ViewModel through its constructor.

        DevicesShellContent.ContentTemplate =
            new DataTemplate(() => services.GetRequiredService<DeviceHubPage>());

        HomeShellContent.ContentTemplate =
            new DataTemplate(() => services.GetRequiredService<HomePage>());

        DeviceShellContent.ContentTemplate =
            new DataTemplate(() => services.GetRequiredService<DevicePage>());

        SettingsShellContent.ContentTemplate =
            new DataTemplate(() => services.GetRequiredService<SettingsPage>());

        // Register the actual My Devices collection page.
        Routing.RegisterRoute(
            "my-devices",
            typeof(MyDevicesPage)
        );

        // Register the actual My Devices collection page.
        Routing.RegisterRoute(
            "my-devices",
            typeof(MyDevicesPage)
        );

        // Register the individual device workspace.
        Routing.RegisterRoute(
            "device-workspace",
            typeof(DeviceWorkspacePage)
        );
    }
}