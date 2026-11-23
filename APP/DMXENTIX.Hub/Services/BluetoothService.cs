using System.Diagnostics;
using System.Linq;
using Microsoft.Maui.Storage;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.EventArgs;

using DMXENTIX.Hub.Services.Bluetooth;
using DMXENTIX.Hub.Services.Devices.Profiles;

using ModelDeviceInfo = DMXENTIX.Hub.Models.DeviceInfo;
using AppDeviceType =
    global::DMXENTIX.Hub.Models.Devices.DeviceType;
using DMXENTIX.Hub.Services.Devices;
using Plugin.BLE.Abstractions.Contracts;

using DMXENTIX.Hub.Models.Devices;


#if ANDROID
using DMXENTIX.Hub.Platforms.Android;
#endif

namespace DMXENTIX.Hub.Services;

/// <summary>
/// Temporary compatibility facade for the existing EMOTIX360 UI.
///
/// The BLE connection and all GATT resources are owned by
/// BleConnectionManager and BleDeviceSession.
///
/// This class retains:
/// - Automatic remembered-device reconnection
/// - Manual connection and disconnection
/// - Discovery compatibility events
/// - Bluetooth power activation
/// - Existing UI connection events
///
/// It does not own BLE characteristics, notification handlers,
/// GATT write locks or connection watchdogs.
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

    private const string REMEMBERED_DEVICE_ID_KEY =
        "emotix360_remembered_ble_device_id";

    private const string REMEMBERED_DEVICE_NAME_KEY =
        "emotix360_remembered_ble_device_name";

    private const int AUTO_CONNECT_SCAN_DURATION_SECONDS = 8;

    private readonly BleCentralService _bleCentral;

    private readonly BleConnectionManager _connectionManager;

    private readonly DeviceDiscoveryService _discovery;

    // Serialize manual and automatic connection operations.
    private readonly SemaphoreSlim _connectionOperationSemaphore =
        new(1, 1);

    // Automatic connection task state.
    private readonly object _autoConnectLock = new();

    private CancellationTokenSource? _autoConnectCts;

    // Bluetooth power changes can interrupt the automatic retry delay.
    private readonly SemaphoreSlim _autoConnectWakeSignal =
        new(0, 1);

    // Protect session event subscriptions and connection tracking.
    private readonly object _sessionLock = new();

    private readonly HashSet<Guid> _observedSessionIds = new();

    private readonly HashSet<Guid> _connectedSessionIds = new();

    private readonly DeviceSessionSelectionService _sessionSelection;
    private readonly DeviceConnectionSupervisor _connectionSupervisor;

    private IDeviceSession? _activeSession;

    // A deliberate disconnect suppresses automatic reconnection
    // until the user explicitly starts a new connection workflow.
    private volatile bool _manualDisconnectRequested;

    public BluetoothService(
        BleCentralService bleCentral,
        BleConnectionManager connectionManager,
        DeviceDiscoveryService discovery,
        DeviceSessionSelectionService sessionSelection,
        DeviceConnectionSupervisor connectionSupervisor)
    {
        _bleCentral = bleCentral;
        _connectionManager = connectionManager;
        _discovery = discovery;
        _sessionSelection = sessionSelection;
        _connectionSupervisor = connectionSupervisor;

        // Shared BLE discovery.
        _bleCentral.DeviceDiscovered += OnCentralDeviceDiscovered;

        // Bluetooth power monitoring.
        _bleCentral.BluetoothPowerStateChanged +=
            OnCentralBluetoothPowerStateChanged;

        // Session lifecycle.
        _connectionManager.SessionAdded += 
            OnSessionAdded;
        _connectionManager.SessionRemoved += 
            OnSessionRemoved;
        _connectionManager.SessionStateChanged +=
            OnManagedSessionStateChanged;
        _connectionSupervisor.ProgressChanged +=
            OnSupervisorProgressChanged;

        // Attach to sessions that may already exist.
        foreach (IDeviceSession session in _connectionManager.Sessions)
        {
            OnSessionAdded(session);
        }
    }

    // ========================================================
    // PUBLIC EVENTS
    // ========================================================

    public event Action<ModelDeviceInfo>? DeviceDiscovered;

    // Compatibility event for existing ViewModels.
    public event Action<bool, string?>? ConnectionChanged;

    // Compatibility events for the existing status services.
    public event Action<string>? StatusPayloadReceived;

    public event Action<string>? WifiProvisioningStatusPayloadReceived;

    // Automatic connection progress.
    public event Action<string>? ConnectionProgressChanged;

    // Bluetooth power state.
    public event Action<bool>? BluetoothPowerStateChanged;

    // The currently selected session for the existing single-device UI.
    public event Action<IDeviceSession?>? ActiveSessionChanged;

    // ========================================================
    // ACTIVE SESSION
    // ========================================================

    public IDeviceSession? ActiveSession => _activeSession;

    public bool IsConnected =>
        _bleCentral.IsBluetoothOn &&
        _activeSession?.IsConnected == true;

    public string? ConnectedDeviceName =>
        _activeSession?.DeviceName;

    public string? CurrentWifiProvisioningStatusPayload =>
        _activeSession?.LastProvisioningStatusPayload;

    // ========================================================
    // BLUETOOTH STATE
    // ========================================================

    public bool IsBluetoothOn =>
        _bleCentral.IsBluetoothOn;

    public BluetoothState BluetoothState =>
        _bleCentral.BluetoothState;

    // ========================================================
    // AUTOMATIC CONNECTION STATE
    // ========================================================

    public bool IsAutoConnecting =>
        _connectionSupervisor.IsReconnecting;

    // ========================================================
    // REMEMBERED DEVICE IDENTITY
    // ========================================================

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

    // ========================================================
    // PERMISSIONS AND SCANNING
    // ========================================================

    public Task EnsurePermissionsAsync()
    {
        return _bleCentral.EnsurePermissionsAsync();
    }

    public Task<bool> RequestEnableBluetoothAsync(
        CancellationToken cancellationToken = default)
    {
        return _bleCentral.RequestEnableBluetoothAsync(
            cancellationToken
        );
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

    // ========================================================
    // MANUAL CONNECTION
    // ========================================================

    public async Task ConnectAsync(Guid deviceId)
    {
        _connectionSupervisor.NotifyManualConnect(deviceId);

        // Determine which product is being connected.
        DiscoveredDeviceInfo? discoveredDevice =
            _discovery.DiscoveredDevices.FirstOrDefault(
                device => device.Id == deviceId
            );

        // Stop the legacy automatic connection loop only when
        // the user manually connects an EMOTIX360.
        if (discoveredDevice?.DeviceType == AppDeviceType.Emotix360)
        {
            StopAutoConnectLoop(manualDisconnect: false);

            // Only an explicit EMOTIX360 connection should
            // clear its manual-disconnect protection.
            _manualDisconnectRequested = false;
        }

        await _connectionOperationSemaphore.WaitAsync();

        try
        {
            await EnsurePermissionsAsync();

            await StopScanAsync();

#if ANDROID
            _connectionSupervisor.EnsureForegroundServiceRunning();
#endif

            try
            {
                IDeviceSession session =
                    await _connectionManager.ConnectAsync(deviceId);

                // The legacy active-session and remembered-device workflow
                // belongs exclusively to EMOTIX360.
                if (session.DeviceType == AppDeviceType.Emotix360)
                {
                    SetActiveSession(session);

                    SaveRememberedDevice(
                        session.DeviceId,
                        session.DeviceName
                    );
                }
                else
                {
                    Debug.WriteLine(
                        $"[BLE] Connected {session.ProductName} through generic device session. " +
                        "EMOTIX360 remembered-device identity was preserved."
                    );
                }

                Debug.WriteLine(
                    $"[BLE] Session connected: {session.DeviceName}, " +
                    $"ID: {session.DeviceId}"
                );
            }
            catch
            {
#if ANDROID
                if (_connectionManager.ConnectedSessionCount == 0)
                {
                    _connectionSupervisor.UpdateForegroundServiceLifecycle();
                }
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
    // CONNECT A SAVED DEVICE
    // ========================================================

    public async Task<IDeviceSession> ConnectKnownDeviceAsync(
        Guid deviceId,
        AppDeviceType deviceType,
        string productName,
        CancellationToken cancellationToken = default)
    {
        _connectionSupervisor.NotifyManualConnect(deviceId);

        // The legacy automatic reconnection workflow belongs
        // exclusively to EMOTIX360.
        if (deviceType == AppDeviceType.Emotix360)
        {
            StopAutoConnectLoop(manualDisconnect: false);

            _manualDisconnectRequested = false;
        }

        await _connectionOperationSemaphore.WaitAsync(
            cancellationToken
        );

        try
        {
            await EnsurePermissionsAsync();

            // Ensure a discovery operation is not holding
            // the shared BLE adapter.
            await StopScanAsync();

#if ANDROID
            _connectionSupervisor.EnsureForegroundServiceRunning();
#endif

            try
            {
                IDeviceSession session =
                    await _connectionManager.ConnectKnownDeviceAsync(
                        deviceId,
                        deviceType,
                        productName,
                        cancellationToken
                    );

                // Only EMOTIX360 owns the legacy active-session
                // and remembered-device compatibility workflow.
                if (session.DeviceType == AppDeviceType.Emotix360)
                {
                    SetActiveSession(session);

                    SaveRememberedDevice(
                        session.DeviceId,
                        session.DeviceName
                    );
                }
                else
                {
                    Debug.WriteLine(
                        $"[BLE] Saved device connected: {session.ProductName}, " +
                        $"ID: {session.DeviceId}"
                    );
                }

                return session;
            }
            catch
            {
#if ANDROID
                if (_connectionManager.ConnectedSessionCount == 0)
                {
                    _connectionSupervisor.UpdateForegroundServiceLifecycle();
                }
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
    // MANUAL DISCONNECTION
    // ========================================================

    public async Task DisconnectAsync()
    {
        // Stop automatic reconnection before disconnecting.
        StopAutoConnectLoop(manualDisconnect: true);

        await _connectionOperationSemaphore.WaitAsync();

        try
        {
            IDeviceSession? session = _activeSession;

            if (session is null)
                return;

            _connectionSupervisor.NotifyManualDisconnect(
                session.DeviceId
            );

            await _connectionManager.DisconnectAsync(
                session.DeviceId
            );
        }
        finally
        {
            _connectionOperationSemaphore.Release();

#if ANDROID
            // Do not stop the foreground service if another
            // device session remains connected.
            if (_connectionManager.ConnectedSessionCount == 0)
            {
                _connectionSupervisor.UpdateForegroundServiceLifecycle();
            }
#endif
        }
    }

    // ========================================================
    // DISCONNECT ONE SPECIFIC DEVICE
    // ========================================================

    public async Task DisconnectDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        IDeviceSession? session =
            _connectionManager.GetSession(deviceId);

        if (session is null)
            return;

        _connectionSupervisor.NotifyManualDisconnect(
            deviceId
        );

        // Suppress automatic EMOTIX360 reconnection before
        // starting its manual disconnection.
        if (session.DeviceType == AppDeviceType.Emotix360)
        {
            StopAutoConnectLoop(manualDisconnect: true);
        }

        await _connectionOperationSemaphore.WaitAsync(
            cancellationToken
        );

        try
        {
            session = _connectionManager.GetSession(deviceId);

            if (session is null || !session.IsConnected)
                return;

            await _connectionManager.DisconnectAsync(
                deviceId,
                cancellationToken
            );

            // Clear the legacy selection only when the exact
            // active EMOTIX360 session was disconnected.
            if (ReferenceEquals(_activeSession, session))
            {
                SetActiveSession(null);
            }
        }
        finally
        {
            _connectionOperationSemaphore.Release();

#if ANDROID
            // Keep the foreground service running while another
            // device remains connected.
            if (_connectionManager.ConnectedSessionCount == 0)
            {
                _connectionSupervisor.UpdateForegroundServiceLifecycle();
            }
#endif
        }
    }

    // ========================================================
    // COMPATIBILITY COMMAND WRITES
    // ========================================================

    // These methods are retained temporarily so the existing
    // DeviceCommandService and WifiProvisioningService continue
    // to work during Stage 3B.1.
    //
    // Stage 3B.2 will change those services to accept an explicit
    // IDeviceSession and remove this global-session convenience path.

    public Task WriteCommandAsync(string command)
    {
        IDeviceSession session = GetConnectedActiveSession();

        return session.WriteCommandAsync(command);
    }

    public Task WriteWifiProvisioningFrameAsync(string frame)
    {
        IDeviceSession session = GetConnectedActiveSession();

        return session.WriteWifiProvisioningFrameAsync(frame);
    }

    private IDeviceSession GetConnectedActiveSession()
    {
        IDeviceSession? session = _activeSession;

        if (session is null || !session.IsConnected)
        {
            throw new InvalidOperationException(
                "No active EMOTIX360 session is connected."
            );
        }

        return session;
    }

    // ========================================================
    // AUTOMATIC CONNECTION COMPATIBILITY
    // ========================================================

    public void StartAutoConnect()
    {
        _ = StartConnectionSupervisorAsync();
    }

    // Called when Android recreates the sticky foreground service.
    // Step 7C will complete the dedicated foreground-service integration.
    internal void ResumeAutoConnectFromForegroundService()
    {
        _ = StartConnectionSupervisorAsync();
    }

    private async Task StartConnectionSupervisorAsync()
    {
        try
        {
            await _connectionSupervisor.StartAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BLE] Could not start connection supervisor: {ex.Message}"
            );
        }
    }

    private void StartAutoConnectCore()
    {
        if (IsConnected || _manualDisconnectRequested)
            return;

        lock (_autoConnectLock)
        {
            if (_autoConnectCts is not null)
                return;

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
        CancellationToken cancellationToken = source.Token;

        int retryAttempt = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   !_manualDisconnectRequested &&
                   !IsConnected)
            {
                try
                {
                    Debug.WriteLine(
                        $"[BLE Diagnostic] Auto-connect iteration. " +
                        $"BluetoothState={BluetoothState}, " +
                        $"IsConnected={IsConnected}"
                    );

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

                        // --------------------------------------------
                        // TRY REMEMBERED DEVICE FIRST
                        // --------------------------------------------

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

                        // --------------------------------------------
                        // FALL BACK TO DISCOVERY
                        // --------------------------------------------

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

                int delaySeconds = Math.Min(
                    15,
                    retryAttempt * 5
                );

                ConnectionProgressChanged?.Invoke(
                    $"EMOTIX360 not found. Retrying in {delaySeconds} seconds..."
                );

                await WaitForAutoConnectRetryAsync(
                    delaySeconds,
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
    // INTERRUPTIBLE RETRY DELAY
    // ========================================================

    private async Task WaitForAutoConnectRetryAsync(
        int delaySeconds,
        CancellationToken cancellationToken)
    {
        using var waitCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );

        Task delayTask = Task.Delay(
            TimeSpan.FromSeconds(delaySeconds),
            waitCts.Token
        );

        Task wakeTask = _autoConnectWakeSignal.WaitAsync(
            waitCts.Token
        );

        Task completedTask = await Task.WhenAny(
            delayTask,
            wakeTask
        );

        waitCts.Cancel();

        await completedTask;

        if (completedTask == wakeTask)
        {
            Debug.WriteLine(
                "[BLE] Automatic retry resumed after Bluetooth state change."
            );
        }
    }

    private void WakeAutoConnectLoop()
    {
        try
        {
            _autoConnectWakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up signal is already pending.
        }
    }

    // ========================================================
    // CONNECT REMEMBERED DEVICE THROUGH A SESSION
    // ========================================================

    private async Task<bool> TryConnectToRememberedDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        await _connectionOperationSemaphore.WaitAsync(
            cancellationToken
        );

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

            attemptCts.CancelAfter(
                TimeSpan.FromSeconds(12)
            );

            Debug.WriteLine(
                $"[BLE] Reconnecting through session manager: {deviceId}"
            );

            IDeviceSession session =
                await _connectionManager.ConnectKnownDeviceAsync(
                    deviceId,
                    AppDeviceType.Emotix360,
                    DeviceName,
                    attemptCts.Token
                );

            SetActiveSession(session);

            SaveRememberedDevice(
                session.DeviceId,
                session.DeviceName
            );

            Debug.WriteLine(
                "[BLE] Remembered-device session reconnected successfully."
            );

            return session.IsConnected;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine(
                "[BLE] Remembered-device session connection timed out."
            );

            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BLE] Remembered-device session connection failed: {ex.Message}"
            );

            return false;
        }
        finally
        {
            _connectionOperationSemaphore.Release();
        }
    }

    // ========================================================
    // SCAN AND CONNECT THROUGH A SESSION
    // ========================================================

    private async Task<bool> TryScanAndConnectAsync(
        CancellationToken cancellationToken)
    {
        await _connectionOperationSemaphore.WaitAsync(
            cancellationToken
        );

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

            DiscoveredDeviceInfo? discovered =
                _discovery.DiscoveredDevices
                    .Where(device =>
                        device.DeviceType == AppDeviceType.Emotix360)
                    .OrderByDescending(device => device.Rssi)
                    .FirstOrDefault();

            if (discovered is null)
            {
                Debug.WriteLine(
                    "[BLE] EMOTIX360 was not found during automatic scan."
                );

                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            Debug.WriteLine(
                $"[BLE] Automatically discovered: {discovered.AdvertisedName}"
            );

            using var attemptCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );

            attemptCts.CancelAfter(
                TimeSpan.FromSeconds(15)
            );

            IDeviceSession session =
                await _connectionManager.ConnectAsync(
                    discovered.Id,
                    attemptCts.Token
                );

            SetActiveSession(session);

            SaveRememberedDevice(
                session.DeviceId,
                session.DeviceName
            );

            Debug.WriteLine(
                "[BLE] Automatic session connection completed successfully."
            );

            return session.IsConnected;
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

    // ========================================================
    // SAVE REMEMBERED DEVICE
    // ========================================================

    private static void SaveRememberedDevice(
        Guid deviceId,
        string deviceName)
    {
        Preferences.Set(
            REMEMBERED_DEVICE_ID_KEY,
            deviceId.ToString("D")
        );

        Preferences.Set(
            REMEMBERED_DEVICE_NAME_KEY,
            string.IsNullOrWhiteSpace(deviceName)
                ? DeviceName
                : deviceName
        );
    }

    // ========================================================
    // DISCOVERY COMPATIBILITY
    // ========================================================

    private void OnCentralDeviceDiscovered(
        object? sender,
        DeviceEventArgs args)
    {
        string? name = args.Device.Name;

        if (!string.Equals(
                name,
                DeviceName,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DeviceDiscovered?.Invoke(
            new ModelDeviceInfo(
                args.Device.Id,
                name,
                args.Device.Rssi
            )
        );
    }

    // ========================================================
    // SESSION REGISTRATION
    // ========================================================

    private void OnSessionAdded(IDeviceSession session)
    {
        lock (_sessionLock)
        {
            if (!_observedSessionIds.Add(session.DeviceId))
                return;
        }

        // Subscribe before the session begins GATT initialization.
        session.StatusPayloadReceived +=
            OnSessionStatusPayloadReceived;

        session.ProvisioningStatusPayloadReceived +=
            OnSessionProvisioningStatusPayloadReceived;

        if (session.DeviceType == AppDeviceType.Emotix360 &&
            (_activeSession is null || !_activeSession.IsConnected))
        {
            SetActiveSession(session);
        }
    }

    private void OnSessionRemoved(IDeviceSession session)
    {
        session.StatusPayloadReceived -=
            OnSessionStatusPayloadReceived;

        session.ProvisioningStatusPayloadReceived -=
            OnSessionProvisioningStatusPayloadReceived;

        lock (_sessionLock)
        {
            _observedSessionIds.Remove(session.DeviceId);
            _connectedSessionIds.Remove(session.DeviceId);
        }

        if (ReferenceEquals(_activeSession, session))
        {
            IDeviceSession? nextSession =
                _connectionManager.Sessions
                    .FirstOrDefault(item =>
                        item.DeviceType == AppDeviceType.Emotix360 &&
                        item.IsConnected
                    );

            SetActiveSession(nextSession);
        }
    }

    // ========================================================
    // SESSION STATE CHANGES
    // ========================================================

    private void OnManagedSessionStateChanged(
        IDeviceSession session)
    {
        bool becameConnected;
        bool becameDisconnected;

        lock (_sessionLock)
        {
            if (session.IsConnected)
            {
                becameConnected =
                    _connectedSessionIds.Add(session.DeviceId);

                becameDisconnected = false;
            }
            else
            {
                becameConnected = false;

                becameDisconnected =
                    _connectedSessionIds.Remove(session.DeviceId);
            }
        }

        if (!ReferenceEquals(session, _activeSession))
            return;

        if (becameConnected)
        {
            Debug.WriteLine(
                $"[BLE] Session connected: {session.DeviceName}"
            );

            ConnectionChanged?.Invoke(
                true,
                session.DeviceName
            );
        }
        else if (becameDisconnected)
        {
            Debug.WriteLine(
                $"[BLE] Session disconnected: {session.DeviceName}"
            );

            ConnectionChanged?.Invoke(
                false,
                "The device disconnected."
            );
        }
    }

    // ========================================================
    // SESSION STATUS FORWARDING
    // ========================================================

    private void OnSessionStatusPayloadReceived(
        IDeviceSession session,
        string payload)
    {
        if (!ReferenceEquals(session, _activeSession))
            return;

        StatusPayloadReceived?.Invoke(payload);
    }

    private void OnSessionProvisioningStatusPayloadReceived(
        IDeviceSession session,
        string payload)
    {
        if (!ReferenceEquals(session, _activeSession))
            return;

        WifiProvisioningStatusPayloadReceived?.Invoke(payload);
    }

    // ========================================================
    // ACTIVE SESSION SELECTION
    // ========================================================

    private void SetActiveSession(IDeviceSession? session)
    {
        if (ReferenceEquals(_activeSession, session))
            return;

        _activeSession = session;

        // Publish the selected session through the shared selection component.
        _sessionSelection.SetSelectedSession(session);

        // Retain the compatibility event for ViewModels not yet migrated.
        ActiveSessionChanged?.Invoke(session);

        if (session is null)
        {
            ConnectionChanged?.Invoke(
                false,
                "No active device session."
            );

            return;
        }

        // Replay the current snapshot when selecting an existing session.\
        if (!string.IsNullOrWhiteSpace(session.LastStatusPayload))
        {
            StatusPayloadReceived?.Invoke(
                session.LastStatusPayload
            );
        }

        if (!string.IsNullOrWhiteSpace(
                session.LastProvisioningStatusPayload))
        {
            WifiProvisioningStatusPayloadReceived?.Invoke(
                session.LastProvisioningStatusPayload
            );
        }

        string? statusPayload = session.LastStatusPayload;

        if (!string.IsNullOrWhiteSpace(statusPayload))
        {
            StatusPayloadReceived?.Invoke(statusPayload);
        }

        string? provisioningPayload =
            session.LastProvisioningStatusPayload;

        if (!string.IsNullOrWhiteSpace(provisioningPayload))
        {
            WifiProvisioningStatusPayloadReceived?.Invoke(
                provisioningPayload
            );
        }

        ConnectionChanged?.Invoke(
            session.IsConnected,
            session.DeviceName
        );
    }

    // ========================================================
    // BLUETOOTH POWER CHANGES
    // ========================================================

    private void OnCentralBluetoothPowerStateChanged(bool isOn)
    {
        Debug.WriteLine(
            $"[BLE] Bluetooth power changed: {(isOn ? "ON" : "OFF")}"
        );

        BluetoothPowerStateChanged?.Invoke(isOn);

        if (!isOn)
        {
            // Update existing single-device UI consumers immediately.
            IDeviceSession? activeSession = _activeSession;

            if (activeSession?.IsConnected == true)
            {
                lock (_sessionLock)
                {
                    // Prevent a duplicate ConnectionChanged event
                    // when the session finishes invalidating itself.
                    _connectedSessionIds.Remove(
                        activeSession.DeviceId
                    );
                }

                ConnectionChanged?.Invoke(
                    false,
                    "Bluetooth was turned off. The device session is disconnected."
                );
            }

            // Wake any pending retry operation.
            WakeAutoConnectLoop();

            // Invalidate all active sessions asynchronously.
            _ = HandleBluetoothUnavailableAsync();

            return;
        }

        // DeviceConnectionSupervisor independently observes Bluetooth
        // power changes and resumes all eligible saved-device connections.
    }

    private async Task HandleBluetoothUnavailableAsync()
    {
        try
        {
            await _connectionManager.HandleBluetoothUnavailableAsync();

            Debug.WriteLine(
                "[BLE] All sessions updated after Bluetooth became unavailable."
            );
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BLE] Bluetooth-off session cleanup failed: {ex.Message}"
            );
        }
    }
    private void OnSupervisorProgressChanged(string message)
    {
        ConnectionProgressChanged?.Invoke(message);
    }
}