
using DMXENTIX.Hub.Models.Devices;
using DMXENTIX.Hub.Services.Bluetooth;
using DMXENTIX.Hub.Services.Devices;

namespace DMXENTIX.Hub;

public partial class App : Application
{
    private readonly AppShell _appShell;
    private readonly DeviceRegistryService _deviceRegistry;
    private readonly DeviceConnectionSupervisor _connectionSupervisor;

    public App(
        AppShell appShell,
        DeviceRegistryService deviceRegistry,
        DeviceConnectionSupervisor connectionSupervisor)
    {
        InitializeComponent();

        // EMOTIX360 uses a dark dashboard by default.
        UserAppTheme = AppTheme.Dark;

        _appShell = appShell;
        _deviceRegistry = deviceRegistry;
        _connectionSupervisor = connectionSupervisor;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_appShell);

        // Initialize persistent records and the connection supervisor
        // after the native window has been created.
        window.Created += OnWindowCreated;

        return window;
    }

    private async void OnWindowCreated(object? sender, EventArgs e)
    {
        try
        {
            await _deviceRegistry.InitializeAsync();

            IReadOnlyList<DeviceRecord> devices =
                await _deviceRegistry.GetDevicesAsync();

            System.Diagnostics.Debug.WriteLine(
                $"[DeviceRegistry] Startup loaded {devices.Count} saved device(s)."
            );

            // Start independent reconnection workers for every saved
            // device whose AutoReconnectEnabled preference is true.
            await _connectionSupervisor.StartAsync();
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ConnectionSupervisor] Bluetooth permission denied: {ex.Message}"
            );
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ConnectionSupervisor] Startup failed: {ex.Message}"
            );
        }
    }
}
