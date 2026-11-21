using System.Collections.ObjectModel;
using System.Diagnostics;
using DMXENTIX.Hub.Models.Devices;
using DMXENTIX.Hub.Services;
using DMXENTIX.Hub.Services.Bluetooth;
using DMXENTIX.Hub.Services.Devices;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace DMXENTIX.Hub.ViewModels;

public sealed class DeviceHubViewModel : ObservableObject
{
    private readonly BluetoothService _bluetooth;
    private readonly DeviceDiscoveryService _deviceDiscovery;
    private readonly BleConnectionManager _connectionManager;
    private readonly DeviceRegistryService _deviceRegistry;
    private readonly DeviceSessionSelectionService _sessionSelection;

    // Prevent simultaneous writes when more than one session connects.
    private readonly SemaphoreSlim _registryUpdateSemaphore = new(1, 1);

    private bool _isInitialized;
    private bool _isScanning;
    private bool _isConnecting;
    private bool _isBluetoothOn;

    private int _savedDeviceCount;

    private string _statusMessage =
        "Preparing the device manager...";

    public DeviceHubViewModel(
        BluetoothService bluetooth,
        DeviceDiscoveryService deviceDiscovery,
        BleConnectionManager connectionManager,
        DeviceRegistryService deviceRegistry,
        DeviceSessionSelectionService sessionSelection)
    {
        _bluetooth = bluetooth;
        _deviceDiscovery = deviceDiscovery;
        _connectionManager = connectionManager;
        _deviceRegistry = deviceRegistry;
        _sessionSelection = sessionSelection;

        IsBluetoothOn = _bluetooth.IsBluetoothOn;

        // ----------------------------------------------------
        // COMMANDS
        // ----------------------------------------------------

        ScanCommand = new Command(
            async () => await ScanAsync(requestEnableWhenOff: true),
            () => CanScan
        );

        ConnectDeviceCommand = new Command<DiscoveredDeviceInfo>(
            async device => await ConnectDeviceAsync(device),
            device => device is not null && CanConnectDevices
        );

        SelectSessionCommand = new Command<IDeviceSession>(
            SelectSession,
            session => session is not null && session.IsConnected
        );

        OpenMyDevicesCommand = new Command(
            async () => await OpenMyDevicesAsync()
        );

        // ----------------------------------------------------
        // DEVICE DISCOVERY EVENTS
        // ----------------------------------------------------

        _deviceDiscovery.DevicesChanged +=
            OnDiscoveredDevicesChanged;

        // ----------------------------------------------------
        // CONNECTION MANAGER EVENTS
        // ----------------------------------------------------

        _connectionManager.SessionAdded +=
            OnSessionCollectionChanged;

        _connectionManager.SessionRemoved +=
            OnSessionCollectionChanged;

        _connectionManager.SessionStateChanged +=
            OnSessionStateChanged;

        // ----------------------------------------------------
        // BLUETOOTH COMPATIBILITY EVENTS
        // ----------------------------------------------------

        _bluetooth.BluetoothPowerStateChanged +=
            OnBluetoothPowerStateChanged;

        _bluetooth.ConnectionProgressChanged +=
            OnConnectionProgressChanged;

        RefreshCommandStates();
    }

    // ========================================================
    // COLLECTIONS
    // ========================================================

    public ObservableCollection<DiscoveredDeviceInfo>
        DiscoveredDevices
    { get; } = new();

    public ObservableCollection<IDeviceSession>
        ConnectedSessions
    { get; } = new();

    // ========================================================
    // COMMANDS
    // ========================================================

    public Command ScanCommand { get; }

    public Command<DiscoveredDeviceInfo> ConnectDeviceCommand { get; }

    public Command<IDeviceSession> SelectSessionCommand { get; }

    public Command OpenMyDevicesCommand { get; }

    // ========================================================
    // BLUETOOTH STATE
    // ========================================================

    public bool IsBluetoothOn
    {
        get => _isBluetoothOn;
        private set
        {
            if (SetProperty(ref _isBluetoothOn, value))
            {
                OnPropertyChanged(nameof(BluetoothStatusText));
                OnPropertyChanged(nameof(ScanButtonText));

                RefreshCommandStates();
            }
        }
    }

    public string BluetoothStatusText =>
        IsBluetoothOn
            ? "BLUETOOTH ON"
            : "BLUETOOTH OFF";

    // ========================================================
    // SCANNING STATE
    // ========================================================

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(ScanButtonText));
                OnPropertyChanged(nameof(NoDiscoveredDevicesText));

                RefreshCommandStates();
            }
        }
    }

    public string ScanButtonText =>
        IsScanning
            ? "SCANNING..."
            : IsBluetoothOn
                ? "SCAN NEARBY DEVICES"
                : "TURN ON BLUETOOTH";

    public bool CanScan =>
        !IsScanning &&
        !IsConnecting;

    // ========================================================
    // CONNECTION STATE
    // ========================================================

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
            {
                OnPropertyChanged(nameof(CanConnectDevices));

                RefreshCommandStates();
            }
        }
    }

    public bool CanConnectDevices =>
        IsBluetoothOn &&
        !IsScanning &&
        !IsConnecting;

    // ========================================================
    // CONNECTED DEVICE SUMMARY
    // ========================================================

    public bool HasConnectedSessions =>
        ConnectedSessions.Count > 0;

    public bool HasNoConnectedSessions =>
        !HasConnectedSessions;

    public string ConnectedSessionCountText =>
        $"{ConnectedSessions.Count} connected";

    // ========================================================
    // DISCOVERY SUMMARY
    // ========================================================

    public bool HasNoDiscoveredDevices =>
        DiscoveredDevices.Count == 0;

    public string DiscoveredDeviceCountText =>
        $"{DiscoveredDevices.Count} found";

    public string NoDiscoveredDevicesText =>
        IsScanning
            ? "Searching for supported devices..."
            : "No supported devices found. Scan to search again.";

    // ========================================================
    // SAVED DEVICE SUMMARY
    // ========================================================

    public int SavedDeviceCount
    {
        get => _savedDeviceCount;
        private set
        {
            if (SetProperty(ref _savedDeviceCount, value))
            {
                OnPropertyChanged(nameof(MyDevicesButtonText));
            }
        }
    }

    public string MyDevicesButtonText =>
        SavedDeviceCount > 0
            ? $"MY DEVICES ({SavedDeviceCount})"
            : "MY DEVICES";

    // ========================================================
    // STATUS MESSAGE
    // ========================================================

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    // ========================================================
    // INITIALIZE DEVICE MANAGER
    // ========================================================

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            RefreshConnectedSessions();
            RefreshDiscoveredDevices();

            return;
        }

        _isInitialized = true;

        // ----------------------------------------------------
        // LOAD PERSISTENT DEVICE REGISTRY
        // ----------------------------------------------------

        try
        {
            await _deviceRegistry.InitializeAsync();

            IReadOnlyList<DeviceRecord> savedDevices =
                await _deviceRegistry.GetDevicesAsync();

            SavedDeviceCount = savedDevices.Count;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[DevicesViewModel] Registry initialization failed: {ex.Message}"
            );
        }

        // ----------------------------------------------------
        // RESTORE CURRENT CONNECTION MANAGER STATE
        // ----------------------------------------------------

        RefreshConnectedSessions();
        RefreshDiscoveredDevices();

        // Save any sessions that connected before this page
        // finished initializing.
        foreach (IDeviceSession session in _connectionManager.Sessions)
        {
            if (session.IsConnected)
            {
                await TrySaveConnectedSessionAsync(session);
            }
        }

        // ----------------------------------------------------
        // AUTOMATIC STARTUP DISCOVERY
        // ----------------------------------------------------

        // Do not automatically show the Android Bluetooth
        // activation dialog during startup.
        // The scan button handles that explicit user action.
        await ScanAsync(requestEnableWhenOff: false);
    }

    // ========================================================
    // SCAN FOR SUPPORTED DEVICES
    // ========================================================

    private async Task ScanAsync(bool requestEnableWhenOff)
    {
        if (!CanScan)
            return;

        if (!_bluetooth.IsBluetoothOn)
        {
            if (!requestEnableWhenOff)
            {
                IsBluetoothOn = false;

                StatusMessage =
                    "Bluetooth is off. Turn it on to discover nearby devices.";

                return;
            }

            try
            {
                bool enabled =
                    await _bluetooth.RequestEnableBluetoothAsync();

                if (!enabled)
                {
                    StatusMessage =
                        "Bluetooth was not enabled. Tap the scan button to try again.";

                    return;
                }
            }
            catch (Exception ex)
            {
                StatusMessage =
                    $"Could not enable Bluetooth: {ex.Message}";

                return;
            }
        }

        // BluetoothPowerStateChanged may have started a scan
        // while the Android activation request was completing.
        if (!CanScan)
            return;

        IsBluetoothOn = _bluetooth.IsBluetoothOn;

        if (!IsBluetoothOn)
            return;

        IsScanning = true;

        StatusMessage =
            "Scanning for supported nearby devices...";

        try
        {
            // BluetoothService forwards this request to the
            // shared BleCentralService scanner.
            // The central scanner serializes concurrent scans.
            await _bluetooth.ScanAsync(
                TimeSpan.FromSeconds(10)
            );

            RefreshDiscoveredDevices();

            StatusMessage =
                DiscoveredDevices.Count > 0
                    ? $"Found {DiscoveredDevices.Count} supported device(s). Select a device to connect."
                    : "No supported devices found. Check that your device is powered on and advertising.";
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage =
                $"Bluetooth permission denied: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusMessage =
                $"Device discovery failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;

            RefreshDiscoveredDevices();
            RefreshCommandStates();
        }
    }

    // ========================================================
    // CONNECT TO A DISCOVERED DEVICE
    // ========================================================

    private async Task ConnectDeviceAsync(
        DiscoveredDeviceInfo? discovered)
    {
        if (discovered is null ||
            !CanConnectDevices)
        {
            return;
        }

        // The device may have connected through automatic
        // reconnection after it was discovered.
        IDeviceSession? existingSession =
            _connectionManager.GetSession(discovered.Id);

        if (existingSession?.IsConnected == true)
        {
            SelectSession(existingSession);

            bool saved =
                await TrySaveConnectedSessionAsync(existingSession);

            StatusMessage = saved
                ? $"{existingSession.ProductName} is already connected and saved."
                : $"{existingSession.ProductName} is connected, but its saved record could not be updated.";

            return;
        }

        IsConnecting = true;

        StatusMessage =
            $"Connecting to {discovered.ProductName}...";

        try
        {
            // Keep the existing Android foreground-service
            // and connection compatibility workflow.
            await _bluetooth.ConnectAsync(discovered.Id);

            IDeviceSession? session =
                _connectionManager.GetSession(discovered.Id);

            if (session is null || !session.IsConnected)
            {
                throw new InvalidOperationException(
                    "The connection workflow completed without an active device session."
                );
            }

            // Select the newly connected session for the
            // Home and provisioning ViewModels.
            _sessionSelection.SetSelectedSession(session);

            RefreshConnectedSessions();
            RefreshDiscoveredDevices();

            bool saved =
                await TrySaveConnectedSessionAsync(session);

            StatusMessage = saved
                ? $"Connected to {session.ProductName}. Device added to My Devices."
                : $"Connected to {session.ProductName}, but its saved device record could not be updated.";
        }
        catch (Exception ex)
        {
            StatusMessage =
                $"Could not connect to {discovered.ProductName}: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;

            RefreshConnectedSessions();
            RefreshDiscoveredDevices();
        }
    }

    // ========================================================
    // SELECT AN EXISTING CONNECTED SESSION
    // ========================================================

    private void SelectSession(IDeviceSession? session)
    {
        if (session is null || !session.IsConnected)
            return;

        _sessionSelection.SetSelectedSession(session);

        StatusMessage =
            $"{session.ProductName} is now the selected device.";
    }

    // ========================================================
    // SAVE CONNECTED DEVICE TO PERSISTENT REGISTRY
    // ========================================================

    private async Task<bool> TrySaveConnectedSessionAsync(
        IDeviceSession session)
    {
        try
        {
            await SaveConnectedSessionAsync(session);

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[DevicesViewModel] Could not save {session.DeviceName}: {ex.Message}"
            );

            return false;
        }
    }

    private async Task SaveConnectedSessionAsync(
        IDeviceSession session)
    {
        await _registryUpdateSemaphore.WaitAsync();

        try
        {
            string bluetoothDeviceId =
                session.DeviceId.ToString("D");

            IReadOnlyList<DeviceRecord> savedDevices =
                await _deviceRegistry.GetDevicesAsync();

            DeviceRecord? existingRecord =
                savedDevices.FirstOrDefault(device =>
                    string.Equals(
                        device.BluetoothDeviceId,
                        bluetoothDeviceId,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

            if (existingRecord is null)
            {
                DiscoveredDeviceInfo? discovered =
                    _deviceDiscovery.DiscoveredDevices
                        .FirstOrDefault(device =>
                            device.Id == session.DeviceId
                        );

                // The transport ID is used as a temporary
                // registry identity until firmware provides
                // a stable product-level device identifier.
                string registryDeviceId =
                    $"ble:{session.DeviceType.ToString().ToLowerInvariant()}:{bluetoothDeviceId}";

                DeviceRecord newRecord = new()
                {
                    DeviceId = registryDeviceId,
                    DeviceType = session.DeviceType,
                    DisplayName = session.ProductName,
                    AdvertisedName =
                        discovered?.AdvertisedName ?? session.DeviceName,
                    BluetoothDeviceId = bluetoothDeviceId,
                    AddedAtUtc = DateTimeOffset.UtcNow,

                    // Per-device automatic reconnection
                    // preferences will be introduced in Step 7.
                    AutoReconnectEnabled = false,

                    IsFavorite = false,

                    Capabilities =
                        discovered?.Capabilities ?? DeviceCapabilities.None
                };

                await _deviceRegistry.AddOrUpdateAsync(newRecord);

                existingRecord = newRecord;
            }

            // Update the persistent connection timestamp.
            await _deviceRegistry.MarkConnectedAsync(
                existingRecord.DeviceId
            );

            IReadOnlyList<DeviceRecord> updatedDevices =
                await _deviceRegistry.GetDevicesAsync();

            SavedDeviceCount = updatedDevices.Count;
        }
        finally
        {
            _registryUpdateSemaphore.Release();
        }
    }

    // ========================================================
    // OPEN MY DEVICES
    // ========================================================

    private async Task OpenMyDevicesAsync()
    {
        Shell? shell = Shell.Current;

        if (shell is null)
            return;

        await shell.GoToAsync("my-devices");
    }

    // ========================================================
    // HANDLE DISCOVERY UPDATES
    // ========================================================

    private void OnDiscoveredDevicesChanged()
    {
        RefreshDiscoveredDevices();
    }

    private void RefreshDiscoveredDevices()
    {
        void Refresh()
        {
            HashSet<Guid> connectedIds =
                _connectionManager.Sessions
                    .Where(session => session.IsConnected)
                    .Select(session => session.DeviceId)
                    .ToHashSet();

            DiscoveredDevices.Clear();

            foreach (DiscoveredDeviceInfo device
                     in _deviceDiscovery.DiscoveredDevices)
            {
                // Connected devices appear in the connected
                // sessions section instead of the discovery list.
                if (connectedIds.Contains(device.Id))
                    continue;

                DiscoveredDevices.Add(device);
            }

            OnPropertyChanged(nameof(HasNoDiscoveredDevices));
            OnPropertyChanged(nameof(DiscoveredDeviceCountText));
            OnPropertyChanged(nameof(NoDiscoveredDevicesText));
        }

        if (MainThread.IsMainThread)
            Refresh();
        else
            MainThread.BeginInvokeOnMainThread(Refresh);
    }

    // ========================================================
    // HANDLE CONNECTION MANAGER UPDATES
    // ========================================================

    private void OnSessionCollectionChanged(IDeviceSession session)
    {
        RefreshConnectedSessions();
        RefreshDiscoveredDevices();
    }

    private void OnSessionStateChanged(IDeviceSession session)
    {
        RefreshConnectedSessions();
        RefreshDiscoveredDevices();

        // Automatically add newly connected sessions to
        // the saved-device registry.
        if (session.IsConnected)
        {
            _ = TrySaveConnectedSessionAsync(session);
        }
    }

    private void RefreshConnectedSessions()
    {
        void Refresh()
        {
            ConnectedSessions.Clear();

            foreach (IDeviceSession session in
                     _connectionManager.Sessions
                         .Where(session => session.IsConnected)
                         .OrderBy(session => session.ProductName)
                         .ThenBy(session => session.DeviceName))
            {
                ConnectedSessions.Add(session);
            }

            OnPropertyChanged(nameof(HasConnectedSessions));
            OnPropertyChanged(nameof(HasNoConnectedSessions));
            OnPropertyChanged(nameof(ConnectedSessionCountText));
        }

        if (MainThread.IsMainThread)
            Refresh();
        else
            MainThread.BeginInvokeOnMainThread(Refresh);
    }

    // ========================================================
    // HANDLE BLUETOOTH POWER CHANGES
    // ========================================================

    private void OnBluetoothPowerStateChanged(bool isOn)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsBluetoothOn = isOn;

            if (!isOn)
            {
                StatusMessage =
                    "Bluetooth is off. Turn it on to discover nearby devices.";
            }
            else
            {
                StatusMessage =
                    "Bluetooth is on. Searching for supported devices...";

                if (_isInitialized)
                {
                    _ = ScanAsync(requestEnableWhenOff: false);
                }
            }

            RefreshConnectedSessions();
            RefreshDiscoveredDevices();
        });
    }

    // ========================================================
    // HANDLE AUTOMATIC CONNECTION PROGRESS
    // ========================================================

    private void OnConnectionProgressChanged(string message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Do not overwrite an active manual scan or
            // manual connection message.
            if (IsScanning || IsConnecting)
                return;

            StatusMessage = message;
        });
    }

    // ========================================================
    // COMMAND STATE UPDATES
    // ========================================================

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanConnectDevices));

        ScanCommand?.ChangeCanExecute();
        ConnectDeviceCommand?.ChangeCanExecute();
        SelectSessionCommand?.ChangeCanExecute();
    }
}