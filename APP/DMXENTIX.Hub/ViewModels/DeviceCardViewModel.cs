using DMXENTIX.Hub.Models;
using DMXENTIX.Hub.Models.Devices;
using DMXENTIX.Hub.Services.Bluetooth;
using Microsoft.Maui.Graphics;

using AppDeviceType =
    global::DMXENTIX.Hub.Models.Devices.DeviceType;

namespace DMXENTIX.Hub.ViewModels;

public sealed class DeviceCardViewModel : ObservableObject
{
    private DeviceRecord _record;

    private IDeviceSession? _session;

    private bool _isConnected;
    private bool _isInformationExpanded;

    private DeviceStatus? _emotixStatus;
    private SoundNodeTelemetry? _soundNodeTelemetry;

    public DeviceCardViewModel(
        DeviceRecord record,
        IDeviceSession? session)
    {
        _record = record;

        Update(record, session);
    }

    // ========================================================
    // DEVICE INFORMATION
    // ========================================================

    public string DeviceId =>
        _record.DeviceId;

    public string RegistryDeviceIdText =>
        _record.DeviceId;

    public string BluetoothDeviceIdText =>
        string.IsNullOrWhiteSpace(_record.BluetoothDeviceId)
            ? "Not available"
            : _record.BluetoothDeviceId;

    public string DisplayName =>
        _record.DisplayName;

    public string AdvertisedName =>
        string.IsNullOrWhiteSpace(_record.AdvertisedName)
            ? "Bluetooth name unavailable"
            : _record.AdvertisedName;

    public string DeviceTypeText =>
        _record.DeviceType.ToString().ToUpperInvariant();

    public bool IsEmotix360 =>
        _record.DeviceType == AppDeviceType.Emotix360;

    public bool IsSoundNode =>
        _record.DeviceType == AppDeviceType.SoundNode;

    public bool IsFavorite =>
        _record.IsFavorite;

    public string FavoriteButtonText =>
        IsFavorite ? "FAVORITED" : "ADD FAVORITE";

    public string FavoriteGlyph =>
        IsFavorite ? "★" : "☆";

    public string LastConnectedText =>
        _record.LastConnectedAtUtc.HasValue
            ? _record.LastConnectedAtUtc.Value
                .ToLocalTime()
                .ToString("g")
            : "Not yet connected";

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
                OnPropertyChanged(nameof(ConnectionStatusColor));

                NotifyLiveStatusProperties();
            }
        }
    }

    public string ConnectionStatusText =>
        IsConnected ? "CONNECTED" : "SAVED · OFFLINE";

    public Color ConnectionStatusColor =>
        IsConnected
            ? Color.FromArgb("#72C9A0")
            : Color.FromArgb("#A0AEC0");

    // ========================================================
    // INFORMATION PANEL
    // ========================================================

    public bool IsInformationExpanded
    {
        get => _isInformationExpanded;
        private set => SetProperty(
            ref _isInformationExpanded,
            value
        );
    }

    public void ToggleInformation()
    {
        IsInformationExpanded = !IsInformationExpanded;
    }

    // ========================================================
    // EMOTIX360 LIVE STATUS
    // ========================================================

    public bool HasEmotix360Status =>
        _emotixStatus is not null;

    public string WifiStatusText
    {
        get
        {
            if (!IsConnected)
                return "--";

            return _emotixStatus?.WifiConnected switch
            {
                true => "Connected",
                false => "Not connected",
                null => "Waiting"
            };
        }
    }

    public string TimeStatusText
    {
        get
        {
            if (!IsConnected)
                return "--";

            return _emotixStatus?.TimeValid switch
            {
                true => "Synchronized",
                false => "Not synchronized",
                null => "Waiting"
            };
        }
    }

    public string ImuStatusText
    {
        get
        {
            if (!IsConnected)
                return "--";

            return _emotixStatus?.PitchDegrees.HasValue == true &&
                   _emotixStatus.RollDegrees.HasValue
                ? "Active"
                : "Waiting";
        }
    }

    public string MotionSummaryText
    {
        get
        {
            if (!IsConnected)
                return "--";

            if (_emotixStatus?.PitchDegrees is float pitch &&
                _emotixStatus.RollDegrees is float roll)
            {
                return
                    $"Pitch {pitch:F1}°  ·  Roll {roll:F1}°";
            }

            return "Waiting for IMU data";
        }
    }

    public string AnimationModeText
    {
        get
        {
            if (!IsConnected)
                return "--";

            return _emotixStatus?.Mode ?? "Waiting";
        }
    }

    public string BrightnessText
    {
        get
        {
            if (!IsConnected)
                return "--";

            return _emotixStatus?.Brightness is int brightness
                ? $"{brightness} / 15"
                : "Waiting";
        }
    }

    // ========================================================
    // SOUNDNODE LIVE TELEMETRY
    // ========================================================

    public bool HasSoundNodeTelemetry =>
        _soundNodeTelemetry is not null;

    public string RmsText
    {
        get
        {
            if (!IsConnected)
                return "--";

            return _soundNodeTelemetry?.Rms.ToString() ?? "Waiting";
        }
    }

    public string PeakText
    {
        get
        {
            if (!IsConnected)
                return "--";

            return _soundNodeTelemetry?.Peak.ToString() ?? "Waiting";
        }
    }

    public string TemperatureText
    {
        get
        {
            if (!IsConnected)
                return "--";

            if (_soundNodeTelemetry is null)
                return "Waiting";

            return _soundNodeTelemetry.ChipTemperatureC is double temperature
                ? $"{temperature:F1} °C"
                : "Unavailable";
        }
    }

    // ========================================================
    // FIRMWARE INFORMATION
    // ========================================================

    public bool HasFirmwareInfo =>
        !string.IsNullOrWhiteSpace(FirmwareInfoText);

    public string FirmwareInfoText
    {
        get
        {
            List<string> details = [];

            if (!string.IsNullOrWhiteSpace(_record.FirmwareVersion))
            {
                details.Add($"Firmware {_record.FirmwareVersion}");
            }

            if (_record.ProtocolVersion.HasValue)
            {
                details.Add(
                    $"Protocol v{_record.ProtocolVersion.Value}"
                );
            }

            return string.Join(" | ", details);
        }
    }

    public string ProductSummary =>
        BuildProductSummary(_record);

    // ========================================================
    // UPDATE DEVICE RECORD AND SESSION
    // ========================================================

    internal void Update(
        DeviceRecord record,
        IDeviceSession? session)
    {
        bool sessionChanged =
            !ReferenceEquals(_session, session);

        bool wasConnected = IsConnected;

        _record = record;
        _session = session;

        IsConnected = session?.IsConnected == true;

        if (!IsConnected)
        {
            ClearLiveStatus();
        }
        else if (sessionChanged || !wasConnected)
        {
            // Restore the latest status when the card receives
            // a new or reconnected BLE session.
            ClearLiveStatus();

            ApplyStatusPayload(
                session?.LastStatusPayload
            );
        }

        OnPropertyChanged(nameof(DeviceId));
        OnPropertyChanged(nameof(RegistryDeviceIdText));
        OnPropertyChanged(nameof(BluetoothDeviceIdText));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(AdvertisedName));
        OnPropertyChanged(nameof(DeviceTypeText));
        OnPropertyChanged(nameof(IsEmotix360));
        OnPropertyChanged(nameof(IsSoundNode));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteButtonText));
        OnPropertyChanged(nameof(FavoriteGlyph));
        OnPropertyChanged(nameof(LastConnectedText));
        OnPropertyChanged(nameof(HasFirmwareInfo));
        OnPropertyChanged(nameof(FirmwareInfoText));
        OnPropertyChanged(nameof(ProductSummary));
    }

    // ========================================================
    // APPLY STATUS PAYLOAD TO THIS DEVICE ONLY
    // ========================================================

    internal void ApplyStatusPayload(string? payload)
    {
        if (!IsConnected ||
            string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        if (IsEmotix360)
        {
            if (DeviceStatus.TryParse(
                    payload,
                    out DeviceStatus status))
            {
                _emotixStatus = status;

                NotifyLiveStatusProperties();
            }

            return;
        }

        if (IsSoundNode &&
            SoundNodeTelemetry.TryParse(
                payload,
                out SoundNodeTelemetry? telemetry) &&
            telemetry is not null)
        {
            _soundNodeTelemetry = telemetry;

            NotifyLiveStatusProperties();
        }
    }

    // ========================================================
    // CLEAR OLD TELEMETRY
    // ========================================================

    private void ClearLiveStatus()
    {
        _emotixStatus = null;
        _soundNodeTelemetry = null;

        NotifyLiveStatusProperties();
    }

    // ========================================================
    // PROPERTY NOTIFICATIONS
    // ========================================================

    private void NotifyLiveStatusProperties()
    {
        OnPropertyChanged(nameof(HasEmotix360Status));
        OnPropertyChanged(nameof(WifiStatusText));
        OnPropertyChanged(nameof(TimeStatusText));
        OnPropertyChanged(nameof(ImuStatusText));
        OnPropertyChanged(nameof(MotionSummaryText));
        OnPropertyChanged(nameof(AnimationModeText));
        OnPropertyChanged(nameof(BrightnessText));

        OnPropertyChanged(nameof(HasSoundNodeTelemetry));
        OnPropertyChanged(nameof(RmsText));
        OnPropertyChanged(nameof(PeakText));
        OnPropertyChanged(nameof(TemperatureText));
    }

    // ========================================================
    // PRODUCT CAPABILITY SUMMARY
    // ========================================================

    private static string BuildProductSummary(
        DeviceRecord record)
    {
        if (record.DeviceType == AppDeviceType.Emotix360)
        {
            List<string> features = [];

            if ((record.Capabilities &
                 DeviceCapabilities.AnimationControl) != 0)
            {
                features.Add("Animation control");
            }

            if ((record.Capabilities &
                 DeviceCapabilities.BrightnessControl) != 0)
            {
                features.Add("Brightness control");
            }

            if ((record.Capabilities &
                 DeviceCapabilities.WifiProvisioning) != 0)
            {
                features.Add("Wi-Fi provisioning");
            }

            if ((record.Capabilities &
                 DeviceCapabilities.MotionTelemetry) != 0)
            {
                features.Add("Motion telemetry");
            }

            if ((record.Capabilities &
                 DeviceCapabilities.ClockStatus) != 0)
            {
                features.Add("Clock status");
            }

            return features.Count > 0
                ? string.Join(" · ", features)
                : "EMOTIX360 interactive display";
        }

        return $"{record.DeviceType} device";
    }
}