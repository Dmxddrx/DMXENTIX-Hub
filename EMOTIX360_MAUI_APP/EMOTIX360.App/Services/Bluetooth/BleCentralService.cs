using Microsoft.Maui.ApplicationModel;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Diagnostics;

namespace EMOTIX360.App.Services;

/// <summary>
/// Central owner of the phone's BLE adapter.
/// Handles scanning, permissions and raw BLE discovery events.
/// Product identification is handled separately by DeviceDiscoveryService.
/// </summary>
public sealed class BleCentralService
{
    private readonly SemaphoreSlim _scanSemaphore = new(1, 1);

    public IBluetoothLE BluetoothLe { get; }

    public IAdapter Adapter { get; }

    public bool IsBluetoothOn =>
        BluetoothLe.State == BluetoothState.On;

    public BluetoothState BluetoothState =>
        BluetoothLe.State;

    public bool IsScanning =>
        Adapter.IsScanning;

    // Raw BLE discovery event. Product-specific filtering is not performed here.
    public event EventHandler<DeviceEventArgs>? DeviceDiscovered;

    // Shared scan lifecycle events.
    public event Action? ScanStarting;
    public event Action? ScanCompleted;


    private readonly object _devicesLock = new();
    private readonly Dictionary<Guid, IDevice> _discoveredDevices = new();

    /// <summary>
    /// Gets the Plugin.BLE peripheral object associated with a discovered ID.
    /// The returned object is used internally by the connection manager.
    /// </summary>
    public bool TryGetDiscoveredDevice(
        Guid deviceId,
        out IDevice? device)
    {
        lock (_devicesLock)
        {
            return _discoveredDevices.TryGetValue(
                deviceId,
                out device
            );
        }
    }

    public BleCentralService()
    {
        BluetoothLe = CrossBluetoothLE.Current;
        Adapter = BluetoothLe.Adapter;

        Adapter.DeviceDiscovered += OnAdapterDeviceDiscovered;
    }

    /// <summary>
    /// Requests the runtime permissions required by the current platform.
    /// </summary>
    public async Task EnsurePermissionsAsync()
    {
#if ANDROID
        PermissionStatus status =
            await Permissions.RequestAsync<BluetoothRuntimePermission>();

        if (status != PermissionStatus.Granted)
        {
            throw new UnauthorizedAccessException(
                "Bluetooth scan/connect permissions were not granted."
            );
        }
#endif
        // iOS requests Bluetooth permission through CoreBluetooth.
    }

    /// <summary>
    /// Scans for BLE peripherals without filtering by product name.
    /// The registered product profiles perform identification later.
    /// </summary>
    public async Task ScanAsync(
        TimeSpan? scanDuration = null,
        CancellationToken cancellationToken = default)
    {
        await _scanSemaphore.WaitAsync(cancellationToken);

        bool scanLifecycleStarted = false;

        try
        {
            await EnsurePermissionsAsync();

            cancellationToken.ThrowIfCancellationRequested();

            if (!IsBluetoothOn)
            {
                throw new InvalidOperationException(
                    "Bluetooth is turned off. Enable Bluetooth and try again."
                );
            }

            if (Adapter.IsScanning)
            {
                await Adapter.StopScanningForDevicesAsync();
            }

            TimeSpan duration =
                scanDuration ?? TimeSpan.FromSeconds(10);

            Adapter.ScanTimeout =
                Math.Max(1, (int)duration.TotalMilliseconds);

            scanLifecycleStarted = true;

            // Each scan starts with fresh peripheral references.
            lock (_devicesLock)
            {
                _discoveredDevices.Clear();
            }

            ScanStarting?.Invoke();

            try
            {
                await Adapter.StartScanningForDevicesAsync(
                    cancellationToken: cancellationToken
                );
            }
            finally
            {
                if (Adapter.IsScanning)
                {
                    await Adapter.StopScanningForDevicesAsync();
                }
            }
        }
        finally
        {
            try
            {
                if (scanLifecycleStarted)
                {
                    ScanCompleted?.Invoke();
                }
            }
            finally
            {
                _scanSemaphore.Release();
            }
        }
    }

    public async Task StopScanAsync()
    {
        if (Adapter.IsScanning)
        {
            await Adapter.StopScanningForDevicesAsync();
        }
    }

    private void OnAdapterDeviceDiscovered(
    object? sender,
    DeviceEventArgs args)
    {
        // Cache the raw peripheral before forwarding the event.
        lock (_devicesLock)
        {
            _discoveredDevices[args.Device.Id] = args.Device;
        }

        DeviceDiscovered?.Invoke(this, args);
    }
}

#if ANDROID
/// <summary>
/// Android 12+: Nearby devices permissions.
/// Android 11 and earlier: location permission for BLE scanning.
/// </summary>
internal sealed class BluetoothRuntimePermission
    : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[]
        RequiredPermissions
    {
        get
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                return new[]
                {
                    (Android.Manifest.Permission.BluetoothScan, true),
                    (Android.Manifest.Permission.BluetoothConnect, true)
                };
            }

            return new[]
            {
                (Android.Manifest.Permission.AccessFineLocation, true)
            };
        }
    }
}
#endif