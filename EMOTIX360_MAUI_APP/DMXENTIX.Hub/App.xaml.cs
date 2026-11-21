using EMOTIX360.App.Services;
using EMOTIX360.App.Services.Devices;
using EMOTIX360.App.Models.Devices;

namespace EMOTIX360.App;

public partial class App : Application
{
    private readonly AppShell _appShell;
    private readonly BluetoothService _bluetoothService;
    private readonly DeviceRegistryService _deviceRegistry;

    public App(
        AppShell appShell,
        BluetoothService bluetoothService,
        DeviceRegistryService deviceRegistry)
    {
        InitializeComponent();

        // EMOTIX360 uses a dark dashboard by default.
        UserAppTheme = AppTheme.Dark;

        _appShell = appShell;
        _bluetoothService = bluetoothService;
        _deviceRegistry = deviceRegistry;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_appShell);

        // Initialize persistent device records after the native window is created.
        // This does not start a BLE scan or alter the existing connection logic.
        window.Created += OnWindowCreated;

        return window;
    }

    private async void OnWindowCreated(object? sender, EventArgs e)
    {
        // Load the multi-device registry and import the previous remembered
        // EMOTIX360 identity before the existing auto-connect logic runs.
        try
        {
            await _deviceRegistry.InitializeAsync();

            IReadOnlyList<DeviceRecord> devices =
                await _deviceRegistry.GetDevicesAsync();

            System.Diagnostics.Debug.WriteLine(
                $"[DeviceRegistry] Startup loaded {devices.Count} saved device(s)."
            );
        }
        catch (Exception ex)
        {
            // Registry failure must not prevent the existing EMOTIX360
            // BLE connection workflow from starting.
            System.Diagnostics.Debug.WriteLine(
                $"[DeviceRegistry] Startup initialization failed: {ex.Message}"
            );
        }

        // Keep the existing single-device auto-connect behaviour during Step 1.
        if (!_bluetoothService.HasRememberedDevice)
            return;

        try
        {
            await _bluetoothService.EnsurePermissionsAsync();
            _bluetoothService.StartAutoConnect();
        }
        catch (UnauthorizedAccessException ex)
        {
            // Keep the app usable when the user denies Bluetooth permission.
            System.Diagnostics.Debug.WriteLine(
                $"[BLE] Startup auto-connect permission denied: {ex.Message}"
            );
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BLE] Startup auto-connect failed: {ex.Message}"
            );
        }
    }
}
