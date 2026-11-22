using System.Collections.ObjectModel;
using System.Diagnostics;
using DMXENTIX.Hub.Models.Devices;
using DMXENTIX.Hub.Services;
using DMXENTIX.Hub.Services.Bluetooth;
using DMXENTIX.Hub.Services.Devices;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using System.Collections.Generic;

namespace DMXENTIX.Hub.ViewModels;

public sealed class MyDevicesViewModel : ObservableObject
{
    private readonly DeviceRegistryService _deviceRegistry;
    private readonly BleConnectionManager _connectionManager;

    private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);

    private bool _isRefreshing;
    public bool CanRefresh => !IsRefreshing;
    private string _statusMessage = "Loading saved devices...";

    public MyDevicesViewModel(
        DeviceRegistryService deviceRegistry,
        BleConnectionManager connectionManager)
    {
        _deviceRegistry = deviceRegistry;
        _connectionManager = connectionManager;

        RefreshCommand = new Command(
            async () => await RefreshAsync(),
            () => !IsRefreshing
        );

        ToggleFavoriteCommand = new Command<DeviceCardViewModel>(
            async card => await ToggleFavoriteAsync(card),
            card => card is not null
        );

        OpenWorkspaceCommand = new Command<DeviceCardViewModel>(
            async card => await OpenWorkspaceAsync(card),
            card => card is not null
        );

        // ----------------------------------------------------
        // DEVICE REGISTRY EVENTS
        // ----------------------------------------------------

        _deviceRegistry.DevicesChanged +=
            OnRegistryDevicesChanged;

        // ----------------------------------------------------
        // CONNECTION MANAGER EVENTS
        // ----------------------------------------------------

        _connectionManager.SessionAdded +=
            OnSessionCollectionChanged;

        _connectionManager.SessionRemoved +=
            OnSessionCollectionChanged;

        _connectionManager.SessionStateChanged +=
            OnSessionStateChanged;
    }

    // ========================================================
    // COLLECTION
    // ========================================================

    public ObservableCollection<DeviceCardViewModel> Devices
    {
        get;
    } = new();

    public bool HasDevices =>
        Devices.Count > 0;

    public bool HasNoDevices =>
        Devices.Count == 0;

    public string DeviceCountText =>
        Devices.Count == 1
            ? "1 SAVED DEVICE"
            : $"{Devices.Count} SAVED DEVICES";

    // ========================================================
    // COMMANDS
    // ========================================================

    public Command RefreshCommand { get; }

    public Command<DeviceCardViewModel> ToggleFavoriteCommand { get; }

    public Command<DeviceCardViewModel> OpenWorkspaceCommand { get; }

    // ========================================================
    // REFRESH STATE
    // ========================================================

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RefreshCommand.ChangeCanExecute();
                OnPropertyChanged(nameof(CanRefresh));
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
    // INITIALIZE MY DEVICES
    // ========================================================

    public async Task InitializeAsync()
    {
        try
        {
            await _deviceRegistry.InitializeAsync();

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[MyDevicesViewModel] Initialization failed: {ex.Message}"
            );

            StatusMessage =
                $"Could not initialize My Devices: {ex.Message}";
        }
    }

    // ========================================================
    // REFRESH SAVED DEVICES
    // ========================================================

    private async Task RefreshAsync()
    {
        await _refreshSemaphore.WaitAsync();

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                IsRefreshing = true;
                StatusMessage = "Loading saved devices...";
            });

            IReadOnlyList<DeviceRecord> savedDevices =
                await _deviceRegistry.GetDevicesAsync();

            IDeviceSession[] sessions =
                _connectionManager.Sessions.ToArray();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                Devices.Clear();

                // DeviceRegistryService already returns records
                // sorted by most recent connection first.
                foreach (DeviceRecord record in savedDevices)
                {
                    IDeviceSession? matchingSession =
                        FindMatchingSession(record, sessions);

                    Devices.Add(
                        new DeviceCardViewModel(
                            record,
                            matchingSession
                        )
                    );
                }

                OnPropertyChanged(nameof(HasDevices));
                OnPropertyChanged(nameof(HasNoDevices));
                OnPropertyChanged(nameof(DeviceCountText));

                StatusMessage = savedDevices.Count == 0
                    ? "No saved devices yet. Connect a supported device from Device Hub to add it here."
                    : $"Showing {savedDevices.Count} saved device(s).";
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[MyDevicesViewModel] Refresh failed: {ex.Message}"
            );

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                StatusMessage =
                    $"Could not load saved devices: {ex.Message}";
            });
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                IsRefreshing = false;
            });

            _refreshSemaphore.Release();
        }
    }

    // ========================================================
    // MATCH SAVED RECORDS TO RUNTIME SESSIONS
    // ========================================================

    private static IDeviceSession? FindMatchingSession(
        DeviceRecord record,
        IEnumerable<IDeviceSession> sessions)
    {
        // BluetoothDeviceId is the current transport identity.
        // DeviceId is the separate persistent registry identity.

        if (string.IsNullOrWhiteSpace(record.BluetoothDeviceId))
            return null;

        if (!Guid.TryParse(
                record.BluetoothDeviceId,
                out Guid bluetoothDeviceId))
        {
            return null;
        }

        return sessions.FirstOrDefault(session =>
            session.DeviceId == bluetoothDeviceId
        );
    }

    // ========================================================
    // TOGGLE FAVOURITE
    // ========================================================

    private async Task ToggleFavoriteAsync(
        DeviceCardViewModel? card)
    {
        if (card is null)
            return;

        try
        {
            await _deviceRegistry.SetFavoriteAsync(
                card.DeviceId,
                !card.IsFavorite
            );

            // DevicesChanged also triggers a refresh.
            // Refresh explicitly to keep the button responsive.
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[MyDevicesViewModel] Could not update favourite: {ex.Message}"
            );

            StatusMessage =
                $"Could not update favourite: {ex.Message}";
        }
    }

    // ========================================================
    // HANDLE REGISTRY CHANGES
    // ========================================================

    private void OnRegistryDevicesChanged()
    {
        _ = RefreshAsync();
    }

    // ========================================================
    // HANDLE CONNECTION MANAGER UPDATES
    // ========================================================

    private void OnSessionCollectionChanged(IDeviceSession session)
    {
        _ = RefreshAsync();
    }

    private void OnSessionStateChanged(IDeviceSession session)
    {
        _ = RefreshAsync();
    }

    // ========================================================
    // OPEN THE SELECTED DEVICE WORKSPACE
    // ========================================================

    private async Task OpenWorkspaceAsync(
        DeviceCardViewModel? card)
    {
        if (card is null)
            return;

        Shell? shell = Shell.Current;

        if (shell is null)
            return;

        try
        {
            // Open the selected device directly.
            // Do not navigate through DeviceHubPage.
            await shell.GoToAsync(
                "device-workspace",
                new Dictionary<string, object>
                {
                    ["DeviceId"] = card.DeviceId
                }
            );
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[MyDevicesViewModel] Could not open workspace: {ex.Message}"
            );

            StatusMessage =
                $"Could not open device workspace: {ex.Message}";
        }
    }
}