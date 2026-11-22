using DMXENTIX.Hub.Services;
using DMXENTIX.Hub.Services.Devices;
using DMXENTIX.Hub.Services.Storage;
using DMXENTIX.Hub.ViewModels;
using DMXENTIX.Hub.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DMXENTIX.Hub.Services.Devices.Profiles;
using DMXENTIX.Hub.Services.Bluetooth;
using DMXENTIX.Hub.Services.Devices.Protocols;

namespace DMXENTIX.Hub;

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
        builder.Services.AddSingleton<IDeviceProfile, SoundNodeDeviceProfile>();

        // Product discovery.
        builder.Services.AddSingleton<DeviceDiscoveryService>();

        // Registered product BLE protocols.
        // These protocol definitions are stateless and can be shared.
        builder.Services.AddSingleton<IDeviceProtocol, Emotix360Protocol>();
        builder.Services.AddSingleton<IDeviceProtocol, SoundNodeProtocol>();

        // Multi-device session manager.
        builder.Services.AddSingleton<BleConnectionManager>();

        builder.Services.AddSingleton<DeviceSessionSelectionService>();

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
        builder.Services.AddSingleton<DeviceHubViewModel>();
        builder.Services.AddSingleton<MyDevicesViewModel>();
        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddSingleton<DeviceViewModel>();
        builder.Services.AddSingleton<WifiProvisioningViewModel>();
        // Individual device workspace.
        builder.Services.AddTransient<DeviceWorkspaceViewModel>();

        // Pages are resolved by AppShell's DataTemplate factories.
        builder.Services.AddSingleton<DeviceHubPage>();
        builder.Services.AddSingleton<MyDevicesPage>();
        builder.Services.AddSingleton<HomePage>();
        builder.Services.AddSingleton<DevicePage>();
        builder.Services.AddSingleton<SettingsPage>();
        builder.Services.AddSingleton<AppShell>();
        builder.Services.AddTransient<DeviceWorkspacePage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
