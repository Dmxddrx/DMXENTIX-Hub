
using System.Collections.ObjectModel;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using ModelDeviceInfo = EMOTIX360.App.Models.DeviceInfo;
using EMOTIX360.App.Services;

namespace EMOTIX360.App.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private readonly BluetoothService _bluetooth;

    private bool _isScanning;
    private bool _isConnecting;
    private bool _isConnected;
    private bool _isAutoConnecting;

    private bool _scanStoppedForConnection;

    private ModelDeviceInfo? _selectedDevice;

    private string _statusMessage =
        "Scan to find your nearby EMOTIX360.";

    public DeviceViewModel(BluetoothService bluetooth)
    {
        _bluetooth = bluetooth;

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

        _bluetooth.DeviceDiscovered += OnDeviceDiscovered;

        _bluetooth.ConnectionChanged += OnConnectionChanged;

        _bluetooth.ConnectionProgressChanged +=
            OnConnectionProgressChanged;

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
        IsScanning
            ? "SCANNING..."
            : IsAutoConnecting
                ? "AUTOMATIC SEARCH ACTIVE"
                : "SCAN FOR EMOTIX360";

    public bool CanScan =>
        !IsScanning &&
        !IsConnecting &&
        !IsConnected &&
        !IsAutoConnecting;

    public bool CanConnectSelected =>
        SelectedDevice is not null &&
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
                StatusMessage = Devices.Count > 0
                    ? $"Found {Devices.Count} EMOTIX360 device(s). Select one to connect."
                    : "No EMOTIX360 device found. Check that the ESP32-S3 is powered on and advertising.";
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

            StatusMessage = connected
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
