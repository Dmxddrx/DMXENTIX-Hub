
using System.Diagnostics;
using DMXENTIX.Hub.Models.Devices;
using DMXENTIX.Hub.Services;
using DMXENTIX.Hub.Services.Bluetooth;
using DMXENTIX.Hub.Services.Devices;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using AppDeviceType = global::DMXENTIX.Hub.Models.Devices.DeviceType;

namespace DMXENTIX.Hub.ViewModels;

public sealed class DeviceWorkspaceViewModel : ObservableObject
{
    private readonly DeviceRegistryService _deviceRegistry;
    private readonly BleConnectionManager _connectionManager;
    private readonly DeviceSessionSelectionService _sessionSelection;

    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);

    private DeviceRecord? _record;
    private IDeviceSession? _session;
    private SoundNodeTelemetry? _soundNodeTelemetry;

    private Guid? _targetBluetoothDeviceId;

    private bool _isLoading;
    private bool _isConnected;
    private bool _isActive;
    private bool _isSubscribedToManagerEvents;

    private string _statusMessage = "Loading device workspace...";

    public DeviceWorkspaceViewModel(
        DeviceRegistryService deviceRegistry,
        BleConnectionManager connectionManager,
        DeviceSessionSelectionService sessionSelection)
    {
        _deviceRegistry = deviceRegistry;
        _connectionManager = connectionManager;
        _sessionSelection = sessionSelection;

        BackCommand = new Command(
            async () =>
            {
                Shell? shell = Shell.Current;

                if (shell is not null)
                    await shell.GoToAsync("..");
            }
        );
    }

    // ========================================================
    // COMMANDS
    // ========================================================

    public Command BackCommand { get; }

    // ========================================================
    // DEVICE INFORMATION
    // ========================================================

    public bool HasRecord =>
        _record is not null;

    public string DisplayName =>
        _record?.DisplayName ?? "Device not found";

    public string DeviceTypeText =>
        _record?.DeviceType.ToString().ToUpperInvariant() ?? "--";

    public string AdvertisedName =>
        string.IsNullOrWhiteSpace(_record?.AdvertisedName)
            ? "Bluetooth name unavailable"
            : _record.AdvertisedName;

    public string RegistryDeviceIdText =>
        _record?.DeviceId ?? "--";

    public string BluetoothDeviceIdText =>
        _record?.BluetoothDeviceId ?? "--";

    public string LastConnectedText =>
        _record?.LastConnectedAtUtc is DateTimeOffset lastConnected
            ? lastConnected.ToLocalTime().ToString("g")
            : "Not yet connected";

    public bool IsEmotix360Workspace =>
        _record?.DeviceType == AppDeviceType.Emotix360;

    public bool IsSoundNodeWorkspace =>
        _record?.DeviceType == AppDeviceType.SoundNode;

    public bool IsSoundNodeDashboardVisible =>
        IsSoundNodeWorkspace && IsConnected;

    public bool IsDashboardVisible =>
        IsEmotix360Workspace && IsConnected;

    public bool IsWorkspaceMessageVisible =>
        !IsDashboardVisible;

    // ========================================================
    // SOUNDNODE TELEMETRY
    // ========================================================

    public int SoundNodeRms =>
        _soundNodeTelemetry?.Rms ?? 0;

    public int SoundNodePeak =>
        _soundNodeTelemetry?.Peak ?? 0;

    public string SoundNodeRmsText =>
        _soundNodeTelemetry?.Rms.ToString() ?? "--";

    public string SoundNodePeakText =>
        _soundNodeTelemetry?.Peak.ToString() ?? "--";

    public string SoundNodeTemperatureText =>
        _soundNodeTelemetry?.ChipTemperatureC is double temperature
            ? $"{temperature:F1} °C"
            : "Unavailable";

    public bool HasSoundNodeTelemetry =>
        _soundNodeTelemetry is not null;

    public string SoundNodeTelemetryStatusText =>
        HasSoundNodeTelemetry
            ? "LIVE TELEMETRY"
            : "WAITING FOR TELEMETRY";

    // ========================================================
    // LOADING STATE
    // ========================================================

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
                OnPropertyChanged(nameof(WorkspaceMessage));
        }
    }

    // ========================================================
    // CONNECTION STATE
    // ========================================================

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(ConnectionStatusText));
                OnPropertyChanged(nameof(WorkspaceMessage));
                OnPropertyChanged(nameof(IsDashboardVisible));
                OnPropertyChanged(nameof(IsWorkspaceMessageVisible));
            }
        }
    }

    public string ConnectionStatusText =>
        IsConnected ? "CONNECTED" : "SAVED · OFFLINE";

    // ========================================================
    // WORKSPACE STATUS
    // ========================================================

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string WorkspaceMessage
    {
        get
        {
            if (IsLoading)
                return "Loading saved device information...";

            if (_record is null)
                return "The requested saved device could not be found.";

            if (!IsConnected)
            {
                return
                    "This device is offline. Connect it from Device Hub " +
                    "before accessing its live controls.";
            }

            if (IsEmotix360Workspace)
            {
                return
                    "EMOTIX360 is connected. Its dashboard and Wi-Fi " +
                    "controls will be integrated into this workspace in " +
                    "the next stages.";
            }

            return
                "A workspace for this product has not been implemented yet.";
        }
    }

    // ========================================================
    // LOAD THE REQUESTED SAVED DEVICE
    // ========================================================

    public async Task LoadDeviceAsync(string deviceId)
    {
        await _loadSemaphore.WaitAsync();

        try
        {
            _isActive = true;

            SubscribeToConnectionManager();

            UnsubscribeFromSessionStatus();

            _record = null;
            _session = null;
            _targetBluetoothDeviceId = null;

            ClearSoundNodeTelemetry();

            IsConnected = false;
            IsLoading = true;

            // Do not leave a previously selected device active
            // while opening this saved device's workspace.
            _sessionSelection.SetSelectedSession(null);

            NotifyDeviceInformationChanged();

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                StatusMessage = "No valid device ID was supplied.";
                return;
            }

            // DeviceId is the persistent registry identity.
            DeviceRecord? record =
                await _deviceRegistry.GetDeviceAsync(deviceId);

            if (record is null)
            {
                StatusMessage = "Saved device was not found.";
                return;
            }

            _record = record;

            // Resolve the transport identity from this record.
            if (Guid.TryParse(
                record.BluetoothDeviceId,
                out Guid bluetoothDeviceId))
            {
                _targetBluetoothDeviceId = bluetoothDeviceId;
            }

            // Resolve the matching session, if it already exists.
            RefreshSessionFromManager();

            StatusMessage = IsConnected
                ? $"{record.DisplayName} workspace opened."
                : $"{record.DisplayName} is saved but currently offline.";

            NotifyDeviceInformationChanged();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[DeviceWorkspaceViewModel] Could not load device: {ex.Message}"
            );

            _record = null;
            _session = null;

            UnsubscribeFromSessionStatus();
            ClearSoundNodeTelemetry();

            _targetBluetoothDeviceId = null;

            IsConnected = false;

            _sessionSelection.SetSelectedSession(null);

            StatusMessage =
                $"Could not load device workspace: {ex.Message}";

            NotifyDeviceInformationChanged();
        }
        finally
        {
            IsLoading = false;
            _loadSemaphore.Release();
        }
    }

    // ========================================================
    // CONNECTION MANAGER SUBSCRIPTIONS
    // ========================================================

    private void SubscribeToConnectionManager()
    {
        if (_isSubscribedToManagerEvents)
            return;

        _connectionManager.SessionAdded +=
            OnManagedSessionChanged;

        _connectionManager.SessionRemoved +=
            OnManagedSessionChanged;

        _connectionManager.SessionStateChanged +=
            OnManagedSessionChanged;

        _isSubscribedToManagerEvents = true;
    }

    private void UnsubscribeFromConnectionManager()
    {
        if (!_isSubscribedToManagerEvents)
            return;

        _connectionManager.SessionAdded -=
            OnManagedSessionChanged;

        _connectionManager.SessionRemoved -=
            OnManagedSessionChanged;

        _connectionManager.SessionStateChanged -=
            OnManagedSessionChanged;

        _isSubscribedToManagerEvents = false;
    }

    // ========================================================
    // HANDLE CONNECTION MANAGER EVENTS
    // ========================================================

    private void OnManagedSessionChanged(IDeviceSession session)
    {
        if (!_isActive ||
            !_targetBluetoothDeviceId.HasValue ||
            session.DeviceId != _targetBluetoothDeviceId.Value)
        {
            return;
        }

        // Session events may arrive from a BLE background callback.
        MainThread.BeginInvokeOnMainThread(
            RefreshSessionFromManager
        );
    }

    // ========================================================
    // SOUNDNODE STATUS NOTIFICATIONS
    // ========================================================

    private void OnSessionStatusPayloadReceived(
        IDeviceSession session,
        string payload)
    {
        if (!_isActive ||
            !IsSoundNodeWorkspace ||
            !ReferenceEquals(_session, session))
        {
            return;
        }

        // BLE notifications may arrive from a background thread.
        // Update ViewModel properties on the main UI thread.
        MainThread.BeginInvokeOnMainThread(
            () =>
            {
                if (!_isActive ||
                    !IsSoundNodeWorkspace ||
                    !ReferenceEquals(_session, session))
                {
                    return;
                }

                ApplySoundNodeStatusPayload(payload);
            }
        );
    }

    private void ApplySoundNodeStatusPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return;

        if (!SoundNodeTelemetry.TryParse(
            payload,
            out SoundNodeTelemetry? telemetry) ||
            telemetry is null)
        {
            Debug.WriteLine(
                $"[SoundNode] Invalid telemetry payload: {payload}"
            );

            return;
        }

        _soundNodeTelemetry = telemetry;

        OnPropertyChanged(nameof(SoundNodeRms));
        OnPropertyChanged(nameof(SoundNodePeak));
        OnPropertyChanged(nameof(SoundNodeRmsText));
        OnPropertyChanged(nameof(SoundNodePeakText));
        OnPropertyChanged(nameof(SoundNodeTemperatureText));
        OnPropertyChanged(nameof(HasSoundNodeTelemetry));
        OnPropertyChanged(nameof(SoundNodeTelemetryStatusText));

        Debug.WriteLine(
            $"[SoundNode] RMS={telemetry.Rms}, " +
            $"PK={telemetry.Peak}, " +
            $"TEMP={SoundNodeTemperatureText}"
        );
    }

    private void ClearSoundNodeTelemetry()
    {
        _soundNodeTelemetry = null;

        OnPropertyChanged(nameof(SoundNodeRms));
        OnPropertyChanged(nameof(SoundNodePeak));
        OnPropertyChanged(nameof(SoundNodeRmsText));
        OnPropertyChanged(nameof(SoundNodePeakText));
        OnPropertyChanged(nameof(SoundNodeTemperatureText));
        OnPropertyChanged(nameof(HasSoundNodeTelemetry));
        OnPropertyChanged(nameof(SoundNodeTelemetryStatusText));
    }

    private void UnsubscribeFromSessionStatus()
    {
        if (_session is null)
            return;

        _session.StatusPayloadReceived -=
            OnSessionStatusPayloadReceived;
    }

    // ========================================================
    // RESOLVE THE CURRENT SESSION FOR THIS WORKSPACE
    // ========================================================

    private void RefreshSessionFromManager()
    {
        if (!_isActive ||
            _record is null ||
            !_targetBluetoothDeviceId.HasValue)
        {
            return;
        }

        Guid targetDeviceId = _targetBluetoothDeviceId.Value;

        // Always query the manager again.
        // The previous session object may have been replaced.
        IDeviceSession? currentSession =
            _connectionManager.GetSession(targetDeviceId);

        // Verify the product type as well as the transport ID.
        if (currentSession is not null &&
            currentSession.DeviceType != _record.DeviceType)
        {
            currentSession = null;
        }

        // Reconnect the status handler only when the session changes.
        if (!ReferenceEquals(_session, currentSession))
        {
            UnsubscribeFromSessionStatus();

            _session = currentSession;

            ClearSoundNodeTelemetry();

            // SoundNode owns its own telemetry parser and status handling.
            if (_session is not null && IsSoundNodeWorkspace)
            {
                _session.StatusPayloadReceived +=
                    OnSessionStatusPayloadReceived;
            }
        }

        IsConnected = currentSession?.IsConnected == true;

        if (IsConnected && currentSession is not null)
        {
            // Process the most recent payload as well as future notifications.
            // The initial BLE status may have arrived before this workspace opened.
            if (IsSoundNodeWorkspace)
            {
                ApplySoundNodeStatusPayload(
                    currentSession.LastStatusPayload
                );
            }

            // Select only the session belonging to this workspace.
            _sessionSelection.SetSelectedSession(currentSession);

            StatusMessage =
                $"{DisplayName} is connected.";
        }
        else
        {
            // Clear selection only if it belongs to this device.
            if (_sessionSelection.SelectedSession?.DeviceId == targetDeviceId)
            {
                _sessionSelection.SetSelectedSession(null);
            }

            ClearSoundNodeTelemetry();

            StatusMessage =
                $"{DisplayName} is saved but currently offline.";
        }
    }

    // ========================================================
    // PAGE LIFETIME
    // ========================================================

    public void Deactivate()
    {
        _isActive = false;

        UnsubscribeFromSessionStatus();
        UnsubscribeFromConnectionManager();
    }

    // ========================================================
    // PROPERTY NOTIFICATIONS
    // ========================================================

    private void NotifyDeviceInformationChanged()
    {
        OnPropertyChanged(nameof(HasRecord));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DeviceTypeText));
        OnPropertyChanged(nameof(AdvertisedName));
        OnPropertyChanged(nameof(RegistryDeviceIdText));
        OnPropertyChanged(nameof(BluetoothDeviceIdText));
        OnPropertyChanged(nameof(LastConnectedText));
        OnPropertyChanged(nameof(IsEmotix360Workspace));
        OnPropertyChanged(nameof(WorkspaceMessage));
        OnPropertyChanged(nameof(IsDashboardVisible));
        OnPropertyChanged(nameof(IsWorkspaceMessageVisible));
        OnPropertyChanged(nameof(IsSoundNodeWorkspace));
        OnPropertyChanged(nameof(IsSoundNodeDashboardVisible));
    }
}
