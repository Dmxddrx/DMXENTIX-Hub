using System.Windows.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using DMXENTIX.Hub.Models;
using DMXENTIX.Hub.Services;
using Microsoft.Maui.Graphics;
using DMXENTIX.Hub.Services.Bluetooth;

namespace DMXENTIX.Hub.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    private readonly DeviceSessionSelectionService _sessionSelection;
    private readonly DeviceCommandService _commands;
    private readonly DeviceStatusService _statusService;

    private IDeviceSession? _session;

    private bool _isConnected;
    private string _dashboardMessage = "Connect your EMOTIX360 to see live device data.";
    private string _activeModeText = "--";
    private string _brightnessText = "--";
    private string _wifiText = "--";
    private string _motionText = "--";
    private double _brightnessValue = 5;
    private string _timeText = "--";

    // --------------------------------------------------------
    // BRIGHTNESS PREVIEW STATE
    // --------------------------------------------------------

    // Latest confirmed brightness reported by the ESP32.
    private double? _deviceBrightnessValue;

    // Prevent device status updates from overwriting
    // the slider while the user is adjusting it.
    private bool _isBrightnessDragging;

    // Indicates that the slider is temporarily showing
    // a user-selected brightness.
    private bool _isBrightnessPreviewActive;

    // Brightness value awaiting device confirmation.
    private int? _pendingBrightnessValue;

    // Cancels the five-second preview timeout when
    // the user starts another adjustment.
    private CancellationTokenSource? _brightnessPreviewTimeoutCts;

    private const int BRIGHTNESS_PREVIEW_TIMEOUT_MS = 5000;

    private const int BRIGHTNESS_RETURN_STEPS = 15;

    private const int BRIGHTNESS_RETURN_INTERVAL_MS = 30;

    // --------------------------------------------------------
    // ANIMATION HIGHLIGHT STATE
    // --------------------------------------------------------

    // Actual animation mode most recently reported by the ESP32.
    private string? _activeModeKey;

    // Temporary app selection while waiting for device confirmation.
    private string? _pendingAppModeKey;

    // Identifies which source made the latest confirmed selection.
    private ModeSelectionSource _modeSelectionSource =
        ModeSelectionSource.None;

    // Prevent older timeout tasks from clearing newer selections.
    private int _modeRequestVersion;

    private enum ModeSelectionSource
    {
        None,
        App,
        Device
    }

    // Only one animation can be highlighted.
    public string? HighlightedModeKey =>
        _pendingAppModeKey ?? _activeModeKey;

    // The highlighted tile displays the appropriate status.
    public string ModeSelectionStatusText =>
        _pendingAppModeKey is not null ||
        _modeSelectionSource == ModeSelectionSource.App
            ? "Selected"
            : _modeSelectionSource == ModeSelectionSource.Device
                ? "Active"
                : string.Empty;

    private void NotifyModeSelectionChanged()
    {
        OnPropertyChanged(nameof(HighlightedModeKey));
        OnPropertyChanged(nameof(ModeSelectionStatusText));
    }

    public HomeViewModel(
        DeviceSessionSelectionService sessionSelection,
        DeviceCommandService commands,
        DeviceStatusService statusService)
    {
        _sessionSelection = sessionSelection;
        _commands = commands;
        _statusService = statusService;

        OpenDeviceCommand = new Command(async () => await Shell.Current.GoToAsync("//device"));
        SelectAnimationCommand = new Command<string>(
            async mode => await SelectAnimationAsync(mode),
            _ => IsConnected);
        ApplyBrightnessCommand = new Command(
            async () => await ApplyBrightnessAsync(),
            () => IsConnected);

        // Brightness slider interaction commands.
        BrightnessDragStartedCommand = new Command(
            OnBrightnessDragStarted
        );

        BrightnessDragCompletedCommand = new Command(
            OnBrightnessDragCompleted
        );

        _sessionSelection.SelectedSessionChanged +=
            OnSelectedSessionChanged;

        _statusService.StatusUpdated += OnStatusUpdated;
        _statusService.StatusCleared += OnStatusCleared;

        // Restore the currently selected device session.
        OnSelectedSessionChanged(_sessionSelection.SelectedSession);
    }

    public ICommand OpenDeviceCommand { get; }
    public Command<string> SelectAnimationCommand { get; }
    public Command ApplyBrightnessCommand { get; }
    public Command BrightnessDragStartedCommand { get; }
    public Command BrightnessDragCompletedCommand { get; }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value))
                return;

            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(ConnectionDescription));
            OnPropertyChanged(nameof(ConnectionIndicatorColor));
            OnPropertyChanged(nameof(ConnectButtonText));
            OnPropertyChanged(nameof(OverviewStatusText));
            SelectAnimationCommand.ChangeCanExecute();
            ApplyBrightnessCommand.ChangeCanExecute();
        }
    }

    public string ConnectionText => IsConnected ? "CONNECTED" : "OFFLINE";
    public string ConnectionDescription => IsConnected ? "Connected to EMOTIX360" : "Not connected";
    public Color ConnectionIndicatorColor => IsConnected
        ? Color.FromArgb("#72C9A0")
        : Color.FromArgb("#F2A65A");
    public string ConnectButtonText => IsConnected ? "DEVICE CONNECTED" : "CONNECT DEVICE";
    public string OverviewStatusText => IsConnected ? "LIVE DEVICE DATA" : "AWAITING CONNECTION";

    public string DashboardMessage
    {
        get => _dashboardMessage;
        private set => SetProperty(ref _dashboardMessage, value);
    }

    public string ActiveModeText
    {
        get => _activeModeText;
        private set => SetProperty(ref _activeModeText, value);
    }
    // --------------------------------------------------------
    // HIGHLIGHTED ANIMATION
    // --------------------------------------------------------

    public string BrightnessText
    {
        get => _brightnessText;
        private set => SetProperty(ref _brightnessText, value);
    }

    public string WifiText
    {
        get => _wifiText;
        private set => SetProperty(ref _wifiText, value);
    }

    public string MotionText
    {
        get => _motionText;
        private set => SetProperty(ref _motionText, value);
    }

    /// <summary>0..15 MAX7219 intensity. The user applies it with the Apply button.</summary>
    public double BrightnessValue
    {
        get => _brightnessValue;
        set
        {
            double clamped = Math.Clamp(value, 0, 15);
            if (SetProperty(ref _brightnessValue, clamped))
                OnPropertyChanged(nameof(BrightnessSliderText));
        }
    }

    public string TimeText
    {
        get => _timeText;
        private set => SetProperty(ref _timeText, value);
    }

    public string BrightnessSliderText => $"{(int)Math.Round(BrightnessValue)} / 15";


    private async Task SelectAnimationAsync(string? wireName)
    {
        IDeviceSession? session = _session;

        if (!IsConnected || session is null || !session.IsConnected)
        {
            DashboardMessage = "Connect to EMOTIX360 before selecting an animation.";
            return;
        }

        if (!AnimationModeExtensions.TryParseFirmwareValue(wireName, out AnimationMode mode))
        {
            DashboardMessage = "Unknown animation mode.";
            return;
        }

        string selectedModeKey = wireName!.Trim().ToUpperInvariant();

        int requestVersion = ++_modeRequestVersion;

        // Show the app selection immediately.
        _pendingAppModeKey = selectedModeKey;
        NotifyModeSelectionChanged();

        try
        {
            await _commands.SetModeAsync(session, mode);

            DashboardMessage =
                $"Sent {mode.ToDisplayName()} command. Waiting for device status.";

            // Remove the temporary selection if the device does not confirm it.
            _ = ClearPendingAppSelectionAsync(
                selectedModeKey,
                requestVersion);
        }
        catch (Exception ex)
        {
            // Only clear this request if a newer selection has not replaced it.
            if (requestVersion == _modeRequestVersion &&
                _pendingAppModeKey == selectedModeKey)
            {
                _pendingAppModeKey = null;
                NotifyModeSelectionChanged();
            }

            DashboardMessage =
                $"Could not send animation command: {ex.Message}";
        }
    }

    private async Task ClearPendingAppSelectionAsync(
    string modeKey,
    int requestVersion)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (requestVersion != _modeRequestVersion ||
                !string.Equals(
                    _pendingAppModeKey,
                    modeKey,
                    StringComparison.Ordinal))
            {
                return;
            }

            // No confirmation arrived. Restore the previously reported mode.
            _pendingAppModeKey = null;
            NotifyModeSelectionChanged();
        });
    }

    private void UpdateModeSelectionFromStatus(string reportedMode)
    {
        if (_pendingAppModeKey is not null)
        {
            // The device confirmed the mode requested by the app.
            if (string.Equals(
                reportedMode,
                _pendingAppModeKey,
                StringComparison.Ordinal))
            {
                _activeModeKey = reportedMode;
                _pendingAppModeKey = null;

                _modeSelectionSource = ModeSelectionSource.App;

                // Invalidate the pending timeout.
                ++_modeRequestVersion;
            }
            else if (_activeModeKey is null)
            {
                // First telemetry packet establishes the previous device mode.
                // Keep the app selection pending until it is confirmed.
                _activeModeKey = reportedMode;
                _modeSelectionSource = ModeSelectionSource.Device;
            }
            else if (!string.Equals(
                reportedMode,
                _activeModeKey,
                StringComparison.Ordinal))
            {
                // The device reported a different mode while an app selection
                // was pending. Treat it as a physical device-button override.
                _activeModeKey = reportedMode;
                _pendingAppModeKey = null;

                _modeSelectionSource = ModeSelectionSource.Device;

                // Invalidate the pending app-selection timeout.
                ++_modeRequestVersion;
            }
        }
        else
        {
            // No app request is pending. A new reported mode means the device
            // changed its animation, normally through the GPIO14 button.
            if (_activeModeKey is null ||
                !string.Equals(
                    reportedMode,
                    _activeModeKey,
                    StringComparison.Ordinal))
            {
                _activeModeKey = reportedMode;
                _modeSelectionSource = ModeSelectionSource.Device;
            }
        }

        NotifyModeSelectionChanged();
    }

    // ========================================================
    // BRIGHTNESS SLIDER INTERACTION
    // ========================================================

    // --------------------------------------------------------
    // USER STARTED MOVING THE SLIDER
    // --------------------------------------------------------

    private void OnBrightnessDragStarted()
    {
        // Cancel any previous return animation or timeout.
        CancelBrightnessPreviewTimeout();

        _isBrightnessDragging = true;
        _isBrightnessPreviewActive = true;

        _pendingBrightnessValue = null;
    }


    // --------------------------------------------------------
    // USER RELEASED THE SLIDER
    // --------------------------------------------------------

    private void OnBrightnessDragCompleted()
    {
        _isBrightnessDragging = false;

        // Give the user five seconds to press Apply.
        RestartBrightnessPreviewTimeout();
    }


    // --------------------------------------------------------
    // CANCEL PREVIOUS PREVIEW TIMEOUT
    // --------------------------------------------------------

    private void CancelBrightnessPreviewTimeout()
    {
        CancellationTokenSource? previous =
            _brightnessPreviewTimeoutCts;

        _brightnessPreviewTimeoutCts = null;

        if (previous is not null)
        {
            previous.Cancel();
        }
    }


    // --------------------------------------------------------
    // START FIVE-SECOND PREVIEW TIMEOUT
    // --------------------------------------------------------

    private void RestartBrightnessPreviewTimeout()
    {
        CancelBrightnessPreviewTimeout();

        var source = new CancellationTokenSource();

        _brightnessPreviewTimeoutCts = source;

        _ = ReturnBrightnessAfterTimeoutAsync(source);
    }


    // --------------------------------------------------------
    // WAIT FOR USER TO APPLY BRIGHTNESS
    // --------------------------------------------------------

    private async Task ReturnBrightnessAfterTimeoutAsync(
        CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(
                BRIGHTNESS_PREVIEW_TIMEOUT_MS,
                source.Token
            );

            if (!source.IsCancellationRequested)
            {
                await AnimateBrightnessBackToDeviceAsync(
                    source.Token
                );
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the user starts another adjustment
            // or the device confirms the selected brightness.
        }
        finally
        {
            if (ReferenceEquals(
                    _brightnessPreviewTimeoutCts,
                    source))
            {
                _brightnessPreviewTimeoutCts = null;
            }

            source.Dispose();
        }
    }


    // --------------------------------------------------------
    // SMOOTHLY RETURN TO DEVICE BRIGHTNESS
    // --------------------------------------------------------

    private async Task AnimateBrightnessBackToDeviceAsync(
        CancellationToken cancellationToken)
    {
        double startValue = BrightnessValue;

        double targetValue =
            _deviceBrightnessValue ?? startValue;

        for (int step = 1;
             step <= BRIGHTNESS_RETURN_STEPS;
             step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double progress =
                step / (double)BRIGHTNESS_RETURN_STEPS;

            double interpolatedValue =
                startValue +
                ((targetValue - startValue) * progress);

            await MainThread.InvokeOnMainThreadAsync(
                () =>
                {
                    BrightnessValue = interpolatedValue;
                }
            );

            await Task.Delay(
                BRIGHTNESS_RETURN_INTERVAL_MS,
                cancellationToken
            );
        }

        // Use the latest confirmed device value.
        await MainThread.InvokeOnMainThreadAsync(
            () =>
            {
                if (_deviceBrightnessValue.HasValue)
                {
                    BrightnessValue =
                        _deviceBrightnessValue.Value;
                }

                _pendingBrightnessValue = null;

                _isBrightnessPreviewActive = false;
            }
        );
    }

    private async Task ApplyBrightnessAsync()
    {
        IDeviceSession? session = _session;

        if (!IsConnected || session is null || !session.IsConnected)
        {
            DashboardMessage =
                "Connect to EMOTIX360 before changing brightness.";

            return;
        }

        int value = (int)Math.Round(BrightnessValue);

        // Keep the selected value visible while waiting
        // for confirmation from the ESP32.
        _isBrightnessPreviewActive = true;

        _isBrightnessDragging = false;

        _pendingBrightnessValue = value;

        // Give the device time to apply and report the value.
        RestartBrightnessPreviewTimeout();

        try
        {
            await _commands.SetBrightnessAsync(session, value);

            DashboardMessage =
                $"Sent brightness {value}. Waiting for device confirmation.";
        }
        catch (Exception ex)
        {
            _pendingBrightnessValue = null;

            DashboardMessage =
                $"Could not send brightness command: {ex.Message}";
        }
    }


    // ========================================================
    // SELECTED DEVICE SESSION
    // ========================================================

    private void OnSelectedSessionChanged(IDeviceSession? session)
    {
        // Remove the previous session subscription.
        if (_session is not null)
        {
            _session.StateChanged -= OnSessionStateChanged;
        }

        _session = session;

        // Subscribe to the newly selected session.
        if (_session is not null)
        {
            _session.StateChanged += OnSessionStateChanged;
        }

        // Tell the status service which device to monitor.
        _statusService.SetSession(session);

        MainThread.BeginInvokeOnMainThread(
            UpdateConnectionState
        );
    }

    // ========================================================
    // SESSION CONNECTION STATE
    // ========================================================

    private void OnSessionStateChanged(IDeviceSession session)
    {
        if (!ReferenceEquals(session, _session))
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!ReferenceEquals(session, _session))
                return;

            // Ignore state notifications that do not change
            // the selected session's connected state.
            if (IsConnected == session.IsConnected)
                return;

            UpdateConnectionState();
        });
    }

    // ========================================================
    // UPDATE DASHBOARD CONNECTION STATE
    // ========================================================

    private void UpdateConnectionState()
    {
        bool connected = _session?.IsConnected == true;

        IsConnected = connected;

        if (connected)
        {
            DashboardMessage =
                $"Connected to {_session!.DeviceName}. Waiting for telemetry.";

            return;
        }

        DashboardMessage = _session is null
            ? "Connect to EMOTIX360 to see live device data."
            : "EMOTIX360 is disconnected.";

        ResetDeviceDisplay();
    }

    // ========================================================
    // RESET DEVICE DISPLAY
    // ========================================================

    private void ResetDeviceDisplay()
    {
        // Stop any active brightness preview.
        CancelBrightnessPreviewTimeout();

        _isBrightnessDragging = false;
        _isBrightnessPreviewActive = false;
        _pendingBrightnessValue = null;

        _deviceBrightnessValue = null;

        BrightnessValue = 5;

        // Clear animation selection.
        ClearModeSelectionState();

        ActiveModeText = "--";
        BrightnessText = "--";
        WifiText = "--";
        MotionText = "--";
        TimeText = "--";
    }

    private void OnStatusUpdated(DeviceStatus status)
    {
        MainThread.BeginInvokeOnMainThread(() => ApplyStatus(status));
    }

    private void OnStatusCleared()
    {
        MainThread.BeginInvokeOnMainThread(
            ResetDeviceDisplay
        );
    }
    private void ClearModeSelectionState()
    {
        ++_modeRequestVersion;

        _activeModeKey = null;
        _pendingAppModeKey = null;

        _modeSelectionSource = ModeSelectionSource.None;

        NotifyModeSelectionChanged();
    }

    private void ApplyStatus(DeviceStatus status)
    {
        // --------------------------------------------------------
        // UPDATE ACTUAL DEVICE ANIMATION
        // --------------------------------------------------------

        if (!string.IsNullOrWhiteSpace(status.Mode))
        {
            string reportedMode = status.Mode.Trim().ToUpperInvariant();

            ActiveModeText = reportedMode.Replace('_', ' ');

            // Handle app selection confirmation or physical device changes.
            UpdateModeSelectionFromStatus(reportedMode);
        }

        // --------------------------------------------------------
        // UPDATE BRIGHTNESS
        // --------------------------------------------------------

        if (status.Brightness.HasValue)
        {
            double reportedBrightness =
                status.Brightness.Value;

            // Always update the actual device brightness display.
            BrightnessText =
                reportedBrightness.ToString();

            // Preserve the latest confirmed device value.
            _deviceBrightnessValue = reportedBrightness;

            // Check whether the ESP32 confirmed our requested value.
            if (_pendingBrightnessValue.HasValue &&
                (int)Math.Round(reportedBrightness) ==
                    _pendingBrightnessValue.Value)
            {
                _pendingBrightnessValue = null;

                _isBrightnessPreviewActive = false;

                // Cancel the preview timeout.
                CancelBrightnessPreviewTimeout();

                // Keep the slider at the confirmed value.
                BrightnessValue = reportedBrightness;
            }
            else if (!_isBrightnessDragging &&
                     !_isBrightnessPreviewActive)
            {
                // Normal status synchronization when no user
                // adjustment is currently in progress.
                BrightnessValue = reportedBrightness;
            }
        }

        // --------------------------------------------------------
        // UPDATE WI-FI
        // --------------------------------------------------------

        if (status.WifiConnected.HasValue)
        {
            WifiText = status.WifiConnected.Value ? "ONLINE" : "OFFLINE";
        }

        // --------------------------------------------------------
        // UPDATE MPU6050 ORIENTATION
        // --------------------------------------------------------

        if (status.PitchDegrees.HasValue &&
            status.RollDegrees.HasValue)
        {
            MotionText =
                $"P {status.PitchDegrees.Value:+0.0;-0.0;0.0}°\n" +
                $"R {status.RollDegrees.Value:+0.0;-0.0;0.0}°";
        }
        else
        {
            MotionText = "NO DATA";
        }

        // --------------------------------------------------------
        // UPDATE NTP SYNCHRONIZATION STATUS
        // --------------------------------------------------------

        if (status.TimeValid.HasValue)
        {
            TimeText = status.TimeValid.Value
                ? "NTP SYNCHRONIZED"
                : "NOT SYNCHRONIZED";
        }

        // --------------------------------------------------------
        // UPDATE DASHBOARD MESSAGE
        // --------------------------------------------------------

        if (IsConnected)
        {
            DashboardMessage =
                $"Last device update: {status.ReceivedAt.ToLocalTime():HH:mm:ss}";
        }
    }
}
