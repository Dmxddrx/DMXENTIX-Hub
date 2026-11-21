using System.Collections.ObjectModel;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using ModelDeviceInfo = EMOTIX360.App.Models.DeviceInfo;
using EMOTIX360.App.Services;
using EMOTIX360.App.Models.Devices;
using EMOTIX360.App.Services.Devices;

namespace EMOTIX360.App.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private readonly BluetoothService _bluetooth;

    private bool _isScanning;
    private bool _isConnecting;
    private bool _isConnected;
    private bool _isAutoConnecting;

    private bool _scanStoppedForConnection;

    private bool _isEnablingBluetooth;

    private ModelDeviceInfo? _selectedDevice;

    private string _statusMessage =
        "Scan to find your nearby EMOTIX360.";

    private readonly DeviceDiscoveryService _deviceDiscovery;

    // ========================================================
    // HANDLE BLUETOOTH POWER STATE
    // ========================================================

    private void OnBluetoothPowerStateChanged(bool isOn)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(ScanButtonText));

            if (!isOn)
            {
                // Immediately remove the old connection indication.
                IsConnected = false;

                // Old scan results must not remain selectable.
                Devices.Clear();

                SelectedDevice = null;

                StatusMessage =
                    "Bluetooth is off. Tap TURN ON BLUETOOTH to continue.";
            }
            else
            {
                StatusMessage =
                    "Bluetooth is on. Searching automatically for EMOTIX360...";
            }

            IsAutoConnecting = _bluetooth.IsAutoConnecting;

            RefreshCommandStates();
        });
    }

    public DeviceViewModel(
        BluetoothService bluetooth,
        DeviceDiscoveryService deviceDiscovery)
    {
        _bluetooth = bluetooth;
        _deviceDiscovery = deviceDiscovery;

        // ----------------------------------------------------
        // COMMANDS
        // ----------------------------------------------------

        ScanCommand = new Command(
            async () => await ScanAsync(),
            () => CanScan
        );

        ConnectSelectedCommand = new Command(
            async () => await ConnectSelectedAsync(),
            () => CanConnectSelected
        );

        DisconnectCommand = new Command(
            async () => await DisconnectAsync(),
            () => IsConnected && !IsConnecting
        );

        // ----------------------------------------------------
        // BLE EVENTS
        // ----------------------------------------------------

        _deviceDiscovery.DeviceDiscovered +=
            OnProfileDeviceDiscovered;

        _bluetooth.ConnectionChanged += OnConnectionChanged;

        _bluetooth.ConnectionProgressChanged +=
            OnConnectionProgressChanged;

        _bluetooth.BluetoothPowerStateChanged +=
            OnBluetoothPowerStateChanged;

        // ----------------------------------------------------
        // RESTORE CURRENT CONNECTION STATE
        // ----------------------------------------------------

        IsConnected = _bluetooth.IsConnected;

        IsAutoConnecting = _bluetooth.IsAutoConnecting;

        if (IsConnected)
        {
            StatusMessage =
                $"Connected to {_bluetooth.ConnectedDeviceName ?? BluetoothService.DeviceName}.";
        }
        else if (!_bluetooth.IsBluetoothOn)
        {
            StatusMessage = OperatingSystem.IsAndroid()
                ? "Bluetooth is off. Turn it on to find EMOTIX360."
                : "Bluetooth is off. Turn on Bluetooth in your device Settings.";
        }
        else if (IsAutoConnecting)
        {
            StatusMessage =
                "Automatically connecting to EMOTIX360...";
        }
        else if (_bluetooth.HasRememberedDevice)
        {
            StatusMessage =
                $"Remembered device: {_bluetooth.RememberedDeviceName ?? BluetoothService.DeviceName}.";
        }
    }

    // ========================================================
    // BLUETOOTH ACTIVATION STATE
    // ========================================================

    public bool IsEnablingBluetooth
    {
        get => _isEnablingBluetooth;
        private set
        {
            if (SetProperty(ref _isEnablingBluetooth, value))
            {
                OnPropertyChanged(nameof(ScanButtonText));

                RefreshCommandStates();
            }
        }
    }

    // ========================================================
    // COLLECTIONS AND COMMANDS
    // ========================================================

    public ObservableCollection<ModelDeviceInfo> Devices { get; } = new();

    public Command ScanCommand { get; }

    public Command ConnectSelectedCommand { get; }

    public Command DisconnectCommand { get; }


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

                RefreshCommandStates();
            }
        }
    }


    // ========================================================
    // MANUAL CONNECTION STATE
    // ========================================================

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
            {
                OnPropertyChanged(nameof(ConnectingText));

                RefreshCommandStates();
            }
        }
    }

    public string ConnectingText =>
        IsConnecting ? "CONNECTING..." : string.Empty;


    // ========================================================
    // AUTOMATIC CONNECTION STATE
    // ========================================================

    public bool IsAutoConnecting
    {
        get => _isAutoConnecting;
        private set
        {
            if (SetProperty(ref _isAutoConnecting, value))
            {
                OnPropertyChanged(nameof(ConnectionText));
                OnPropertyChanged(nameof(ScanButtonText));
                OnPropertyChanged(nameof(AutoConnectionText));

                RefreshCommandStates();
            }
        }
    }

    public string AutoConnectionText =>
        IsAutoConnecting
            ? "AUTOMATIC CONNECTION ACTIVE"
            : string.Empty;


    // ========================================================
    // DEVICE CONNECTION STATE
    // ========================================================

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(ConnectionText));

                RefreshCommandStates();
            }
        }
    }

    public string ConnectionText =>
        IsConnected
            ? "Connected"
            : !_bluetooth.IsBluetoothOn
                ? "Bluetooth off"
                : IsAutoConnecting
                    ? "Connecting automatically"
                    : IsConnecting
                        ? "Connecting..."
                        : "Not connected";


    // ========================================================
    // DEVICE SELECTION
    // ========================================================

    public ModelDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                RefreshCommandStates();
            }
        }
    }


    // ========================================================
    // STATUS MESSAGE
    // ========================================================

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }


    // ========================================================
    // COMMAND AVAILABILITY
    // ========================================================

    public string ScanButtonText =>
        IsEnablingBluetooth
            ? "REQUESTING BLUETOOTH..."
            : IsScanning
                ? "SCANNING..."
                : !_bluetooth.IsBluetoothOn
                    ? OperatingSystem.IsAndroid()
                        ? "TURN ON BLUETOOTH"
                        : "ENABLE BLUETOOTH IN SETTINGS"
                    : IsAutoConnecting
                        ? "AUTOMATIC SEARCH ACTIVE"
                        : "SCAN FOR EMOTIX360";

    public bool CanScan =>
        !IsEnablingBluetooth &&
        !IsScanning &&
        !IsConnecting &&
        !IsConnected &&
        (!_bluetooth.IsAutoConnecting || !_bluetooth.IsBluetoothOn) &&
        (_bluetooth.IsBluetoothOn || OperatingSystem.IsAndroid());

    public bool CanConnectSelected =>
        SelectedDevice is not null &&
        _bluetooth.IsBluetoothOn &&
        !IsScanning &&
        !IsConnecting &&
        !IsConnected &&
        !IsAutoConnecting;


    // ========================================================
    // MANUAL DEVICE SCANNING
    // ========================================================

    private async Task ScanAsync()
    {
        if (!CanScan)
            return;

        // When Bluetooth is off, the scan button becomes
        // a request to enable Bluetooth.
        if (!_bluetooth.IsBluetoothOn)
        {
            await RequestBluetoothEnableAsync();
            return;
        }

        Devices.Clear();

        SelectedDevice = null;

        _scanStoppedForConnection = false;

        IsScanning = true;

        StatusMessage =
            "Searching for EMOTIX360 (10 seconds)...";

        try
        {
            await _bluetooth.ScanAsync(
                TimeSpan.FromSeconds(10)
            );

            // Connecting may have stopped the scan early.
            // In that case, ConnectSelectedAsync owns
            // the status message.
            if (!_scanStoppedForConnection)
            {
                int discoveredCount =
                    _deviceDiscovery.DiscoveredDevices.Count;

                StatusMessage = discoveredCount > 0
                    ? $"Found {discoveredCount} supported EMOTIX360 device(s). Select one to connect."
                    : "No supported EMOTIX360 device found. Check that the ESP32-S3 is powered on and advertising.";
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            if (!_scanStoppedForConnection)
            {
                StatusMessage = ex.Message;
            }
        }
        catch (Exception ex)
        {
            if (!_scanStoppedForConnection)
            {
                StatusMessage =
                    $"Bluetooth scan failed: {ex.Message}";
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    // ========================================================
    // REQUEST BLUETOOTH ACTIVATION
    // ========================================================

    private async Task RequestBluetoothEnableAsync()
    {
        if (IsEnablingBluetooth)
            return;

        IsEnablingBluetooth = true;

        StatusMessage =
            "Waiting for permission to turn on Bluetooth...";

        try
        {
            bool enabled =
                await _bluetooth.RequestEnableBluetoothAsync();

            if (enabled)
            {
                StatusMessage =
                    "Bluetooth enabled. Searching for EMOTIX360...";
            }
            else
            {
                StatusMessage =
                    "Bluetooth was not enabled. Tap TURN ON BLUETOOTH to try again.";
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage =
                $"Bluetooth permission denied: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusMessage =
                $"Could not enable Bluetooth: {ex.Message}";
        }
        finally
        {
            IsEnablingBluetooth = false;
        }
    }


    // ========================================================
    // MANUAL DEVICE CONNECTION
    // ========================================================

    private async Task ConnectSelectedAsync()
    {
        if (SelectedDevice is null ||
            !CanConnectSelected ||
            IsScanning)
        {
            return;
        }

        _scanStoppedForConnection = IsScanning;

        IsConnecting = true;

        StatusMessage =
            $"Connecting to {SelectedDevice.Name}...";

        try
        {
            await _bluetooth.ConnectAsync(
                SelectedDevice.Id
            );

            IsConnected = true;

            IsAutoConnecting =
                _bluetooth.IsAutoConnecting;

            StatusMessage =
                "Connected. GATT service and characteristics discovered; status notifications enabled.";
        }
        catch (Exception ex)
        {
            IsConnected = false;

            StatusMessage =
                $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }


    // ========================================================
    // MANUAL DISCONNECTION
    // ========================================================

    private async Task DisconnectAsync()
    {
        try
        {
            StatusMessage =
                "Disconnecting from EMOTIX360...";

            await _bluetooth.DisconnectAsync();

            IsConnected = false;

            IsAutoConnecting =
                _bluetooth.IsAutoConnecting;

            StatusMessage =
                "Disconnected from EMOTIX360. Automatic reconnection is disabled for this session.";
        }
        catch (Exception ex)
        {
            StatusMessage =
                $"Disconnect error: {ex.Message}";
        }
    }


    // ========================================================
    // HANDLE DISCOVERED DEVICES
    // ========================================================
    private void OnProfileDeviceDiscovered(
    DiscoveredDeviceInfo device)
    {
        OnDeviceDiscovered(
            new ModelDeviceInfo(
                device.Id,
                device.AdvertisedName,
                device.Rssi
            )
        );
    }

    private void OnDeviceDiscovered(ModelDeviceInfo device)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Ignore discovery updates when a connection
            // is already established.
            if (IsConnected)
                return;

            int existingIndex = -1;

            for (int i = 0; i < Devices.Count; i++)
            {
                if (Devices[i].Id == device.Id)
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                Devices[existingIndex] = device;
            }
            else
            {
                Devices.Add(device);
            }
        });
    }


    // ========================================================
    // HANDLE CONNECTION CHANGES
    // ========================================================

    private void OnConnectionChanged(
        bool connected,
        string? detail)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsConnected = connected;

            IsAutoConnecting =
                _bluetooth.IsAutoConnecting;

            StatusMessage = !_bluetooth.IsBluetoothOn
                ? "Bluetooth is off. Tap TURN ON BLUETOOTH to continue."
                : connected
                    ? $"Connected to {detail ?? BluetoothService.DeviceName}."
                    : detail ?? "Device disconnected.";
        });
    }


    // ========================================================
    // HANDLE AUTOMATIC CONNECTION PROGRESS
    // ========================================================

    private void OnConnectionProgressChanged(string message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsAutoConnecting =
                _bluetooth.IsAutoConnecting;

            // Do not overwrite an established connection
            // or an active manual operation.
            if (IsConnected ||
                IsScanning ||
                IsConnecting)
            {
                return;
            }

            StatusMessage = message;
        });
    }


    // ========================================================
    // REFRESH COMMAND AVAILABILITY
    // ========================================================

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(CanScan));

        OnPropertyChanged(nameof(CanConnectSelected));

        ScanCommand?.ChangeCanExecute();

        ConnectSelectedCommand?.ChangeCanExecute();

        DisconnectCommand?.ChangeCanExecute();
    }
}
