using Microsoft.Maui.ApplicationModel;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Diagnostics;
#if ANDROID
using EMOTIX360.App.Platforms.Android;
#endif


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

    public event Action<bool>? BluetoothPowerStateChanged;


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

    // ========================================================
    // BLUETOOTH POWER STATE
    // ========================================================

    private void OnBluetoothStateChanged(
        object? sender,
        BluetoothStateChangedArgs args)
    {
        Debug.WriteLine(
            $"[BleCentral] Bluetooth state changed: {args.NewState}"
        );

        if (args.NewState == BluetoothState.Off)
        {
            // Discovered peripheral references are no longer valid
            // for a future connection after the adapter is disabled.
            lock (_devicesLock)
            {
                _discoveredDevices.Clear();
            }

            Debug.WriteLine(
                "[BleCentral] Discovered peripheral cache cleared."
            );

            BluetoothPowerStateChanged?.Invoke(false);
        }
        else if (args.NewState == BluetoothState.On)
        {
            BluetoothPowerStateChanged?.Invoke(true);
        }
    }

    public BleCentralService()
    {
        BluetoothLe = CrossBluetoothLE.Current;
        Adapter = BluetoothLe.Adapter;

        Adapter.DeviceDiscovered += OnAdapterDeviceDiscovered;

        // Monitor Bluetooth being switched on or off by the user.
        BluetoothLe.StateChanged += OnBluetoothStateChanged;
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

    // ========================================================
    // REQUEST BLUETOOTH ACTIVATION
    // ========================================================

    public async Task<bool> RequestEnableBluetoothAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsBluetoothOn)
            return true;

        // Android 12+ requires BLUETOOTH_CONNECT permission
        // for the Bluetooth activation request.
        await EnsurePermissionsAsync();

        cancellationToken.ThrowIfCancellationRequested();

#if ANDROID

        bool enabled = await BluetoothEnableRequest.RequestAsync(
            cancellationToken
        );

        Debug.WriteLine(
            $"[BleCentral] Bluetooth activation result: {enabled}"
        );

        return enabled;

#else

    throw new PlatformNotSupportedException(
        "Automatic Bluetooth activation requests are currently " +
        "supported on Android only. Please enable Bluetooth " +
        "through your device Settings."
    );

#endif
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

        Debug.WriteLine(
            $"[BleCentral] Scan requested. " +
            $"BluetoothState={BluetoothState}, " +
            $"AlreadyScanning={Adapter.IsScanning}"
        );

        bool scanLifecycleStarted = false;

        try
        {
            await EnsurePermissionsAsync();
            Debug.WriteLine(
                "[BleCentral] BLE permissions verified."
            );

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
            Debug.WriteLine(
                $"[BleCentral] Starting BLE scan for {duration.TotalSeconds:F0} seconds."
            );

            try
            {
                await Adapter.StartScanningForDevicesAsync(
                    cancellationToken: cancellationToken
                );
                Debug.WriteLine(
                    "[BleCentral] BLE scan operation completed."
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
        IDevice device = args.Device;

        lock (_devicesLock)
        {
            _discoveredDevices[device.Id] = device;
        }

        Debug.WriteLine(
            $"[BleCentral] Advertisement received | " +
            $"Name={device.Name ?? "(unnamed)"} | " +
            $"ID={device.Id} | " +
            $"RSSI={device.Rssi} dBm"
        );

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