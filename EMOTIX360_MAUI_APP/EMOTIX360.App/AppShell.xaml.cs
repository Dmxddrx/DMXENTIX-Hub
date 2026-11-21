using EMOTIX360.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Graphics;

namespace EMOTIX360.App;

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

        // Temporary destination for the My Devices navigation entry.
        // Step 5 will replace this placeholder with the real collection page.
        Routing.RegisterRoute(
            "my-devices",
            typeof(MyDevicesPlaceholderPage)
        );
    }
}

/// <summary>
/// Temporary My Devices page.
///
/// The actual saved-device collection will replace this page in Step 5.
/// </summary>
public sealed class MyDevicesPlaceholderPage : ContentPage
{
    public MyDevicesPlaceholderPage()
    {
        Title = "My Devices";
        BackgroundColor = Color.FromArgb("#0B111B");

        Content = new VerticalStackLayout
        {
            Padding = new Thickness(28),
            Spacing = 16,
            VerticalOptions = LayoutOptions.Center,

            Children =
            {
                new Label
                {
                    Text = "MY DEVICES",
                    FontSize = 12,
                    CharacterSpacing = 2,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb("#5796FF")
                },

                new Label
                {
                    Text = "Your saved device collection is coming in Step 5.",
                    FontSize = 20,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb("#F2F5FC")
                },

                new Label
                {
                    Text = "Connected devices are already being recorded in the device registry.",
                    FontSize = 13,
                    TextColor = Color.FromArgb("#8999AF")
                },

                new Button
                {
                    Text = "BACK TO DEVICES",
                    BackgroundColor = Color.FromArgb("#397FE8"),
                    TextColor = Colors.White,
                    CornerRadius = 13,
                    Command = new Command(
                        async () =>
                            await Shell.Current.GoToAsync("//devices")
                    )
                }
            }
        };
    }
}