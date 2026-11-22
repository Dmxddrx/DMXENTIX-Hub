namespace DMXENTIX.Hub.Models.Devices;

/// <summary>
/// Features a device advertises as supported. The app uses these capabilities
/// later to decide which controls and settings to display for each product.
/// </summary>
[Flags]
public enum DeviceCapabilities
{
    None = 0,

    AnimationControl = 1 << 0,
    BrightnessControl = 1 << 1,
    WifiProvisioning = 1 << 2,
    MotionTelemetry = 1 << 3,
    ClockStatus = 1 << 4,

    TemperatureTelemetry = 1 << 5,
    HumidityTelemetry = 1 << 6,
    DustTelemetry = 1 << 7,
    SensorConfiguration = 1 << 8,

    RemoteDiagnostics = 1 << 9,

    AudioLevelTelemetry = 1 << 10
}
