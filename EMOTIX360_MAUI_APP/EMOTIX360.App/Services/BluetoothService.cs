using Microsoft.Maui.ApplicationModel;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Diagnostics;
using System.Text;
using System.Threading;
using static Microsoft.Maui.ApplicationModel.Permissions;
using ModelDeviceInfo = EMOTIX360.App.Models.DeviceInfo;
using Microsoft.Maui.Storage;
using EMOTIX360.App.Services.Devices.Profiles;

#if ANDROID
using EMOTIX360.App.Platforms.Android;
#endif

namespace EMOTIX360.App.Services;

/// <summary>
/// Owns BLE scanning, connection, GATT discovery, status notifications and command writes.
/// UI and view-model code must not retain Plugin.BLE service/characteristic objects.
/// </summary>
public sealed class BluetoothService
{
    public const string DeviceName =
    Emotix360DeviceProfile.AdvertisedName;

    public static readonly Guid ServiceUuid =
        Emotix360DeviceProfile.ServiceUuid;

    public static readonly Guid CommandCharacteristicUuid =
        Guid.Parse("beb5483e-36e1-4688-b7f5-ea07361b26a8");

    public static readonly Guid StatusCharacteristicUuid =
        Guid.Parse("e3223119-9445-4e96-a4a1-85358c4046a2");

    public static readonly Guid WifiProvisioningCharacteristicUuid =
        Guid.Parse("d3600001-5a3b-4b3f-9c21-8f5a2d7c0101");

    private readonly BleCentralService _bleCentral;
    private readonly IBluetoothLE _bluetoothLe;
    private readonly IAdapter _adapter;
    private readonly object _devicesLock = new();
    private readonly Dictionary<Guid, IDevice> _discoveredDevices = new();
    private readonly SemaphoreSlim _gattSemaphore = new(1, 1);

    // --------------------------------------------------------
    // AUTOMATIC BLE CONNECTION
    // --------------------------------------------------------

    private const string REMEMBERED_DEVICE_ID_KEY =
        "emotix360_remembered_ble_device_id";

    private const string REMEMBERED_DEVICE_NAME_KEY =
        "emotix360_remembered_ble_device_name";

    private const int AUTO_CONNECT_SCAN_DURATION_SECONDS = 8;

    // Prevent manual and automatic connection operations
    // from running simultaneously.
    private readonly SemaphoreSlim _connectionOperationSemaphore =
        new(1, 1);

    // Protect automatic connection task state.
    private readonly object _autoConnectLock = new();

    private CancellationTokenSource? _autoConnectCts;

    // A manual disconnect must not trigger automatic reconnection.
    private volatile bool _manualDisconnectRequested;
    // --------------------------------------------------------

    private IDevice? _connectedDevice;
    private ICharacteristic? _commandCharacteristic;
    private ICharacteristic? _statusCharacteristic;
    private ICharacteristic? _wifiProvisioningCharacteristic;

    private EventHandler<CharacteristicUpdatedEventArgs>?
        _wifiProvisioningUpdatedHandler;
    private EventHandler<CharacteristicUpdatedEventArgs>? _statusUpdatedHandler;

    // --------------------------------------------------------
    // BLE CONNECTION WATCHDOG
    // --------------------------------------------------------

    // Firmware normally sends a status notification every 1 second.
    // Six seconds allows several missed notifications before
    // considering the connection lost.

    private static readonly TimeSpan STATUS_TIMEOUT =
        TimeSpan.FromSeconds(6);

    private CancellationTokenSource? _heartbeatMonitorCts;

    private long _lastStatusReceivedUtcTicks;

    public BluetoothService(BleCentralService bleCentral)
    {
        _bleCentral = bleCentral;

        _bluetoothLe = _bleCentral.BluetoothLe;
        _adapter = _bleCentral.Adapter;

        _bleCentral.DeviceDiscovered += OnDeviceDiscovered;

        _adapter.DeviceDisconnected += OnDeviceDisconnected;

        // Clear the connection lookup cache before every scan.
        _bleCentral.ScanStarting += ClearDiscoveredDevices;
    }
    private void ClearDiscoveredDevices()
    {
        lock (_devicesLock)
        {
            _discoveredDevices.Clear();
        }
    }

    public event Action<ModelDeviceInfo>? DeviceDiscovered;
    public event Action<bool, string?>? ConnectionChanged;
    public event Action<string>? StatusPayloadReceived;
    public event Action<string>? WifiProvisioningStatusPayloadReceived;
    public string? CurrentWifiProvisioningStatusPayload { get; private set; }

    // Automatic connection progress for DeviceViewModel.
    public event Action<string>? ConnectionProgressChanged;

    // Previously connected device identity.
    public Guid? RememberedDeviceId
    {
        get
        {
            string savedId = Preferences.Get(
                REMEMBERED_DEVICE_ID_KEY,
                string.Empty
            );

            return Guid.TryParse(savedId, out Guid deviceId)
                ? deviceId
                : null;
        }
    }

    public string? RememberedDeviceName
    {
        get
        {
            string name = Preferences.Get(
                REMEMBERED_DEVICE_NAME_KEY,
                string.Empty
            );

            return string.IsNullOrWhiteSpace(name)
                ? null
                : name;
        }
    }

    public bool HasRememberedDevice =>
        RememberedDeviceId.HasValue;

    public bool IsAutoConnecting
    {
        get
        {
            lock (_autoConnectLock)
            {
                return _autoConnectCts is not null;
            }
        }
    }
    // --------------------------------------------------------

    public bool IsBluetoothOn => _bluetoothLe.State == BluetoothState.On;
    public bool IsConnected => _connectedDevice is not null;
    public string? ConnectedDeviceName => _connectedDevice?.Name;
    public BluetoothState BluetoothState => _bluetoothLe.State;

    /// <summary>Requests the runtime permissions required by the current platform.</summary>
    public Task EnsurePermissionsAsync()
    {
        return _bleCentral.EnsurePermissionsAsync();
    }

    public Task ScanAsync(
    TimeSpan? scanDuration = null,
    CancellationToken cancellationToken = default)
    {
        return _bleCentral.ScanAsync(
            scanDuration,
            cancellationToken
        );
    }

    public Task StopScanAsync()
    {
        return _bleCentral.StopScanAsync();
    }

    public async Task ConnectAsync(Guid deviceId)
    {
        // Stop an automatic connection attempt before starting
        // a user-requested connection.
        StopAutoConnectLoop(false);

        _manualDisconnectRequested = false;

        await _connectionOperationSemaphore.WaitAsync();

        try
        {
            IDevice device;

            lock (_devicesLock)
            {
                if (!_discoveredDevices.TryGetValue(
                        deviceId,
                        out device!))
                {
                    throw new InvalidOperationException(
                        "The selected device is no longer in the scan results. Scan again."
                    );
                }
            }

            await EnsurePermissionsAsync();

            await StopScanAsync();

            // Disconnect a previously connected device if necessary.
            if (_connectedDevice is not null)
            {
                await DisconnectAsync();

                // DisconnectAsync marks this as a deliberate disconnect.
                // We are now starting a new connection.
                _manualDisconnectRequested = false;
            }
#if ANDROID
            // Start foreground service while the user-initiated
            // connection operation is taking place.
            BleForegroundServiceController.Start();
#endif

            try
            {
                using var connectCts =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(15)
                    );

                await _adapter.ConnectToDeviceAsync(
                    device,
                    new ConnectParameters(autoConnect: false),
                    connectCts.Token
                );

                _connectedDevice = device;

                // Configure all GATT characteristics and notifications.
                await InitializeConnectedDeviceAsync(device);

                // Remember only a successfully initialized device.
                SaveRememberedDevice(device);

                Debug.WriteLine(
                    $"[BLE] Remembered device: {device.Name}, ID: {device.Id}"
                );
            }
            catch
            {
                await DisconnectAfterFailedConnectAsync(device);
#if ANDROID
                // Do not leave the foreground service running
                // when the manual connection attempt has failed.
                BleForegroundServiceController.Stop();
#endif
                throw;
            }
        }
        finally
        {
            _connectionOperationSemaphore.Release();
        }
    }
    // ========================================================
    // INITIALIZE CONNECTED BLE DEVICE
    // ========================================================

    private async Task InitializeConnectedDeviceAsync(IDevice device)
    {
        _connectedDevice = device;

        // --------------------------------------------------------
        // NEGOTIATE BLE MTU
        // --------------------------------------------------------

        // ESP32-S3 is configured for MTU 100.
        // Request the same MTU from Android.

#if ANDROID
        try
        {
            int negotiatedMtu = await device.RequestMtuAsync(100);

            Debug.WriteLine(
                $"[BLE] ATT MTU negotiated: {negotiatedMtu}"
            );

            Debug.WriteLine(
                $"[BLE] Maximum notification payload: " +
                $"{negotiatedMtu - 3} bytes"
            );
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BLE] MTU negotiation failed: {ex.Message}"
            );

            // Continue using the negotiated/default MTU.
        }
#endif

        // --------------------------------------------------------
        // DISCOVER GATT SERVICE
        // --------------------------------------------------------

        IService? service =
            await device.GetServiceAsync(ServiceUuid);

        if (service is null)
        {
            throw new InvalidOperationException(
                "EMOTIX360 BLE service was not found."
            );
        }

        _commandCharacteristic =
            await service.GetCharacteristicAsync(
                CommandCharacteristicUuid
            );

        _statusCharacteristic =
            await service.GetCharacteristicAsync(
                StatusCharacteristicUuid
            );

        if (_commandCharacteristic is null)
        {
            throw new InvalidOperationException(
                "The EMOTIX360 command characteristic was not found."
            );
        }

        if (_statusCharacteristic is null)
        {
            throw new InvalidOperationException(
                "The EMOTIX360 status characteristic was not found."
            );
        }

        // --------------------------------------------------------
        // WIFI PROVISIONING CHARACTERISTIC
        // --------------------------------------------------------

        _wifiProvisioningCharacteristic =
            await service.GetCharacteristicAsync(
                WifiProvisioningCharacteristicUuid
            );

        if (_wifiProvisioningCharacteristic is null)
        {
            throw new InvalidOperationException(
                "The EMOTIX360 Wi-Fi provisioning characteristic was not found."
            );
        }

        // --------------------------------------------------------
        // NORMAL DEVICE STATUS NOTIFICATIONS
        // --------------------------------------------------------

        _statusUpdatedHandler =
            OnStatusCharacteristicUpdated;

        _statusCharacteristic.ValueUpdated +=
            _statusUpdatedHandler;

        await _statusCharacteristic.StartUpdatesAsync();

        // Read the current device status.
        try
        {
            var readResult =
                await _statusCharacteristic.ReadAsync();

            if (readResult.resultCode == 0)
            {
                PublishStatus(readResult.data);
            }
            else
            {
                Debug.WriteLine(
                    $"[BLE] Initial status read failed. " +
                    $"GATT result: {readResult.resultCode}"
                );
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BLE] Initial status read exception: {ex.Message}"
            );
        }

        // --------------------------------------------------------
        // WIFI PROVISIONING STATUS NOTIFICATIONS
        // --------------------------------------------------------

        _wifiProvisioningUpdatedHandler =
            OnWifiProvisioningCharacteristicUpdated;

        _wifiProvisioningCharacteristic.ValueUpdated +=
            _wifiProvisioningUpdatedHandler;

        await _wifiProvisioningCharacteristic.StartUpdatesAsync();

        // Read the current provisioning status.
        try
        {
            var readResult =
                await _wifiProvisioningCharacteristic.ReadAsync();

            if (readResult.resultCode == 0)
            {
                PublishWifiProvisioningStatus(readResult.data);
            }
            else
            {
                Debug.WriteLine(
                    "[BLE] Initial Wi-Fi provisioning status read failed. " +
                    $"GATT result: {readResult.resultCode}"
                );
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BLE] Wi-Fi provisioning status read exception: {ex.Message}"
            );
        }

        // --------------------------------------------------------
        // CONNECTION COMPLETED
        // --------------------------------------------------------

        Debug.WriteLine(
            $"[BLE] Connected and GATT initialized: {device.Name}"
        );

        ConnectionChanged?.Invoke(
            true,
            device.Name
        );

        StartHeartbeatMonitor();
    }

    // ========================================================
    // SAVE REMEMBERED DEVICE IDENTITY
    // ========================================================

    private void SaveRememberedDevice(IDevice device)
    {
        Preferences.Set(
            REMEMBERED_DEVICE_ID_KEY,
            device.Id.ToString("D")
        );

        Preferences.Set(
            REMEMBERED_DEVICE_NAME_KEY,
            string.IsNullOrWhiteSpace(device.Name)
                ? DeviceName
                : device.Name
        );
    }

    // ========================================================
    // START AUTOMATIC CONNECTION
    // ========================================================

    public void StartAutoConnect()
    {
        if (IsConnected)
            return;

#if ANDROID
        // Keep the Android process active during connection attempts
        // and subsequent background reconnection.
        BleForegroundServiceController.Start();
#endif

        StartAutoConnectCore();
    }

    // Called when Android recreates the sticky foreground service.
    internal void ResumeAutoConnectFromForegroundService()
    {
        StartAutoConnectCore();
    }

    // Contains the actual automatic connection manager logic.
    private void StartAutoConnectCore()
    {
        if (IsConnected)
            return;

        lock (_autoConnectLock)
        {
            if (_autoConnectCts is not null)
                return;

            _manualDisconnectRequested = false;

            var source = new CancellationTokenSource();

            _autoConnectCts = source;

            Debug.WriteLine(
                "[BLE] Automatic connection manager started."
            );

            ConnectionProgressChanged?.Invoke(
                "Searching for EMOTIX360..."
            );

            _ = RunAutoConnectLoopAsync(source);
        }
    }

    // ========================================================
    // AUTOMATIC CONNECTION RETRY LOOP
    // ========================================================

    private async Task RunAutoConnectLoopAsync(
        CancellationTokenSource source)
    {
        CancellationToken cancellationToken =
            source.Token;

        int retryAttempt = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   !_manualDisconnectRequested &&
                   !IsConnected)
            {
                try
                {
                    if (!IsBluetoothOn)
                    {
                        ConnectionProgressChanged?.Invoke(
                            "Bluetooth is turned off. Enable Bluetooth."
                        );
                    }
                    else
                    {
                        bool connected = false;

                        Guid? rememberedId = RememberedDeviceId;

                        // ------------------------------------------------
                        // TRY REMEMBERED DEVICE FIRST
                        // ------------------------------------------------

                        if (rememberedId.HasValue)
                        {
                            ConnectionProgressChanged?.Invoke(
                                $"Connecting to {RememberedDeviceName ?? DeviceName}..."
                            );

                            connected =
                                await TryConnectToRememberedDeviceAsync(
                                    rememberedId.Value,
                                    cancellationToken
                                );
                        }

                        // ------------------------------------------------
                        // FALLBACK TO DEVICE DISCOVERY
                        // ------------------------------------------------

                        if (!connected &&
                            !cancellationToken.IsCancellationRequested &&
                            !IsConnected)
                        {
                            ConnectionProgressChanged?.Invoke(
                                "Searching for EMOTIX360..."
                            );

                            connected =
                                await TryScanAndConnectAsync(
                                    cancellationToken
                                );
                        }

                        if (connected || IsConnected)
                            break;
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[BLE] Automatic connection attempt failed: {ex.Message}"
                    );
                }

                if (cancellationToken.IsCancellationRequested ||
                    _manualDisconnectRequested ||
                    IsConnected)
                {
                    break;
                }

                retryAttempt++;

                int delaySeconds =
                    Math.Min(15, retryAttempt * 5);

                ConnectionProgressChanged?.Invoke(
                    $"EMOTIX360 not found. Retrying in {delaySeconds} seconds..."
                );

                await Task.Delay(
                    TimeSpan.FromSeconds(delaySeconds),
                    cancellationToken
                );
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when automatic connection is stopped.
        }
        finally
        {
            lock (_autoConnectLock)
            {
                if (ReferenceEquals(_autoConnectCts, source))
                {
                    _autoConnectCts = null;
                }
            }

            ConnectionProgressChanged?.Invoke(
                IsConnected
                    ? $"Connected to {ConnectedDeviceName ?? DeviceName}."
                    : "Automatic connection manager stopped."
            );

            source.Dispose();

            Debug.WriteLine(
                "[BLE] Automatic connection manager stopped."
            );
        }
    }

    // ========================================================
    // CONNECT TO REMEMBERED DEVICE
    // ========================================================

    private async Task<bool> TryConnectToRememberedDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        await _connectionOperationSemaphore.WaitAsync(
            cancellationToken
        );

        IDevice? device = null;

        bool connectionInitialized = false;

        try
        {
            if (IsConnected)
                return true;

            await EnsurePermissionsAsync();

            await StopScanAsync();

            using var attemptCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );

            // Bound the attempt so the retry loop can continue.
            attemptCts.CancelAfter(
                TimeSpan.FromSeconds(12)
            );

            Debug.WriteLine(
                $"[BLE] Reconnecting using remembered ID: {deviceId}"
            );

            device = await _adapter.ConnectToKnownDeviceAsync(
                deviceId,
                new ConnectParameters(autoConnect: false),
                attemptCts.Token
            );

            _connectedDevice = device;

            await InitializeConnectedDeviceAsync(device);

            connectionInitialized = true;

            SaveRememberedDevice(device);

            Debug.WriteLine(
                "[BLE] Remembered device reconnected successfully."
            );

            return true;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine(
                "[BLE] Remembered-device connection timed out."
            );

            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BLE] Remembered-device connection failed: {ex.Message}"
            );

            return false;
        }
        finally
        {
            if (!connectionInitialized &&
                device is not null &&
                _connectedDevice?.Id == device.Id)
            {
                await DisconnectAfterFailedConnectAsync(device);
            }

            _connectionOperationSemaphore.Release();
        }
    }

    // ========================================================
    // SCAN AND CONNECT TO EMOTIX360
    // ========================================================

    private async Task<bool> TryScanAndConnectAsync(
        CancellationToken cancellationToken)
    {
        await _connectionOperationSemaphore.WaitAsync(
            cancellationToken
        );

        IDevice? device = null;
        bool connectionInitialized = false;

        try
        {
            if (IsConnected)
                return true;

            Debug.WriteLine(
                "[BLE] Starting automatic EMOTIX360 scan."
            );

            await ScanAsync(
                TimeSpan.FromSeconds(
                    AUTO_CONNECT_SCAN_DURATION_SECONDS
                ),
                cancellationToken
            );

            // Find the strongest matching EMOTIX360 advertisement.
            lock (_devicesLock)
            {
                device = _discoveredDevices.Values
                    .Where(d => string.Equals(
                        d.Name,
                        DeviceName,
                        StringComparison.OrdinalIgnoreCase
                    ))
                    .OrderByDescending(d => d.Rssi)
                    .FirstOrDefault();
            }

            if (device is null)
            {
                Debug.WriteLine(
                    "[BLE] EMOTIX360 was not found during automatic scan."
                );

                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            Debug.WriteLine(
                $"[BLE] Automatically discovered: {device.Name}"
            );

            using var attemptCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );

            attemptCts.CancelAfter(
                TimeSpan.FromSeconds(15)
            );

            await _adapter.ConnectToDeviceAsync(
                device,
                new ConnectParameters(autoConnect: false),
                attemptCts.Token
            );

            _connectedDevice = device;

            await InitializeConnectedDeviceAsync(device);
            connectionInitialized = true;

            // Update remembered ID if the discovered device has
            // a different identity from the previously saved one.
            SaveRememberedDevice(device);

            Debug.WriteLine(
                "[BLE] Automatic connection completed successfully."
            );

            return true;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine(
                "[BLE] Automatic scan connection attempt timed out."
            );

            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BLE] Automatic scan connection failed: {ex.Message}"
            );

            return false;
        }
        finally
        {
            if (!connectionInitialized &&
                device is not null &&
                _connectedDevice?.Id == device.Id)
            {
                await DisconnectAfterFailedConnectAsync(device);
            }

            _connectionOperationSemaphore.Release();
        }
    }

    // ========================================================
    // STOP AUTOMATIC CONNECTION LOOP
    // ========================================================

    private void StopAutoConnectLoop(bool manualDisconnect)
    {
        if (manualDisconnect)
        {
            _manualDisconnectRequested = true;
        }

        CancellationTokenSource? source;

        lock (_autoConnectLock)
        {
            source = _autoConnectCts;

            _autoConnectCts = null;
        }

        if (source is not null)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The connection task has already completed.
            }
        }
    }

    public async Task DisconnectAsync()
    {
        // A user-requested disconnect must suppress automatic reconnection.
        StopAutoConnectLoop(manualDisconnect: true);

        // Serialize disconnect with writes; Android GATT operations should not overlap.
        await _gattSemaphore.WaitAsync();
        try
        {
            IDevice? device = _connectedDevice;
            if (device is null)
                return;

            try
            {
                ICharacteristic? statusCharacteristic = _statusCharacteristic;
                EventHandler<CharacteristicUpdatedEventArgs>? statusHandler = _statusUpdatedHandler;

                if (statusCharacteristic is not null)
                {
                    if (statusHandler is not null)
                        statusCharacteristic.ValueUpdated -= statusHandler;

                    try
                    {
                        await statusCharacteristic.StopUpdatesAsync();
                    }
                    catch
                    {
                        // A lost link may already have stopped notifications.
                        // Continue and explicitly ask the adapter to disconnect.
                    }
                }
                // --------------------------------------------------------
                // STOP WIFI PROVISIONING NOTIFICATIONS
                // --------------------------------------------------------

                ICharacteristic? wifiCharacteristic =
                    _wifiProvisioningCharacteristic;

                EventHandler<CharacteristicUpdatedEventArgs>?
                    wifiHandler = _wifiProvisioningUpdatedHandler;

                if (wifiCharacteristic is not null)
                {
                    if (wifiHandler is not null)
                    {
                        wifiCharacteristic.ValueUpdated -= wifiHandler;
                    }

                    try
                    {
                        await wifiCharacteristic.StopUpdatesAsync();
                    }
                    catch
                    {
                        // The BLE connection may already have been lost.
                        // Continue with explicit disconnection.
                    }
                }

                await _adapter.DisconnectDeviceAsync(device);
            }
            finally
            {
                // DeviceDisconnected may already have cleared the state and raised the event.
                bool stillOwned = _connectedDevice?.Id == device.Id;
                ClearConnectionState();

                if (stillOwned)
                    ConnectionChanged?.Invoke(false, "Disconnected");
            }
        }
        finally
        {
            _gattSemaphore.Release();
#if ANDROID
            // The user explicitly disconnected the ESP32.
            // Remove the persistent foreground notification and stop the service.
            BleForegroundServiceController.Stop();
#endif
        }
    }

    /// <summary>
    /// Writes a UTF-8 command without a newline. Android GATT operations are serialized
    /// because overlapping characteristic operations can fail at the platform layer.
    /// </summary>
    public async Task WriteCommandAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException(
                "Command cannot be empty.",
                nameof(command)
            );
        }

        byte[] payload = Encoding.UTF8.GetBytes(command.Trim());

        await _gattSemaphore.WaitAsync();

        try
        {
            ICharacteristic characteristic =
                _commandCharacteristic
                ?? throw new InvalidOperationException(
                    "Not connected to EMOTIX360 command characteristic."
                );

            if (!IsConnected)
            {
                throw new InvalidOperationException(
                    "EMOTIX360 is not connected."
                );
            }

            // Plugin.BLE returns an integer GATT result code.
            // Keep the write on the Android/iOS main thread.
            int resultCode = await MainThread.InvokeOnMainThreadAsync(
                () => characteristic.WriteAsync(payload)
            );

            // GATT_SUCCESS = 0.
            if (resultCode != 0)
            {
                throw new IOException(
                    $"BLE command write failed. GATT result code: {resultCode}"
                );
            }
        }
        finally
        {
            _gattSemaphore.Release();
        }
    }
    // ========================================================
    // WIFI PROVISIONING FRAME WRITE
    // ========================================================

    public async Task WriteWifiProvisioningFrameAsync(string frame)
    {
        if (string.IsNullOrEmpty(frame))
        {
            throw new ArgumentException(
                "Wi-Fi provisioning frame cannot be empty.",
                nameof(frame)
            );
        }

        // The ESP32 accepts printable ASCII frames up to 20 bytes.
        if (frame.Length > 20 ||
            frame.Any(character => character < '!' || character > '~'))
        {
            throw new ArgumentException(
                "Invalid Wi-Fi provisioning frame. " +
                "Frames must contain 1–20 printable ASCII characters.",
                nameof(frame)
            );
        }

        byte[] payload = Encoding.ASCII.GetBytes(frame);

        await _gattSemaphore.WaitAsync();

        try
        {
            ICharacteristic characteristic =
                _wifiProvisioningCharacteristic
                ?? throw new InvalidOperationException(
                    "The Wi-Fi provisioning characteristic is unavailable."
                );

            if (!IsConnected)
            {
                throw new InvalidOperationException(
                    "EMOTIX360 is not connected."
                );
            }

            int resultCode = await MainThread.InvokeOnMainThreadAsync(
                () => characteristic.WriteAsync(payload)
            );

            if (resultCode != 0)
            {
                throw new IOException(
                    "Wi-Fi provisioning frame write failed. " +
                    $"GATT result code: {resultCode}"
                );
            }
        }
        finally
        {
            _gattSemaphore.Release();
        }
    }
    private void OnDeviceDiscovered(
    object? sender,
    DeviceEventArgs args)
    {
        IDevice device = args.Device;

        if (!string.Equals(
                device.Name,
                DeviceName,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_devicesLock)
        {
            _discoveredDevices[device.Id] = device;
        }

        DeviceDiscovered?.Invoke(
            new ModelDeviceInfo(
                device.Id,
                device.Name,
                device.Rssi
            )
        );
    }

    private void OnDeviceDisconnected(
    object? sender,
    DeviceEventArgs args)
    {
        if (_connectedDevice?.Id != args.Device.Id)
            return;

        bool shouldReconnect =
            !_manualDisconnectRequested &&
            RememberedDeviceId.HasValue;

        // Remove the normal status handler.
        if (_statusCharacteristic is not null &&
            _statusUpdatedHandler is not null)
        {
            _statusCharacteristic.ValueUpdated -=
                _statusUpdatedHandler;
        }

        // Remove the Wi-Fi provisioning handler.
        if (_wifiProvisioningCharacteristic is not null &&
            _wifiProvisioningUpdatedHandler is not null)
        {
            _wifiProvisioningCharacteristic.ValueUpdated -=
                _wifiProvisioningUpdatedHandler;
        }

        ClearConnectionState();

        Debug.WriteLine(
            _manualDisconnectRequested
        ? "[BLE] Device disconnected by user."
        : "[BLE] Device disconnected unexpectedly."
            );

        ConnectionChanged?.Invoke(
            false,
            "The device disconnected."
        );

        // Retry only if the user did not explicitly disconnect.
        if (shouldReconnect)
        {
            Debug.WriteLine(
                "[BLE] Starting automatic reconnection."
            );

            StartAutoConnect();
        }
    }

    private void OnStatusCharacteristicUpdated(object? sender, CharacteristicUpdatedEventArgs args)
    {
        PublishStatus(args.Characteristic.Value);
    }

    private void PublishStatus(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return;

        // Record that the ESP32 is still communicating.
        Interlocked.Exchange(
            ref _lastStatusReceivedUtcTicks,
            DateTime.UtcNow.Ticks
        );

        string payload = Encoding.UTF8.GetString(bytes)
            .Trim('\0', '\r', '\n', ' ');

        if (!string.IsNullOrWhiteSpace(payload))
        {
            StatusPayloadReceived?.Invoke(payload);
        }
    }

    // --------------------------------------------------------
    // WIFI PROVISIONING NOTIFICATION CALLBACK
    // --------------------------------------------------------

    private void OnWifiProvisioningCharacteristicUpdated(
        object? sender,
        CharacteristicUpdatedEventArgs args)
    {
        PublishWifiProvisioningStatus(
            args.Characteristic.Value
        );
    }


    // --------------------------------------------------------
    // PUBLISH WIFI PROVISIONING STATUS
    // --------------------------------------------------------

    private void PublishWifiProvisioningStatus(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return;

        string payload = Encoding.UTF8.GetString(bytes)
            .Trim('\0', '\r', '\n', ' ');

        if (string.IsNullOrWhiteSpace(payload))
            return;

        // Keep the latest device status so Settings can retrieve it
        // even if its ViewModel is created after BLE connection.
        CurrentWifiProvisioningStatusPayload = payload;

        WifiProvisioningStatusPayloadReceived?.Invoke(payload);
    }

    private async Task DisconnectAfterFailedConnectAsync(IDevice device)
    {
        try
        {
            // Remove normal status notifications.
            if (_statusCharacteristic is not null &&
                _statusUpdatedHandler is not null)
            {
                _statusCharacteristic.ValueUpdated -=
                    _statusUpdatedHandler;

                try
                {
                    await _statusCharacteristic.StopUpdatesAsync();
                }
                catch
                {
                    // Continue connection cleanup.
                }
            }

            // Remove Wi-Fi provisioning notifications.
            if (_wifiProvisioningCharacteristic is not null &&
                _wifiProvisioningUpdatedHandler is not null)
            {
                _wifiProvisioningCharacteristic.ValueUpdated -=
                    _wifiProvisioningUpdatedHandler;

                try
                {
                    await _wifiProvisioningCharacteristic.StopUpdatesAsync();
                }
                catch
                {
                    // Continue connection cleanup.
                }
            }

            await _adapter.DisconnectDeviceAsync(device);
        }
        catch
        {
            // Preserve the original service-discovery/connection exception.
        }
        finally
        {
            bool stillOwned = _connectedDevice?.Id == device.Id;

            ClearConnectionState();

            if (stillOwned)
            {
                ConnectionChanged?.Invoke(
                    false,
                    "Connection failed."
                );
            }
        }
    }


    // ========================================================
    // BLE CONNECTION WATCHDOG
    // ========================================================

    private void StartHeartbeatMonitor()
    {
        StopHeartbeatMonitor();

        Interlocked.Exchange(
            ref _lastStatusReceivedUtcTicks,
            DateTime.UtcNow.Ticks
        );

        var source = new CancellationTokenSource();

        Interlocked.Exchange(
            ref _heartbeatMonitorCts,
            source
        );

        _ = MonitorHeartbeatAsync(source);
    }


    private void StopHeartbeatMonitor()
    {
        var source = Interlocked.Exchange(
            ref _heartbeatMonitorCts,
            null
        );

        if (source is not null)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Monitor already finished.
            }
        }
    }


    // --------------------------------------------------------
    // MONITOR INCOMING STATUS
    // --------------------------------------------------------

    private async Task MonitorHeartbeatAsync(
        CancellationTokenSource source)
    {
        try
        {
            using var timer = new PeriodicTimer(
                TimeSpan.FromSeconds(1)
            );

            while (await timer.WaitForNextTickAsync(source.Token))
            {
                // No active connection.
                if (_connectedDevice is null)
                    return;

                long lastReceivedTicks =
                    Interlocked.Read(
                        ref _lastStatusReceivedUtcTicks
                    );

                if (lastReceivedTicks == 0)
                    continue;

                long elapsedTicks =
                    DateTime.UtcNow.Ticks - lastReceivedTicks;

                if (elapsedTicks >= STATUS_TIMEOUT.Ticks)
                {
                    Debug.WriteLine(
                        "[BLE] Status heartbeat timeout. " +
                        "Connection considered lost."
                    );

                    await HandleHeartbeatTimeoutAsync(source.Token);

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during a manual or unexpected disconnect.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BLE] Heartbeat monitor error: {ex.Message}"
            );
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _heartbeatMonitorCts,
                null,
                source
            );

            source.Dispose();
        }
    }


    // --------------------------------------------------------
    // HANDLE HEARTBEAT TIMEOUT
    // --------------------------------------------------------

    private async Task HandleHeartbeatTimeoutAsync(
        CancellationToken cancellationToken)
    {
        IDevice? lostDevice = null;

        await _gattSemaphore.WaitAsync(cancellationToken);

        try
        {
            IDevice? device = _connectedDevice;

            if (device is null)
                return;

            // Recheck before clearing the connection.
            long lastReceivedTicks =
                Interlocked.Read(
                    ref _lastStatusReceivedUtcTicks
                );

            if (lastReceivedTicks != 0 &&
                DateTime.UtcNow.Ticks - lastReceivedTicks
                    < STATUS_TIMEOUT.Ticks)
            {
                return;
            }

            lostDevice = device;

            // Remove the notification callback.
            if (_statusCharacteristic is not null &&
                _statusUpdatedHandler is not null)
            {
                _statusCharacteristic.ValueUpdated -=
                    _statusUpdatedHandler;
            }

            if (_wifiProvisioningCharacteristic is not null &&
                _wifiProvisioningUpdatedHandler is not null)
            {
                _wifiProvisioningCharacteristic.ValueUpdated -=
                    _wifiProvisioningUpdatedHandler;
            }

            // Clear all local connection references.
            ClearConnectionState();

            Debug.WriteLine(
                "[BLE] EMOTIX360 connection lost."
            );

            // Notify HomeViewModel and DeviceViewModel.
            ConnectionChanged?.Invoke(
                false,
                "EMOTIX360 disconnected. Device is powered off or out of range."
            );
        }
        finally
        {
            _gattSemaphore.Release();
        }

        // Request cleanup of the stale peripheral connection.
        // This is best-effort because the device may already be off.
        if (lostDevice is not null)
        {
            try
            {
                await _adapter.DisconnectDeviceAsync(lostDevice);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[BLE] Disconnection cleanup: {ex.Message}"
                );
            }

            // The heartbeat watchdog detected an unexpected loss.
            // Restore the connection automatically.
            if (!_manualDisconnectRequested &&
                RememberedDeviceId.HasValue)
            {
                StartAutoConnect();
            }
        }
    }


    private void ClearConnectionState()
    {
        StopHeartbeatMonitor();

        Interlocked.Exchange(
            ref _lastStatusReceivedUtcTicks,
            0
        );

        _connectedDevice = null;
        _commandCharacteristic = null;
        _statusCharacteristic = null;
        _statusUpdatedHandler = null;
        _wifiProvisioningCharacteristic = null;
        _wifiProvisioningUpdatedHandler = null;
        CurrentWifiProvisioningStatusPayload = null;
    }

    private async Task StopScanSafelyAsync()
    {
        try
        {
            await StopScanAsync();
        }
        catch
        {
            // Cancellation may race with the scan timeout; ignore only this cleanup error.
        }
    }
}
