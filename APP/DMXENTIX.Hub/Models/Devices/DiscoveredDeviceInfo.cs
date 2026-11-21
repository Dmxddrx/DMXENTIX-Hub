namespace DMXENTIX.Hub.Models.Devices;

/// <summary>
/// UI-safe information about a discovered supported BLE product.
/// Does not expose Plugin.BLE device objects.
/// </summary>
public sealed record DiscoveredDeviceInfo(
    Guid Id,
    DeviceType DeviceType,
    string ProductName,
    string AdvertisedName,
    int Rssi,
    DeviceCapabilities Capabilities,
    string MatchDetails
);