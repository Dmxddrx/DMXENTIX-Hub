
using System.Collections.Concurrent;
using System.Diagnostics;
using DMXENTIX.Hub.Models.Devices;
using DMXENTIX.Hub.Services.Devices;
using DMXENTIX.Hub.Services;

namespace DMXENTIX.Hub.Services.Bluetooth;

/// <summary>
/// Supervises automatic reconnection for all saved devices.
///
/// DeviceRegistryService owns persistent preferences.
/// BleConnectionManager owns independent BLE sessions.
/// DeviceConnectionSupervisor owns automatic reconnection policy.
///
/// Each saved device has an independent retry task.
/// </summary>
public sealed class DeviceConnectionSupervisor
{
    private static readonly TimeSpan ScanDuration =
        TimeSpan.FromSeconds(8);

    private readonly DeviceRegistryService _deviceRegistry;
    private readonly BleCentralService _bleCentral;
    private readonly DeviceDiscoveryService _deviceDiscovery;
    private readonly BleConnectionManager _connectionManager;

    // One independent reconnect worker per BLE transport ID.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource>
        _reconnectTasks = new();

    // Devices explicitly disconnected by the user must remain disconnected
    // until the user manually reconnects or re-enables Auto Reconnect.
    private readonly HashSet<Guid> _manualDisconnects = new();
    private readonly object _manualDisconnectLock = new();

    // Prevent simultaneous registry reconciliation.
    private readonly SemaphoreSlim _reconcileSemaphore = new(1, 1);

    // Bluetooth permissions are shared by all device workers.
    private readonly SemaphoreSlim _permissionSemaphore = new(1, 1);

    private volatile bool _started;
    private bool _permissionsGranted;

    public DeviceConnectionSupervisor(
        DeviceRegistryService deviceRegistry,
        BleCentralService bleCentral,
        DeviceDiscoveryService deviceDiscovery,
        BleConnectionManager connectionManager)
    {
        _deviceRegistry = deviceRegistry;
        _bleCentral = bleCentral;
        _deviceDiscovery = deviceDiscovery;
        _connectionManager = connectionManager;

        _deviceRegistry.DevicesChanged +=
            OnRegistryDevicesChanged;

        _bleCentral.BluetoothPowerStateChanged +=
            OnBluetoothPowerStateChanged;

        _bleCentral.ScanCompleted +=
            OnScanCompleted;

        _connectionManager.SessionStateChanged +=
            OnSessionStateChanged;
    }

    public event Action<string>? ProgressChanged;

    public bool IsReconnecting =>
        !_reconnectTasks.IsEmpty;

    // ========================================================
    // START SUPERVISOR
    // ========================================================

    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        await _deviceRegistry.InitializeAsync(
            cancellationToken
        );

        _started = true;

        Debug.WriteLine(
            "[ConnectionSupervisor] Started."
        );

        await ReconcileAsync(cancellationToken);
    }

    // ========================================================
    // MANUAL CONNECTION INTENT
    // ========================================================

    /// <summary>
    /// Called before a user-initiated connection.
    /// Cancels an automatic attempt for the same device.
    /// </summary>
    public void NotifyManualConnect(Guid deviceId)
    {
        lock (_manualDisconnectLock)
        {
            _manualDisconnects.Remove(deviceId);
        }

        StopReconnectLoop(deviceId);

        Debug.WriteLine(
            $"[ConnectionSupervisor] Manual connection requested: {deviceId}"
        );
    }

    /// <summary>
    /// Called before a user-initiated disconnection.
    /// Prevents the supervisor from reconnecting that device.
    /// </summary>
    public void NotifyManualDisconnect(Guid deviceId)
    {
        lock (_manualDisconnectLock)
        {
            _manualDisconnects.Add(deviceId);
        }

        StopReconnectLoop(deviceId);

        Debug.WriteLine(
            $"[ConnectionSupervisor] Manual disconnect requested: {deviceId}"
        );

        ProgressChanged?.Invoke(
            "Automatic reconnection disabled for the manually disconnected device."
        );
    }

    // ========================================================
    // RE-ENABLE AUTOMATIC RECONNECTION
    // ========================================================

    /// <summary>
    /// Called when the user explicitly enables Auto Reconnect.
    /// Clears previous manual-disconnect suppression.
    /// </summary>
    public async Task AllowAutoReconnectAsync(
        string registryDeviceId,
        CancellationToken cancellationToken = default)
    {
        DeviceRecord? record =
            await _deviceRegistry.GetDeviceAsync(
                registryDeviceId,
                cancellationToken
            );

        if (record is null ||
            !record.AutoReconnectEnabled ||
            !Guid.TryParse(
                record.BluetoothDeviceId,
                out Guid bluetoothDeviceId))
        {
            return;
        }

        lock (_manualDisconnectLock)
        {
            _manualDisconnects.Remove(bluetoothDeviceId);
        }

        await ReconcileAsync(cancellationToken);
    }

    // ========================================================
    // REGISTRY RECONCILIATION
    // ========================================================

    private async Task ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_started)
            return;

        await _reconcileSemaphore.WaitAsync(
            cancellationToken
        );

        try
        {
            if (!_started)
                return;

            if (!_bleCentral.IsBluetoothOn)
            {
                StopAllReconnectLoops();
                return;
            }

            IReadOnlyList<DeviceRecord> savedDevices =
                await _deviceRegistry.GetDevicesAsync(
                    cancellationToken
                );

            Dictionary<Guid, DeviceRecord> eligibleDevices = new();

            foreach (DeviceRecord record in savedDevices)
            {
                if (!record.AutoReconnectEnabled)
                    continue;

                if (!Guid.TryParse(
                        record.BluetoothDeviceId,
                        out Guid bluetoothDeviceId))
                {
                    Debug.WriteLine(
                        $"[ConnectionSupervisor] Skipping {record.DisplayName}: " +
                        "no valid saved Bluetooth identity."
                    );

                    continue;
                }

                if (IsManualDisconnectSuppressed(bluetoothDeviceId))
                    continue;

                IDeviceSession? existingSession =
                    _connectionManager.GetSession(bluetoothDeviceId);

                if (existingSession?.IsConnected == true)
                {
                    StopReconnectLoop(bluetoothDeviceId);
                    continue;
                }

                eligibleDevices[bluetoothDeviceId] = record;
            }

            // Stop workers for devices that are no longer eligible.
            foreach (Guid runningDeviceId in _reconnectTasks.Keys)
            {
                if (!eligibleDevices.ContainsKey(runningDeviceId))
                {
                    StopReconnectLoop(runningDeviceId);
                }
            }

            if (eligibleDevices.Count == 0)
                return;

            // Request shared BLE permissions once before starting workers.
            try
            {
                await EnsurePermissionsAsync(cancellationToken);
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine(
                    $"[ConnectionSupervisor] Bluetooth permission denied: {ex.Message}"
                );

                ProgressChanged?.Invoke(
                    "Bluetooth permission is required for automatic reconnection."
                );

                return;
            }

            foreach (KeyValuePair<Guid, DeviceRecord> entry
                     in eligibleDevices)
            {
                StartReconnectLoop(
                    entry.Key,
                    entry.Value
                );
            }
        }
        finally
        {
            _reconcileSemaphore.Release();
        }
    }

    private async Task EnsurePermissionsAsync(
        CancellationToken cancellationToken)
    {
        await _permissionSemaphore.WaitAsync(
            cancellationToken
        );

        try
        {
            if (_permissionsGranted)
                return;

            await _bleCentral.EnsurePermissionsAsync();

            _permissionsGranted = true;
        }
        finally
        {
            _permissionSemaphore.Release();
        }
    }

    // ========================================================
    // START ONE DEVICE WORKER
    // ========================================================

    private void StartReconnectLoop(
        Guid bluetoothDeviceId,
        DeviceRecord record)
    {
        if (IsManualDisconnectSuppressed(bluetoothDeviceId))
            return;

        IDeviceSession? session =
            _connectionManager.GetSession(bluetoothDeviceId);

        if (session?.IsConnected == true)
            return;

        var source = new CancellationTokenSource();

        if (!_reconnectTasks.TryAdd(
                bluetoothDeviceId,
                source))
        {
            source.Dispose();
            return;
        }

        Debug.WriteLine(
            $"[ConnectionSupervisor] Starting reconnect worker: " +
            $"{record.DisplayName} ({bluetoothDeviceId})"
        );

        _ = RunReconnectLoopAsync(
            bluetoothDeviceId,
            record.DeviceId,
            source
        );
    }

    // ========================================================
    // INDEPENDENT DEVICE RETRY LOOP
    // ========================================================

    private async Task RunReconnectLoopAsync(
        Guid bluetoothDeviceId,
        string registryDeviceId,
        CancellationTokenSource source)
    {
        CancellationToken cancellationToken = source.Token;

        int retryAttempt = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_bleCentral.IsBluetoothOn ||
                    IsManualDisconnectSuppressed(bluetoothDeviceId))
                {
                    break;
                }

                DeviceRecord? record =
                    await _deviceRegistry.GetDeviceAsync(
                        registryDeviceId,
                        cancellationToken
                    );

                if (record is null ||
                    !record.AutoReconnectEnabled ||
                    !Guid.TryParse(
                        record.BluetoothDeviceId,
                        out Guid currentBluetoothId) ||
                    currentBluetoothId != bluetoothDeviceId)
                {
                    break;
                }

                IDeviceSession? existingSession =
                    _connectionManager.GetSession(bluetoothDeviceId);

                if (existingSession?.IsConnected == true)
                    break;

                Debug.WriteLine(
                    $"[ConnectionSupervisor] Reconnect attempt: " +
                    $"{record.DisplayName}"
                );

                ProgressChanged?.Invoke(
                    $"Automatically reconnecting to {record.DisplayName}..."
                );

                bool connected =
                    await TryConnectSavedDeviceAsync(
                        record,
                        bluetoothDeviceId,
                        cancellationToken
                    );

                if (connected)
                {
                    Debug.WriteLine(
                        $"[ConnectionSupervisor] Reconnected: {record.DisplayName}"
                    );

                    ProgressChanged?.Invoke(
                        $"Reconnected to {record.DisplayName}."
                    );

                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                retryAttempt++;

                // 5, 10, 15, 20, 25, then 30-second intervals.
                int delaySeconds = Math.Min(
                    30,
                    retryAttempt * 5
                );

                Debug.WriteLine(
                    $"[ConnectionSupervisor] {record.DisplayName} unavailable. " +
                    $"Retrying in {delaySeconds} seconds."
                );

                ProgressChanged?.Invoke(
                    $"{record.DisplayName} unavailable. Retrying in {delaySeconds} seconds..."
                );

                await Task.Delay(
                    TimeSpan.FromSeconds(delaySeconds),
                    cancellationToken
                );
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the device preference is disabled,
            // the user disconnects manually or Bluetooth turns off.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[ConnectionSupervisor] Worker failed for {bluetoothDeviceId}: {ex}"
            );
        }
        finally
        {
            if (_reconnectTasks.TryGetValue(
                    bluetoothDeviceId,
                    out CancellationTokenSource? currentSource) &&
                ReferenceEquals(currentSource, source))
            {
                _reconnectTasks.TryRemove(
                    bluetoothDeviceId,
                    out _
                );
            }

            source.Dispose();
        }
    }

    // ========================================================
    // CONNECT SAVED DEVICE
    // ========================================================

    private async Task<bool> TryConnectSavedDeviceAsync(
        DeviceRecord record,
        Guid bluetoothDeviceId,
        CancellationToken cancellationToken)
    {
        // Do not attempt a connection after the user has
        // explicitly disconnected this device.
        if (IsManualDisconnectSuppressed(bluetoothDeviceId))
            return false;

        IDeviceSession? existingSession =
            _connectionManager.GetSession(bluetoothDeviceId);

        if (existingSession?.IsConnected == true)
            return true;

        // ----------------------------------------------------
        // ATTEMPT 1: SAVED BLUETOOTH IDENTITY
        // ----------------------------------------------------

        using (var knownDeviceCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken))
        {
            knownDeviceCts.CancelAfter(
                TimeSpan.FromSeconds(18)
            );

            try
            {
                IDeviceSession session =
                    await _connectionManager.ConnectKnownDeviceAsync(
                        bluetoothDeviceId,
                        record.DeviceType,
                        record.DisplayName,
                        knownDeviceCts.Token
                    );

                if (session.IsConnected)
                    return true;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                Debug.WriteLine(
                    $"[ConnectionSupervisor] Saved-device connection timed out: " +
                    $"{record.DisplayName}"
                );
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[ConnectionSupervisor] Saved-device connection failed: " +
                    $"{record.DisplayName}: {ex.Message}"
                );
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!_bleCentral.IsBluetoothOn)
            return false;

        // ----------------------------------------------------
        // ATTEMPT 2: SCAN FOR THE EXACT SAVED DEVICE
        // ----------------------------------------------------

        try
        {
            Debug.WriteLine(
                $"[ConnectionSupervisor] Scanning for {record.DisplayName}."
            );

            await _bleCentral.ScanAsync(
                ScanDuration,
                cancellationToken
            );

            cancellationToken.ThrowIfCancellationRequested();

            // Match both the saved BLE identity and product type.
            // Do not connect to another device of the same product.
            DiscoveredDeviceInfo? discovered =
                _deviceDiscovery.DiscoveredDevices.FirstOrDefault(
                    device =>
                        device.Id == bluetoothDeviceId &&
                        device.DeviceType == record.DeviceType
                );

            if (discovered is null)
            {
                Debug.WriteLine(
                    $"[ConnectionSupervisor] Exact saved device not found: " +
                    $"{record.DisplayName}"
                );

                return false;
            }

            using var connectCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );

            connectCts.CancelAfter(
                TimeSpan.FromSeconds(18)
            );

            IDeviceSession session =
                await _connectionManager.ConnectAsync(
                    discovered.Id,
                    connectCts.Token
                );

            return session.IsConnected;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine(
                $"[ConnectionSupervisor] Scan connection timed out: " +
                $"{record.DisplayName}"
            );

            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[ConnectionSupervisor] Scan connection failed for " +
                $"{record.DisplayName}: {ex.Message}"
            );

            return false;
        }
    }

    // ========================================================
    // SESSION STATE CHANGES
    // ========================================================

    private void OnSessionStateChanged(
        IDeviceSession session)
    {
        if (session.IsConnected)
        {
            // A successful connection makes its retry worker unnecessary.
            StopReconnectLoop(session.DeviceId);

            _ = UpdateLastConnectedAsync(session);

            return;
        }

        if (session.ConnectionState is
            DeviceConnectionState.Disconnected or
            DeviceConnectionState.Error)
        {
            // Reconcile all saved preferences. The dictionary
            // prevents duplicate workers for the same device.
            _ = ReconcileSafelyAsync();
        }
    }

    private async Task UpdateLastConnectedAsync(
        IDeviceSession session)
    {
        try
        {
            IReadOnlyList<DeviceRecord> savedDevices =
                await _deviceRegistry.GetDevicesAsync();

            string bluetoothId =
                session.DeviceId.ToString("D");

            DeviceRecord? record =
                savedDevices.FirstOrDefault(device =>
                    string.Equals(
                        device.BluetoothDeviceId,
                        bluetoothId,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

            if (record is not null)
            {
                await _deviceRegistry.MarkConnectedAsync(
                    record.DeviceId
                );
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[ConnectionSupervisor] Could not update connection timestamp: {ex.Message}"
            );
        }
    }

    // ========================================================
    // STOP WORKERS
    // ========================================================

    private void StopReconnectLoop(Guid bluetoothDeviceId)
    {
        if (!_reconnectTasks.TryRemove(
                bluetoothDeviceId,
                out CancellationTokenSource? source))
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Worker has already completed.
        }
    }

    private void StopAllReconnectLoops()
    {
        foreach (Guid deviceId in _reconnectTasks.Keys)
        {
            StopReconnectLoop(deviceId);
        }
    }

    // ========================================================
    // MANUAL DISCONNECT TRACKING
    // ========================================================

    private bool IsManualDisconnectSuppressed(Guid deviceId)
    {
        lock (_manualDisconnectLock)
        {
            return _manualDisconnects.Contains(deviceId);
        }
    }

    // ========================================================
    // REGISTRY CHANGES
    // ========================================================

    private void OnRegistryDevicesChanged()
    {
        _ = ReconcileSafelyAsync();
    }

    // ========================================================
    // BLUETOOTH POWER CHANGES
    // ========================================================

    private void OnBluetoothPowerStateChanged(bool isOn)
    {
        Debug.WriteLine(
            $"[ConnectionSupervisor] Bluetooth power changed: " +
            $"{(isOn ? "ON" : "OFF")}"
        );

        if (!isOn)
        {
            StopAllReconnectLoops();
            return;
        }

        _ = ReconcileSafelyAsync();
    }

    // ========================================================
    // SCAN COMPLETED
    // ========================================================

    private void OnScanCompleted()
    {
        // A user-initiated scan may have restored permissions
        // or discovered saved peripherals.
        _ = ReconcileSafelyAsync();
    }

    // ========================================================
    // SAFE RECONCILIATION ENTRY POINT
    // ========================================================

    private async Task ReconcileSafelyAsync()
    {
        try
        {
            await ReconcileAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[ConnectionSupervisor] Reconciliation failed: {ex.Message}"
            );
        }
    }
}
