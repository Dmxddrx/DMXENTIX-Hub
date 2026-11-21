using EMOTIX360.App.Services;
using EMOTIX360.App.Services.Devices;
using EMOTIX360.App.Services.Storage;
using EMOTIX360.App.ViewModels;
using EMOTIX360.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using EMOTIX360.App.Services.Devices.Profiles;
using EMOTIX360.App.Services.Bluetooth;
using EMOTIX360.App.Services.Devices.Protocols;

namespace EMOTIX360.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder.UseMauiApp<App>();

        // ----------------------------------------------------
        // BLE CENTRAL AND PRODUCT DISCOVERY
        // ----------------------------------------------------

        // One shared BLE adapter and scanner for the application.
        builder.Services.AddSingleton<BleCentralService>();

        // Product advertisement profiles.
        builder.Services.AddSingleton<IDeviceProfile, Emotix360DeviceProfile>();

        // Product discovery.
        builder.Services.AddSingleton<DeviceDiscoveryService>();

        // Registered product BLE protocols.
        // These protocol definitions are stateless and can be shared.
        builder.Services.AddSingleton<IDeviceProtocol, Emotix360Protocol>();

        // Multi-device session manager.
        builder.Services.AddSingleton<BleConnectionManager>();

        // Existing connection service remains temporarily for compatibility.
        builder.Services.AddSingleton<BluetoothService>();

        builder.Services.AddSingleton<DeviceCommandService>();
        builder.Services.AddSingleton<DeviceStatusService>();
        builder.Services.AddSingleton<WifiProvisioningService>();
        builder.Services.AddSingleton<WifiNetworkInfoService>();

        // Persistent multi-device registry.
        // The JSON store is replaceable by SQLite later without changing callers.
        builder.Services.AddSingleton<IDeviceRegistryStore, JsonDeviceRegistryStore>();
        builder.Services.AddSingleton<DeviceRegistryService>();

        // Shared view-model state is retained while navigating between tabs.
        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddSingleton<DeviceViewModel>();
        builder.Services.AddSingleton<WifiProvisioningViewModel>();

        // Pages are resolved by AppShell's DataTemplate factories.
        builder.Services.AddSingleton<HomePage>();
        builder.Services.AddSingleton<DevicePage>();
        builder.Services.AddSingleton<SettingsPage>();
        builder.Services.AddSingleton<AppShell>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
