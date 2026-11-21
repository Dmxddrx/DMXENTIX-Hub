using DMXENTIX.Hub.Models.Devices;
using DMXENTIX.Hub.Services;
using DMXENTIX.Hub.Services.Bluetooth;
using Microsoft.Maui.Graphics;
using AppDeviceType = DMXENTIX.Hub.Models.Devices.DeviceType;

namespace DMXENTIX.Hub.ViewModels;

public sealed class DeviceCardViewModel : ObservableObject
{
    private DeviceRecord _record;
    private bool _isConnected;

    public DeviceCardViewModel(
        DeviceRecord record,
        IDeviceSession? session)
    {
        _record = record;
        Update(record, session);
    }

    public string DeviceId => _record.DeviceId;

    public string DisplayName => _record.DisplayName;

    public string AdvertisedName =>
        string.IsNullOrWhiteSpace(_record.AdvertisedName)
            ? "Bluetooth name unavailable"
            : _record.AdvertisedName;

    public string DeviceTypeText =>
        _record.DeviceType.ToString().ToUpperInvariant();

    public bool IsFavorite => _record.IsFavorite;

    public string FavoriteButtonText =>
        IsFavorite ? "FAVORITED" : "ADD FAVORITE";

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(ConnectionStatusText));
                OnPropertyChanged(nameof(ConnectionStatusColor));
            }
        }
    }

    public string ConnectionStatusText =>
        IsConnected ? "CONNECTED" : "SAVED · OFFLINE";

    public Color ConnectionStatusColor =>
        IsConnected
            ? Color.FromArgb("#72C9A0")
            : Color.FromArgb("#A0AEC0");

    public string LastConnectedText =>
        _record.LastConnectedAtUtc.HasValue
            ? $"Last connected: {_record.LastConnectedAtUtc.Value.ToLocalTime():g}"
            : "Last connected: Not yet";

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
                details.Add($"Protocol v{_record.ProtocolVersion.Value}");
            }

            return string.Join(" | ", details);
        }
    }

    public string ProductSummary =>
        BuildProductSummary(_record);

    internal void Update(
        DeviceRecord record,
        IDeviceSession? session)
    {
        _record = record;

        IsConnected = session?.IsConnected == true;

        OnPropertyChanged(nameof(DeviceId));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(AdvertisedName));
        OnPropertyChanged(nameof(DeviceTypeText));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteButtonText));
        OnPropertyChanged(nameof(LastConnectedText));
        OnPropertyChanged(nameof(HasFirmwareInfo));
        OnPropertyChanged(nameof(FirmwareInfoText));
        OnPropertyChanged(nameof(ProductSummary));
    }

    private static string BuildProductSummary(DeviceRecord record)
    {
        if (record.DeviceType == AppDeviceType.Emotix360)
        {
            List<string> features = [];

            if ((record.Capabilities & DeviceCapabilities.AnimationControl) != 0)
                features.Add("Animation control");

            if ((record.Capabilities & DeviceCapabilities.BrightnessControl) != 0)
                features.Add("Brightness control");

            if ((record.Capabilities & DeviceCapabilities.WifiProvisioning) != 0)
                features.Add("Wi-Fi provisioning");

            if ((record.Capabilities & DeviceCapabilities.MotionTelemetry) != 0)
                features.Add("Motion telemetry");

            if ((record.Capabilities & DeviceCapabilities.ClockStatus) != 0)
                features.Add("Clock status");

            return features.Count > 0
                ? string.Join(" · ", features)
                : "EMOTIX360 interactive display";
        }

        return $"{record.DeviceType} device";
    }
}