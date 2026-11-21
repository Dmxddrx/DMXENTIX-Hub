using System.Diagnostics;
using System.Text;
using System.Threading;
using DMXENTIX.Hub.Models.Devices;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Microsoft.Maui.ApplicationModel;
using AppDeviceType = global::DMXENTIX.Hub.Models.Devices.DeviceType;

namespace DMXENTIX.Hub.Services.Bluetooth;

/// <summary>
/// Owns one physical BLE device connection.
///
/// Every session has its own:
/// - IDevice reference
/// - GATT characteristic references
/// - Notification event handlers
/// - Write semaphore
/// - Connection state
/// - Status payloads
/// - Heartbeat monitor
///
/// No GATT state is shared with other sessions.
/// </summary>
public sealed class BleDeviceSession : IDeviceSession
{
    private static readonly TimeSpan StatusTimeout =
        TimeSpan.FromSeconds(6);

    private readonly IAdapter _adapter;

    private readonly IDeviceProtocol _protocol;

    // Serialize GATT operations for this device only.
    private readonly SemaphoreSlim _gattSemaphore = new(1, 1);

    private IDevice? _device;

    private IService? _service;

    private ICharacteristic? _commandCharacteristic;

    private ICharacteristic? _statusCharacteristic;

    private ICharacteristic? _wifiProvisioningCharacteristic;

    private EventHandler<CharacteristicUpdatedEventArgs>?
        _statusUpdatedHandler;

    private EventHandler<CharacteristicUpdatedEventArgs>?
        _wifiProvisioningUpdatedHandler;

    private CancellationTokenSource? _heartbeatMonitorCts;

    private long _lastStatusReceivedUtcTicks;

    private DeviceConnectionState _connectionState =
        DeviceConnectionState.Discovered;

    private string? _lastStatusPayload;

    private string? _lastProvisioningStatusPayload;

    private string _deviceName;

    private bool _intentionalDisconnect;

    private bool _disposed;

    public BleDeviceSession(
        Guid deviceId,
        AppDeviceType deviceType,
        string productName,
        IAdapter adapter,
        IDeviceProtocol protocol)
    {
        if (protocol.DeviceType != deviceType)
        {
            throw new ArgumentException(
                "The protocol device type does not match the session device type.",
                nameof(protocol)
            );
        }

        DeviceId = deviceId;
        DeviceType = deviceType;
        ProductName = productName;
        _deviceName = productName;

        _adapter = adapter;
        _protocol = protocol;

        // Every session listens to the shared adapter,
        // but only handles events belonging to its own device ID.
        _adapter.DeviceDisconnected += OnAdapterDeviceDisconnected;
    }

    public Guid DeviceId { get; }

    public AppDeviceType DeviceType { get; }

    public string ProductName { get; }

    public string DeviceName => _deviceName;

    public DeviceConnectionState ConnectionState =>
        _connectionState;

    public bool IsConnected =>
        ConnectionState == DeviceConnectionState.Connected;

    public string? LastStatusPayload =>
        _lastStatusPayload;

    public string? LastProvisioningStatusPayload =>
        _lastProvisioningStatusPayload;

    public event Action<IDeviceSession>? StateChanged;

    public event Action<IDeviceSession, string>? StatusPayloadReceived;

    public event Action<IDeviceSession, string>?
        ProvisioningStatusPayloadReceived;

    // ========================================================
    // HANDLE BLUETOOTH RADIO TURNED OFF
    // ========================================================

    public async Task HandleBluetoothUnavailableAsync(
        CancellationToken cancellationToken = default)
    {
        StopHeartbeatMonitor();

        await _gattSemaphore.WaitAsync(cancellationToken);

        try
        {
            // No active connection or connection attempt to invalidate.
            if (_device is null &&
                _connectionState != DeviceConnectionState.Connecting &&
                _connectionState != DeviceConnectionState.Reconnecting &&
                _connectionState != DeviceConnectionState.Error)
            {
                return;
            }

            _intentionalDisconnect = true;

            Debug.WriteLine(
                $"[BleSession:{DeviceId}] Bluetooth unavailable. " +
                "Invalidating session immediately."
            );

            // Bluetooth is already disabled.
            // Detach local callbacks without attempting more GATT operations.
            DetachNotificationHandlers();

            ClearConnectionReferences();

            SetConnectionState(
                DeviceConnectionState.Disconnected
            );
        }
        finally
        {
            _intentionalDisconnect = false;
            _gattSemaphore.Release();
        }
    }

    // ========================================================
    // DETACH NOTIFICATION CALLBACKS
    // ========================================================

    private void DetachNotificationHandlers()
    {
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
    }

    // ========================================================
    // CONNECT
    // ========================================================

    public async Task ConnectAsync(
        IDevice device,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (device.Id != DeviceId)
        {
            throw new ArgumentException(
                "The supplied BLE device ID does not belong to this session.",
                nameof(device)
            );
        }

        await _gattSemaphore.WaitAsync(cancellationToken);

        try
        {
            if (IsConnected)
                return;

            _device = device;

            _deviceName = string.IsNullOrWhiteSpace(device.Name)
                ? ProductName
                : device.Name;

            SetConnectionState(DeviceConnectionState.Connecting);

            using var connectCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );

            connectCts.CancelAfter(TimeSpan.FromSeconds(15));

            Debug.WriteLine(
                $"[BleSession:{DeviceId}] Connecting to {_deviceName}."
            );

            await _adapter.ConnectToDeviceAsync(
                device,
                new ConnectParameters(autoConnect: false),
                connectCts.Token
            );

            await InitializeGattAsync(
                device,
                cancellationToken
            );

            SetConnectionState(DeviceConnectionState.Connected);

            StartHeartbeatMonitor();

            Debug.WriteLine(
                $"[BleSession:{DeviceId}] GATT initialized."
            );
        }
        catch
        {
            _intentionalDisconnect = true;

            try
            {
                await StopNotificationsAsync();

                IDevice? failedDevice = _device;

                if (failedDevice is not null)
                {
                    try
                    {
                        await _adapter.DisconnectDeviceAsync(
                            failedDevice
                        );
                    }
                    catch
                    {
                        // Preserve the original connection exception.
                    }
                }
            }
            finally
            {
                ClearConnectionReferences();

                _intentionalDisconnect = false;

                SetConnectionState(DeviceConnectionState.Error);
            }

            throw;
        }
        finally
        {
            _gattSemaphore.Release();
        }
    }

    // ========================================================
    // CONNECT TO REMEMBERED DEVICE
    // ========================================================

    public async Task ConnectKnownDeviceAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gattSemaphore.WaitAsync(cancellationToken);

        try
        {
            if (IsConnected)
                return;

            _intentionalDisconnect = false;

            SetConnectionState(DeviceConnectionState.Connecting);

            using var connectCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );

            connectCts.CancelAfter(TimeSpan.FromSeconds(15));

            Debug.WriteLine(
                $"[BleSession:{DeviceId}] Connecting to remembered device."
            );

            _device = await _adapter.ConnectToKnownDeviceAsync(
                DeviceId,
                new ConnectParameters(autoConnect: false),
                connectCts.Token
            );

            _deviceName = string.IsNullOrWhiteSpace(_device.Name)
                ? ProductName
                : _device.Name;

            await InitializeGattAsync(
                _device,
                connectCts.Token
            );

            SetConnectionState(DeviceConnectionState.Connected);

            StartHeartbeatMonitor();

            Debug.WriteLine(
                $"[BleSession:{DeviceId}] Remembered device connected and GATT initialized."
            );
        }
        catch
        {
            _intentionalDisconnect = true;

            try
            {
                await StopNotificationsAsync();

                IDevice? failedDevice = _device;

                if (failedDevice is not null)
                {
                    try
                    {
                        await _adapter.DisconnectDeviceAsync(
                            failedDevice
                        );
                    }
                    catch
                    {
                        // Preserve the original connection exception.
                    }
                }
            }
            finally
            {
                ClearConnectionReferences();

                _intentionalDisconnect = false;

                SetConnectionState(DeviceConnectionState.Error);
            }

            throw;
        }
        finally
        {
            _gattSemaphore.Release();
        }
    }

    // ========================================================
    // GATT INITIALIZATION
    // ========================================================

    private async Task InitializeGattAsync(
        IDevice device,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // ----------------------------------------------------
        // NEGOTIATE BLE MTU
        // ----------------------------------------------------

#if ANDROID
        try
        {
            int negotiatedMtu =
                await device.RequestMtuAsync(100);

            Debug.WriteLine(
                $"[BleSession:{DeviceId}] MTU={negotiatedMtu}"
            );
        }
        catch (Exception ex)
        {
            // Continue using the negotiated/default MTU.
            Debug.WriteLine(
                $"[BleSession:{DeviceId}] MTU negotiation failed: {ex.Message}"
            );
        }
#endif

        cancellationToken.ThrowIfCancellationRequested();

        // ----------------------------------------------------
        // DISCOVER SERVICE
        // ----------------------------------------------------

        _service = await device.GetServiceAsync(
            _protocol.ServiceUuid
        );

        if (_service is null)
        {
            throw new InvalidOperationException(
                $"{ProductName} BLE service was not found."
            );
        }

        // ----------------------------------------------------
        // COMMAND CHARACTERISTIC
        // ----------------------------------------------------

        _commandCharacteristic =
            await _service.GetCharacteristicAsync(
                _protocol.CommandCharacteristicUuid
            );

        if (_commandCharacteristic is null)
        {
            throw new InvalidOperationException(
                $"{ProductName} command characteristic was not found."
            );
        }

        // ----------------------------------------------------
        // STATUS CHARACTERISTIC
        // ----------------------------------------------------

        if (_protocol.StatusCharacteristicUuid.HasValue)
        {
            _statusCharacteristic =
                await _service.GetCharacteristicAsync(
                    _protocol.StatusCharacteristicUuid.Value
                );

            if (_statusCharacteristic is null)
            {
                throw new InvalidOperationException(
                    $"{ProductName} status characteristic was not found."
                );
            }

            _statusUpdatedHandler = OnStatusCharacteristicUpdated;

            _statusCharacteristic.ValueUpdated +=
                _statusUpdatedHandler;

            await _statusCharacteristic.StartUpdatesAsync();

            // Read the current status immediately after subscribing.
            try
            {
                var result =
                    await _statusCharacteristic.ReadAsync();

                if (result.resultCode == 0)
                {
                    PublishStatus(result.data);
                }
                else
                {
                    Debug.WriteLine(
                        $"[BleSession:{DeviceId}] Initial status read failed: " +
                        $"{result.resultCode}"
                    );
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[BleSession:{DeviceId}] Initial status read error: {ex.Message}"
                );
            }
        }

        // ----------------------------------------------------
        // WIFI PROVISIONING CHARACTERISTIC
        // ----------------------------------------------------

        if (_protocol.WifiProvisioningCharacteristicUuid.HasValue)
        {
            _wifiProvisioningCharacteristic =
                await _service.GetCharacteristicAsync(
                    _protocol.WifiProvisioningCharacteristicUuid.Value
                );

            if (_wifiProvisioningCharacteristic is null)
            {
                throw new InvalidOperationException(
                    $"{ProductName} Wi-Fi provisioning characteristic was not found."
                );
            }

            _wifiProvisioningUpdatedHandler =
                OnWifiProvisioningCharacteristicUpdated;

            _wifiProvisioningCharacteristic.ValueUpdated +=
                _wifiProvisioningUpdatedHandler;

            await _wifiProvisioningCharacteristic.StartUpdatesAsync();

            // Read the current provisioning status.
            try
            {
                var result =
                    await _wifiProvisioningCharacteristic.ReadAsync();

                if (result.resultCode == 0)
                {
                    PublishProvisioningStatus(result.data);
                }
                else
                {
                    Debug.WriteLine(
                        $"[BleSession:{DeviceId}] Initial provisioning read failed: " +
                        $"{result.resultCode}"
                    );
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[BleSession:{DeviceId}] Initial provisioning read error: {ex.Message}"
                );
            }
        }
    }

    // ========================================================
    // COMMAND WRITE
    // ========================================================

    public async Task WriteCommandAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        byte[] payload = _protocol.EncodeCommand(command);

        await WriteToCharacteristicAsync(
            () => _commandCharacteristic,
            payload,
            "command",
            cancellationToken
        );
    }

    // ========================================================
    // WIFI PROVISIONING WRITE
    // ========================================================

    public async Task WriteWifiProvisioningFrameAsync(
        string frame,
        CancellationToken cancellationToken = default)
    {
        if (!_protocol.WifiProvisioningCharacteristicUuid.HasValue)
        {
            throw new NotSupportedException(
                $"{ProductName} does not support Wi-Fi provisioning."
            );
        }

        byte[] payload =
            _protocol.EncodeProvisioningFrame(frame);

        await WriteToCharacteristicAsync(
            () => _wifiProvisioningCharacteristic,
            payload,
            "Wi-Fi provisioning",
            cancellationToken
        );
    }

    // ========================================================
    // SERIALIZED GATT WRITE
    // ========================================================

    private async Task WriteToCharacteristicAsync(
        Func<ICharacteristic?> characteristicProvider,
        byte[] payload,
        string operationName,
        CancellationToken cancellationToken)
    {
        await _gattSemaphore.WaitAsync(cancellationToken);

        try
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException(
                    $"{ProductName} is not connected."
                );
            }

            ICharacteristic characteristic =
                characteristicProvider()
                ?? throw new InvalidOperationException(
                    $"The {operationName} characteristic is unavailable."
                );

            int resultCode =
                await MainThread.InvokeOnMainThreadAsync(
                    () => characteristic.WriteAsync(payload)
                );

            if (resultCode != 0)
            {
                throw new IOException(
                    $"BLE {operationName} write failed. " +
                    $"GATT result code: {resultCode}"
                );
            }
        }
        finally
        {
            _gattSemaphore.Release();
        }
    }

    // ========================================================
    // DISCONNECT
    // ========================================================

    public async Task DisconnectAsync(
        CancellationToken cancellationToken = default)
    {
        StopHeartbeatMonitor();

        await _gattSemaphore.WaitAsync(cancellationToken);

        try
        {
            IDevice? device = _device;

            if (device is null)
            {
                SetConnectionState(
                    DeviceConnectionState.Disconnected
                );

                return;
            }

            _intentionalDisconnect = true;

            try
            {
                await StopNotificationsAsync();

                await _adapter.DisconnectDeviceAsync(device);
            }
            finally
            {
                ClearConnectionReferences();

                SetConnectionState(
                    DeviceConnectionState.Disconnected
                );

                _intentionalDisconnect = false;
            }

            Debug.WriteLine(
                $"[BleSession:{DeviceId}] Disconnected."
            );
        }
        finally
        {
            _gattSemaphore.Release();
        }
    }

    // ========================================================
    // STOP NOTIFICATIONS
    // ========================================================

    private async Task StopNotificationsAsync()
    {
        ICharacteristic? status = _statusCharacteristic;

        if (status is not null)
        {
            if (_statusUpdatedHandler is not null)
            {
                status.ValueUpdated -= _statusUpdatedHandler;
            }

            try
            {
                await status.StopUpdatesAsync();
            }
            catch
            {
                // The link may already have been lost.
            }
        }

        ICharacteristic? provisioning =
            _wifiProvisioningCharacteristic;

        if (provisioning is not null)
        {
            if (_wifiProvisioningUpdatedHandler is not null)
            {
                provisioning.ValueUpdated -=
                    _wifiProvisioningUpdatedHandler;
            }

            try
            {
                await provisioning.StopUpdatesAsync();
            }
            catch
            {
                // The link may already have been lost.
            }
        }
    }

    // ========================================================
    // HANDLE STATUS NOTIFICATIONS
    // ========================================================

    private void OnStatusCharacteristicUpdated(
        object? sender,
        CharacteristicUpdatedEventArgs args)
    {
        PublishStatus(args.Characteristic.Value);
    }

    private void PublishStatus(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return;

        Interlocked.Exchange(
            ref _lastStatusReceivedUtcTicks,
            DateTime.UtcNow.Ticks
        );

        string payload = Encoding.UTF8.GetString(bytes)
            .Trim('\0', '\r', '\n', ' ');

        if (string.IsNullOrWhiteSpace(payload))
            return;

        _lastStatusPayload = payload;

        StatusPayloadReceived?.Invoke(this, payload);
    }

    // ========================================================
    // HANDLE WIFI PROVISIONING NOTIFICATIONS
    // ========================================================

    private void OnWifiProvisioningCharacteristicUpdated(
        object? sender,
        CharacteristicUpdatedEventArgs args)
    {
        PublishProvisioningStatus(args.Characteristic.Value);
    }

    private void PublishProvisioningStatus(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return;

        string payload = Encoding.UTF8.GetString(bytes)
            .Trim('\0', '\r', '\n', ' ');

        if (string.IsNullOrWhiteSpace(payload))
            return;

        _lastProvisioningStatusPayload = payload;

        ProvisioningStatusPayloadReceived?.Invoke(
            this,
            payload
        );
    }

    // ========================================================
    // ADAPTER DISCONNECTION EVENT
    // ========================================================

    private void OnAdapterDeviceDisconnected(
        object? sender,
        DeviceEventArgs args)
    {
        if (_device?.Id != args.Device.Id)
            return;

        if (_intentionalDisconnect)
            return;

        _ = HandleUnexpectedDisconnectionAsync(args.Device.Id);
    }

    private async Task HandleUnexpectedDisconnectionAsync(Guid deviceId)
    {
        try
        {
            await _gattSemaphore.WaitAsync();

            try
            {
                if (_device?.Id != deviceId ||
                    _intentionalDisconnect)
                {
                    return;
                }

                StopHeartbeatMonitor();

                await StopNotificationsAsync();

                ClearConnectionReferences();

                SetConnectionState(
                    DeviceConnectionState.Disconnected
                );

                Debug.WriteLine(
                    $"[BleSession:{DeviceId}] Unexpected disconnection."
                );
            }
            finally
            {
                _gattSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BleSession:{DeviceId}] Disconnect event handling failed: {ex.Message}"
            );
        }
    }

    // ========================================================
    // CONNECTION HEARTBEAT
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
        CancellationTokenSource? source =
            Interlocked.Exchange(
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
                // Monitor already completed.
            }
        }
    }

    private async Task MonitorHeartbeatAsync(
        CancellationTokenSource source)
    {
        try
        {
            using var timer =
                new PeriodicTimer(TimeSpan.FromSeconds(1));

            while (await timer.WaitForNextTickAsync(source.Token))
            {
                if (!IsConnected)
                    return;

                if (_protocol.StatusCharacteristicUuid is null)
                    return;

                long lastTicks =
                    Interlocked.Read(
                        ref _lastStatusReceivedUtcTicks
                    );

                if (lastTicks == 0)
                    continue;

                long elapsedTicks =
                    DateTime.UtcNow.Ticks - lastTicks;

                if (elapsedTicks >= StatusTimeout.Ticks)
                {
                    Debug.WriteLine(
                        $"[BleSession:{DeviceId}] Status heartbeat timeout."
                    );

                    await HandleHeartbeatTimeoutAsync(
                        source.Token
                    );

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during disconnect or session disposal.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BleSession:{DeviceId}] Heartbeat monitor error: {ex.Message}"
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

    private async Task HandleHeartbeatTimeoutAsync(
        CancellationToken cancellationToken)
    {
        IDevice? lostDevice = null;

        await _gattSemaphore.WaitAsync(cancellationToken);

        try
        {
            if (!IsConnected)
                return;

            long lastTicks =
                Interlocked.Read(
                    ref _lastStatusReceivedUtcTicks
                );

            if (lastTicks != 0 &&
                DateTime.UtcNow.Ticks - lastTicks < StatusTimeout.Ticks)
            {
                return;
            }

            lostDevice = _device;

            await StopNotificationsAsync();

            ClearConnectionReferences();

            SetConnectionState(
                DeviceConnectionState.Disconnected
            );
        }
        finally
        {
            _gattSemaphore.Release();
        }

        if (lostDevice is not null)
        {
            try
            {
                await _adapter.DisconnectDeviceAsync(lostDevice);
            }
            catch
            {
                // The peripheral may already be unavailable.
            }
        }
    }

    // ========================================================
    // CLEANUP
    // ========================================================

    private void ClearConnectionReferences()
    {
        StopHeartbeatMonitor();

        Interlocked.Exchange(
            ref _lastStatusReceivedUtcTicks,
            0
        );

        _device = null;
        _service = null;

        _commandCharacteristic = null;
        _statusCharacteristic = null;
        _wifiProvisioningCharacteristic = null;

        _statusUpdatedHandler = null;
        _wifiProvisioningUpdatedHandler = null;

        _lastStatusPayload = null;
        _lastProvisioningStatusPayload = null;
    }

    private void SetConnectionState(DeviceConnectionState state)
    {
        if (_connectionState == state)
            return;

        _connectionState = state;

        StateChanged?.Invoke(this);
    }

    // ========================================================
    // DISPOSE
    // ========================================================

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            await DisconnectAsync();
        }
        finally
        {
            _adapter.DeviceDisconnected -=
                OnAdapterDeviceDisconnected;

            _disposed = true;

            _gattSemaphore.Dispose();

            StateChanged = null;
            StatusPayloadReceived = null;
            ProvisioningStatusPayloadReceived = null;
        }
    }
}