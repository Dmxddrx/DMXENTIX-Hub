
using EMOTIX360.App.Services;

namespace EMOTIX360.App;

public partial class App : Application
{
    private readonly AppShell _appShell;
    private readonly BluetoothService _bluetoothService;

    public App(
        AppShell appShell,
        BluetoothService bluetoothService)
    {
        InitializeComponent();

        // EMOTIX360 uses a dark dashboard by default.
        UserAppTheme = AppTheme.Dark;

        _appShell = appShell;
        _bluetoothService = bluetoothService;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_appShell);

        // Start automatic BLE connection after the native window
        // has been created and the app is launching.
        window.Created += OnWindowCreated;

        return window;
    }

    private async void OnWindowCreated(object? sender, EventArgs e)
    {
        // Do not search for an EMOTIX360 device on the first launch.
        // Automatic connection starts only after a device has been
        // successfully connected and remembered previously.
        if (!_bluetoothService.HasRememberedDevice)
            return;

        try
        {
            // Ensure Android Nearby Devices permissions are available
            // before starting the automatic connection manager.
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
