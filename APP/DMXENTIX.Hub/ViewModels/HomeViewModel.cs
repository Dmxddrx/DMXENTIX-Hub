using System.Collections.ObjectModel;
using System.Diagnostics;
using DMXENTIX.Hub.Models.Devices;
using DMXENTIX.Hub.Services.Bluetooth;
using DMXENTIX.Hub.Services.Devices;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace DMXENTIX.Hub.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    private readonly DeviceRegistryService _deviceRegistry;
    private readonly BleConnectionManager _connectionManager;

    private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);

    // Reuse card objects while devices move between sections.
    private readonly Dictionary<string, DeviceCardViewModel> _cardsByDeviceId =
        new(StringComparer.Ordinal);

    // Track the status subscription for every managed BLE session.
    private readonly Dictionary<Guid, IDeviceSession> _observedSessions = new();

    // Resolve incoming BLE notifications to the correct visible card.
    private readonly Dictionary<Guid, DeviceCardViewModel> _cardsByBluetoothId = new();

    private string _statusMessage = string.Empty;

    private readonly DeviceHubViewModel _deviceHubViewModel;

    private DeviceCardViewModel? _selectedInfoCard;
    private bool _isInfoPopupVisible;

    public HomeViewModel(
        DeviceRegistryService deviceRegistry,
        BleConnectionManager connectionManager,
        DeviceHubViewModel deviceHubViewModel)
    {
        _deviceRegistry = deviceRegistry;
        _connectionManager = connectionManager;
        _deviceHubViewModel = deviceHubViewModel;

    OpenWorkspaceCommand = new Command<DeviceCardViewModel>(
            async card => await OpenWorkspaceAsync(card),
            card => card is not null
        );

        ToggleFavoriteCommand = new Command<DeviceCardViewModel>(
            async card => await ToggleFavoriteAsync(card),
            card => card is not null
        );

        ShowInformationCommand =
            new Command<DeviceCardViewModel>(
                ShowInformation,
                card => card is not null
            );

        CloseInfoPopupCommand =
            new Command(CloseInformationPopup);

        // Persistent device registry changes.
        _deviceRegistry.DevicesChanged +=
            OnRegistryDevicesChanged;

        // Independent BLE session lifecycle.
        _connectionManager.SessionAdded +=
            OnSessionCollectionChanged;

        _connectionManager.SessionRemoved +=
            OnSessionCollectionChanged;

        _connectionManager.SessionStateChanged +=
            OnSessionStateChanged;
    }

    // ========================================================
    // DEVICE COLLECTIONS
    // ========================================================

    public ObservableCollection<DeviceCardViewModel> ConnectedDevices
    {
        get;
    } = new();

    public ObservableCollection<DeviceCardViewModel> FavoriteDevices
    {
        get;
    } = new();

    public bool HasConnectedDevices =>
        ConnectedDevices.Count > 0;

    public bool HasNoConnectedDevices =>
        !HasConnectedDevices;

    public bool HasFavoriteDevices =>
        FavoriteDevices.Count > 0;

    public string ConnectedDeviceCountText =>
        ConnectedDevices.Count == 1
            ? "1 DEVICE CONNECTED"
            : $"{ConnectedDevices.Count} DEVICES CONNECTED";

    public string FavoriteDeviceCountText =>
        FavoriteDevices.Count == 1
            ? "1 FAVOURITE"
            : $"{FavoriteDevices.Count} FAVOURITES";

    // ========================================================
    // COMMANDS
    // ========================================================

    public Command<DeviceCardViewModel> OpenWorkspaceCommand { get; }

    public Command<DeviceCardViewModel> ToggleFavoriteCommand { get; }

    public Command<DeviceCardViewModel> ShowInformationCommand { get; }

    public Command CloseInfoPopupCommand { get; }

    // ========================================================
    // HOME STATUS
    // ========================================================

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
                OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    public bool HasStatusMessage =>
        !string.IsNullOrWhiteSpace(StatusMessage);


    // ========================================================
    // DEVICE INFORMATION POPUP
    // ========================================================

    public DeviceCardViewModel? SelectedInfoCard
    {
        get => _selectedInfoCard;
        private set => SetProperty(ref _selectedInfoCard, value);
    }

    public bool IsInfoPopupVisible
    {
        get => _isInfoPopupVisible;
        private set => SetProperty(ref _isInfoPopupVisible, value);
    }

    private void ShowInformation(DeviceCardViewModel? card)
    {
        if (card is null)
            return;

        SelectedInfoCard = card;
        IsInfoPopupVisible = true;
    }

    public void CloseInformationPopup()
    {
        IsInfoPopupVisible = false;
        SelectedInfoCard = null;
    }

    // ========================================================
    // INITIALIZATION
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
                $"[HomeViewModel] Initialization failed: {ex.Message}"
            );

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                StatusMessage =
                    $"Could not initialize Home: {ex.Message}";
            });
        }
    }

    // ========================================================
    // REFRESH DEVICE SNAPSHOT
    // ========================================================

    private async Task RefreshAsync()
    {
        await _refreshSemaphore.WaitAsync();

        try
        {
            IReadOnlyList<DeviceRecord> records =
                await _deviceRegistry.GetDevicesAsync();

            IDeviceSession[] sessions =
                _connectionManager.Sessions.ToArray();

            var entries = records
                .Select(record => new DeviceEntry(
                    record,
                    FindMatchingSession(record, sessions)
                ))
                .ToArray();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                ApplyDeviceSnapshot(entries, sessions);

                StatusMessage = string.Empty;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[HomeViewModel] Device refresh failed: {ex.Message}"
            );

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                StatusMessage =
                    $"Could not refresh devices: {ex.Message}";
            });
        }
        finally
        {
            _refreshSemaphore.Release();
        }
    }

    // ========================================================
    // APPLY DEVICE SNAPSHOT
    // ========================================================

    private void ApplyDeviceSnapshot(
        IReadOnlyList<DeviceEntry> entries,
        IReadOnlyList<IDeviceSession> sessions)
    {
        SynchronizeSessionSubscriptions(sessions);

        // Every connected device belongs in Connected Devices.
        DeviceEntry[] connectedEntries = entries
            .Where(entry => entry.Session?.IsConnected == true)
            .ToArray();

        // A connected favourite is already displayed above.
        // Only disconnected favourites belong in this collection.
        DeviceEntry[] favoriteEntries = entries
            .Where(entry =>
                entry.Record.IsFavorite &&
                entry.Session?.IsConnected != true
            )
            .ToArray();

        DeviceEntry[] visibleEntries =
            connectedEntries.Concat(favoriteEntries).ToArray();

        HashSet<string> visibleDeviceIds = visibleEntries
            .Select(entry => entry.Record.DeviceId)
            .ToHashSet(StringComparer.Ordinal);

        // Create or update only visible cards.
        foreach (DeviceEntry entry in visibleEntries)
        {
            if (!_cardsByDeviceId.TryGetValue(
                    entry.Record.DeviceId,
                    out DeviceCardViewModel? card))
            {
                card = new DeviceCardViewModel(
                    entry.Record,
                    entry.Session
                );

                _cardsByDeviceId.Add(
                    entry.Record.DeviceId,
                    card
                );
            }
            else
            {
                card.Update(
                    entry.Record,
                    entry.Session
                );
            }
        }

        // Remove cards that are neither connected nor favourites.
        foreach (string deviceId in _cardsByDeviceId.Keys.ToArray())
        {
            if (!visibleDeviceIds.Contains(deviceId))
            {
                _cardsByDeviceId.Remove(deviceId);
            }
        }

        // Rebuild the transport-ID lookup without rebuilding card objects.
        _cardsByBluetoothId.Clear();

        foreach (DeviceEntry entry in visibleEntries)
        {
            if (entry.Session is not null)
            {
                _cardsByBluetoothId[entry.Session.DeviceId] =
                    _cardsByDeviceId[entry.Record.DeviceId];
            }
        }

        // Preserve the existing card objects and their UI state.
        SynchronizeCollection(
            ConnectedDevices,
            connectedEntries
                .Select(entry => _cardsByDeviceId[entry.Record.DeviceId])
                .ToArray()
        );

        SynchronizeCollection(
            FavoriteDevices,
            favoriteEntries
                .Select(entry => _cardsByDeviceId[entry.Record.DeviceId])
                .ToArray()
        );

        NotifyCollectionProperties();
    }

    // ========================================================
    // MATCH A SAVED DEVICE TO ITS BLE SESSION
    // ========================================================

    private static IDeviceSession? FindMatchingSession(
        DeviceRecord record,
        IEnumerable<IDeviceSession> sessions)
    {
        if (string.IsNullOrWhiteSpace(record.BluetoothDeviceId))
            return null;

        if (!Guid.TryParse(
                record.BluetoothDeviceId,
                out Guid bluetoothDeviceId))
        {
            return null;
        }

        return sessions.FirstOrDefault(session =>
            session.DeviceId == bluetoothDeviceId &&
            session.DeviceType == record.DeviceType
        );
    }

    // ========================================================
    // SYNCHRONIZE STATUS SUBSCRIPTIONS
    // ========================================================

    private void SynchronizeSessionSubscriptions(
        IReadOnlyList<IDeviceSession> sessions)
    {
        Dictionary<Guid, IDeviceSession> currentSessions =
            sessions.ToDictionary(session => session.DeviceId);

        // Remove subscriptions for sessions that are no longer managed.
        foreach (Guid deviceId in _observedSessions.Keys.ToArray())
        {
            IDeviceSession existingSession =
                _observedSessions[deviceId];

            if (!currentSessions.TryGetValue(
                    deviceId,
                    out IDeviceSession? currentSession) ||
                !ReferenceEquals(existingSession, currentSession))
            {
                existingSession.StatusPayloadReceived -=
                    OnSessionStatusPayloadReceived;

                _observedSessions.Remove(deviceId);
            }
        }

        // Subscribe to newly added or replaced sessions.
        foreach (IDeviceSession session in sessions)
        {
            if (_observedSessions.TryGetValue(
                    session.DeviceId,
                    out IDeviceSession? existingSession) &&
                ReferenceEquals(existingSession, session))
            {
                continue;
            }

            session.StatusPayloadReceived +=
                OnSessionStatusPayloadReceived;

            _observedSessions[session.DeviceId] = session;
        }
    }

    // ========================================================
    // RECEIVE DEVICE STATUS
    // ========================================================

    private void OnSessionStatusPayloadReceived(
        IDeviceSession session,
        string payload)
    {
        // BLE notifications can arrive on a background thread.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_observedSessions.TryGetValue(
                    session.DeviceId,
                    out IDeviceSession? observedSession) ||
                !ReferenceEquals(observedSession, session))
            {
                return;
            }

            if (!session.IsConnected)
                return;

            if (_cardsByBluetoothId.TryGetValue(
                    session.DeviceId,
                    out DeviceCardViewModel? card))
            {
                card.ApplyStatusPayload(payload);
            }
        });
    }

    // ========================================================
    // REGISTRY EVENTS
    // ========================================================

    private void OnRegistryDevicesChanged()
    {
        _ = RefreshAsync();
    }

    // ========================================================
    // BLE SESSION EVENTS
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[HomeViewModel] Favourite update failed: {ex.Message}"
            );

            StatusMessage =
                $"Could not update favourite: {ex.Message}";
        }
    }

    // ========================================================
    // TOGGLE DEVICE INFORMATION
    // ========================================================

    private static void ToggleInformation(
        DeviceCardViewModel? card)
    {
        card?.ToggleInformation();
    }

    // ========================================================
    // OPEN DEVICE WORKSPACE
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
            // Device workspaces belong to the Devices tab stack.
            // Skip the automatic discovery scan for this navigation.
            _deviceHubViewModel.SkipNextAppearanceScan();
            await shell.GoToAsync("//devices");

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
                $"[HomeViewModel] Workspace navigation failed: {ex.Message}"
            );

            StatusMessage =
                $"Could not open device workspace: {ex.Message}";
        }
    }

    // ========================================================
    // UPDATE OBSERVABLE COLLECTION WITHOUT RECREATING CARDS
    // ========================================================

    private static void SynchronizeCollection(
        ObservableCollection<DeviceCardViewModel> target,
        IReadOnlyList<DeviceCardViewModel> desired)
    {
        for (int index = 0; index < desired.Count; index++)
        {
            DeviceCardViewModel desiredCard = desired[index];

            if (index < target.Count &&
                ReferenceEquals(target[index], desiredCard))
            {
                continue;
            }

            int existingIndex = target.IndexOf(desiredCard);

            if (existingIndex >= 0)
            {
                target.Move(existingIndex, index);
            }
            else
            {
                target.Insert(index, desiredCard);
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    // ========================================================
    // PROPERTY NOTIFICATIONS
    // ========================================================

    private void NotifyCollectionProperties()
    {
        OnPropertyChanged(nameof(HasConnectedDevices));
        OnPropertyChanged(nameof(HasNoConnectedDevices));
        OnPropertyChanged(nameof(HasFavoriteDevices));
        OnPropertyChanged(nameof(ConnectedDeviceCountText));
        OnPropertyChanged(nameof(FavoriteDeviceCountText));
    }

    // ========================================================
    // INTERNAL SNAPSHOT
    // ========================================================

    private sealed record DeviceEntry(
        DeviceRecord Record,
        IDeviceSession? Session
    );
}